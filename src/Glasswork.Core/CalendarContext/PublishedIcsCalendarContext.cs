using System.Security.Cryptography;
using System.Text;

namespace Glasswork.Core.CalendarContext;

public sealed class PublishedIcsCalendarContext : ICalendarContext
{
    public const int ConfigurationSchemaVersion =
        CalendarContextPersistenceContract.ConfigurationSchemaVersion;
    public const int SnapshotSchemaVersion =
        CalendarContextPersistenceContract.SnapshotSchemaVersion;
    public const int NormalizationVersion =
        CalendarContextPersistenceContract.NormalizationVersion;

    private static readonly IReadOnlyList<CalendarContextAction> ConnectedActions =
        [CalendarContextAction.Refresh, CalendarContextAction.Disconnect];
    private static readonly IReadOnlyList<CalendarContextAction> SetupActions =
        [CalendarContextAction.Connect];
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(15);

    private readonly ICalendarFeedTransport _transport;
    private readonly ICalendarContextStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _refreshGate = new();
    private InFlightRefresh? _inFlight;
    private long _generation;

    public PublishedIcsCalendarContext(
        ICalendarFeedTransport transport,
        ICalendarContextStore store,
        Func<DateTimeOffset>? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<CalendarContextResult> GetTodayAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CalendarContextStoreRead<CalendarContextConfiguration> configuration;
        CalendarContextStoreRead<CalendarContextSnapshot> stored;
        lock (_refreshGate)
        {
            configuration = _store.ReadConfiguration();
            if (configuration.Status == CalendarContextStoreStatus.Missing)
                return Task.FromResult(SetupRequired());
            if (configuration.Status != CalendarContextStoreStatus.Ready
                || configuration.Value is null)
            {
                return Task.FromResult(StoreFailure(configuration.Status));
            }

            stored = _store.ReadSnapshot();
            if (stored.Status is not (
                    CalendarContextStoreStatus.Missing
                    or CalendarContextStoreStatus.Ready))
            {
                return Task.FromResult(StoreFailure(stored.Status));
            }

            if (stored is { Status: CalendarContextStoreStatus.Ready, Value: { } snapshot })
            {
                if (!IsQualified(snapshot, configuration.Value, request))
                {
                    _store.DeleteSnapshot();
                }
                else if (!request.ForceRefresh && IsFresh(snapshot))
                {
                    return Task.FromResult(Current(snapshot));
                }
            }
        }

        return RefreshCoalescedAsync(configuration.Value, request, cancellationToken);
    }

    public async Task<CalendarContextResult> ConnectAsync(
        CalendarContextConnection connection,
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection.Provider != CalendarContextProviderKind.PublishedIcs
            || !CalendarEndpointPolicy.TryValidate(connection.Secret, out var endpoint))
        {
            return new CalendarContextResult(
                CalendarContextStatus.SetupRequired,
                null,
                SetupActions,
                new CalendarContextDiagnostic("unsafe_endpoint", "published_ics"));
        }

        var fingerprint = CalendarContextPersistenceContract.SourceFingerprint(endpoint);
        CalendarContextStoreRead<CalendarContextConfiguration> prior;
        long generation;
        lock (_refreshGate)
        {
            prior = _store.ReadConfiguration();
            if (prior.Status is not (
                    CalendarContextStoreStatus.Missing
                    or CalendarContextStoreStatus.Ready))
            {
                return StoreFailure(prior.Status);
            }

            var stored = _store.ReadSnapshot();
            if (stored.Status is not (
                    CalendarContextStoreStatus.Missing
                    or CalendarContextStoreStatus.Ready))
            {
                return StoreFailure(stored.Status);
            }

            generation = AdvanceGenerationLocked();
        }

        var configuration = new CalendarContextConfiguration(
            ConfigurationSchemaVersion,
            CalendarContextProviderKind.PublishedIcs,
            endpoint.AbsoluteUri,
            fingerprint);
        var result = await RefreshAsync(
            configuration,
            request,
            cancellationToken,
            persistSnapshot: false,
            generation);
        EnsureCurrentGeneration(generation, cancellationToken);
        if (result is { Status: CalendarContextStatus.Current, Snapshot: { } snapshot })
        {
            lock (_refreshGate)
            {
                EnsureCurrentGenerationLocked(generation, cancellationToken);
                _store.WriteConfiguration(configuration);
                if (prior.Status != CalendarContextStoreStatus.Ready
                    || prior.Value is null
                    || !string.Equals(
                        prior.Value.SourceFingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    _store.DeleteSnapshot();
                }
                _store.WriteSnapshot(snapshot);
            }
            return result;
        }

        return prior.Status == CalendarContextStoreStatus.Missing
            ? result with { Actions = SetupActions }
            : result;
    }

    public Task<CalendarContextResult> DisconnectAsync(
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        lock (_refreshGate)
        {
            AdvanceGenerationLocked();
            _store.DeleteConfiguration();
            _store.DeleteSnapshot();
        }
        return Task.FromResult(SetupRequired());
    }

    public Task<CalendarContextResult> ResetAsync(
        CalendarContextResetConfirmation confirmation,
        CalendarContextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var scope = CreateResetScope();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(scope.Token),
                Encoding.UTF8.GetBytes(confirmation.ScopeToken)))
        {
            return Task.FromResult(ProtectedStoreRecovery(
                CalendarContextStoreStatus.Corrupt));
        }

        lock (_refreshGate)
        {
            AdvanceGenerationLocked();
            _store.DeleteConfiguration();
            _store.DeleteSnapshot();
        }
        return Task.FromResult(SetupRequired());
    }

    private Task<CalendarContextResult> RefreshCoalescedAsync(
        CalendarContextConfiguration configuration,
        CalendarContextRequest request,
        CancellationToken callerCancellationToken)
    {
        RefreshKey key;
        InFlightRefresh current;
        lock (_refreshGate)
        {
            var latestConfiguration = _store.ReadConfiguration();
            if (latestConfiguration.Status == CalendarContextStoreStatus.Missing)
                return Task.FromResult(SetupRequired());
            if (latestConfiguration.Status != CalendarContextStoreStatus.Ready
                || latestConfiguration.Value is null)
            {
                return Task.FromResult(
                    StoreFailure(latestConfiguration.Status));
            }
            configuration = latestConfiguration.Value;
            key = new RefreshKey(
                request.Day,
                request.TimeZone.Id,
                configuration.SourceFingerprint);

            if (_inFlight is { } existing)
            {
                if (!existing.Task.IsCompleted && existing.Key == key)
                    return AwaitSharedAsync(existing, callerCancellationToken);

                CancelInFlightLocked();
            }

            var generation = ++_generation;
            var cancellation = new CancellationTokenSource();
            var refresh = RefreshAsync(
                configuration,
                request,
                cancellation.Token,
                persistSnapshot: true,
                generation);
            current = new InFlightRefresh(key, refresh, cancellation, generation);
            _inFlight = current;
        }

        return AwaitSharedAsync(current, callerCancellationToken);
    }

    private async Task<CalendarContextResult> RefreshAsync(
        CalendarContextConfiguration configuration,
        CalendarContextRequest request,
        CancellationToken cancellationToken,
        bool persistSnapshot,
        long generation)
    {
        if (configuration.SchemaVersion > ConfigurationSchemaVersion
            || configuration.Provider != CalendarContextProviderKind.PublishedIcs
            || !CalendarEndpointPolicy.TryValidate(configuration.Secret, out var endpoint))
        {
            return ProtectedStoreRecovery(CalendarContextStoreStatus.UnsupportedVersion);
        }

        try
        {
            var response = await _transport.GetAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            EnsureCurrentGeneration(generation, cancellationToken);
            if (response.StatusCode is < 200 or >= 300)
            {
                return FailureWithStoredSnapshot(
                    configuration,
                    request,
                    "http_failure",
                    generation,
                    response.StatusCode / 100);
            }

            var snapshot = PublishedIcsNormalizer.Normalize(
                response.Content,
                request,
                configuration.SourceFingerprint,
                _clock());
            if (persistSnapshot)
            {
                lock (_refreshGate)
                {
                    EnsureCurrentGenerationLocked(generation, cancellationToken);
                    _store.WriteSnapshot(snapshot);
                }
            }
            return new CalendarContextResult(
                CalendarContextStatus.Current,
                snapshot,
                ConnectedActions,
                new CalendarContextDiagnostic(
                    "current",
                    "published_ics",
                    IntervalCount: snapshot.Intervals.Count));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailureWithStoredSnapshot(
                configuration,
                request,
                "timeout",
                generation);
        }
        catch (CalendarFeedException ex)
        {
            return FailureWithStoredSnapshot(
                configuration,
                request,
                ex.Code,
                generation,
                ex.HttpStatusClass);
        }
        catch (CalendarContextIncompleteException)
        {
            return FailureWithStoredSnapshot(
                configuration,
                request,
                "incomplete",
                generation,
                failureStatus: CalendarContextStatus.Incomplete);
        }
    }

    private CalendarContextResult FailureWithStoredSnapshot(
        CalendarContextConfiguration configuration,
        CalendarContextRequest request,
        string code,
        long generation,
        int? httpStatusClass = null,
        CalendarContextStatus failureStatus = CalendarContextStatus.TransientFailure)
    {
        CalendarContextSnapshot? snapshot;
        lock (_refreshGate)
        {
            EnsureCurrentGenerationLocked(generation, CancellationToken.None);
            var stored = _store.ReadSnapshot();
            snapshot = stored.Status == CalendarContextStoreStatus.Ready
                && stored.Value is { IsComplete: true } value
                && value.Day == request.Day
                && string.Equals(
                    value.SourceFingerprint,
                    configuration.SourceFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    value.TimeZoneId,
                    request.TimeZone.Id,
                    StringComparison.Ordinal)
                    ? value
                    : null;
        }
        return new CalendarContextResult(
            snapshot is null
                ? failureStatus
                : CalendarContextStatus.PossiblyStale,
            snapshot,
            ConnectedActions,
            new CalendarContextDiagnostic(
                code,
                "published_ics",
                httpStatusClass,
                snapshot?.Intervals.Count));
    }

    private bool IsFresh(CalendarContextSnapshot snapshot)
    {
        var age = _clock() - snapshot.FetchedAt;
        return age >= TimeSpan.Zero && age <= FreshnessWindow;
    }

    private static bool IsQualified(
        CalendarContextSnapshot snapshot,
        CalendarContextConfiguration configuration,
        CalendarContextRequest request) =>
        snapshot.SchemaVersion == SnapshotSchemaVersion
        && snapshot.NormalizationVersion == NormalizationVersion
        && snapshot.IsComplete
        && snapshot.Day == request.Day
        && string.Equals(snapshot.TimeZoneId, request.TimeZone.Id, StringComparison.Ordinal)
        && string.Equals(
            snapshot.SourceFingerprint,
            configuration.SourceFingerprint,
            StringComparison.Ordinal);

    private static CalendarContextResult Current(CalendarContextSnapshot snapshot) =>
        new(
            CalendarContextStatus.Current,
            snapshot,
            ConnectedActions,
            new CalendarContextDiagnostic(
                "current",
                "published_ics",
                IntervalCount: snapshot.Intervals.Count));

    private long AdvanceGenerationLocked()
    {
        CancelInFlightLocked();
        return ++_generation;
    }

    private void CancelInFlightLocked()
    {
        if (_inFlight is not { } existing)
            return;

        existing.Cancellation.Cancel();
        if (existing.Task.IsCompleted)
            existing.Cancellation.Dispose();
        else
            _ = existing.Task.ContinueWith(
                _ => existing.Cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        _inFlight = null;
    }

    private static async Task<CalendarContextResult> AwaitSharedAsync(
        InFlightRefresh refresh,
        CancellationToken callerCancellationToken)
    {
        return await refresh.Task.WaitAsync(callerCancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureCurrentGeneration(
        long generation,
        CancellationToken cancellationToken)
    {
        lock (_refreshGate)
            EnsureCurrentGenerationLocked(generation, cancellationToken);
    }

    private void EnsureCurrentGenerationLocked(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _generation)
            throw new OperationCanceledException(cancellationToken);
    }

    private static CalendarContextResult SetupRequired() =>
        new(CalendarContextStatus.SetupRequired, null, SetupActions);

    private static CalendarContextResult ProtectedStoreRecovery(
        CalendarContextStoreStatus status) =>
        new(
            CalendarContextStatus.ProtectedStoreRecovery,
            null,
            [CalendarContextAction.Reset],
            new CalendarContextDiagnostic(
                status switch
                {
                    CalendarContextStoreStatus.UnsupportedVersion => "protected_store_newer",
                    CalendarContextStoreStatus.Undecryptable => "protected_store_undecryptable",
                    _ => "protected_store_corrupt",
                },
                "published_ics"),
            CreateResetScope());

    private static CalendarContextResult StoreFailure(
        CalendarContextStoreStatus status) =>
        status == CalendarContextStoreStatus.TransientFailure
            ? new CalendarContextResult(
                CalendarContextStatus.TransientFailure,
                null,
                [CalendarContextAction.Refresh],
                new CalendarContextDiagnostic(
                    "protected_store_transient",
                    "published_ics"))
            : ProtectedStoreRecovery(status);

    private static CalendarContextResetScope CreateResetScope() =>
        new(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("calendar-context:configuration,snapshot"))),
            ["Published calendar connection", "Current-day calendar snapshot"]);

    private sealed record InFlightRefresh(
        RefreshKey Key,
        Task<CalendarContextResult> Task,
        CancellationTokenSource Cancellation,
        long Generation);

    private readonly record struct RefreshKey(
        DateOnly Day,
        string TimeZoneId,
        string SourceFingerprint);
}
