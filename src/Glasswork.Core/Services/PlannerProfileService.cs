using System.Text.Json;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class PlannerProfileService
{
    public const string UiStateKey = "planner.profile";
    public const int CurrentSchemaVersion = 1;

    private readonly IUiStateService _uiState;

    public PlannerProfileService(IUiStateService uiState)
    {
        _uiState = uiState ?? throw new ArgumentNullException(nameof(uiState));
    }

    public PlannerProfileLoadResult Load()
    {
        var stored = _uiState.Get<JsonElement>(UiStateKey);
        if (stored.ValueKind == JsonValueKind.Undefined)
        {
            return new PlannerProfileLoadResult(
                PlannerProfileLoadStatus.SetupRequired,
                SuggestedDraft(),
                null,
                []);
        }

        if (stored.ValueKind != JsonValueKind.Object
            || !stored.TryGetProperty("schemaVersion", out var schemaVersion)
            || !schemaVersion.TryGetInt32(out var version))
        {
            return Invalid();
        }

        if (version > CurrentSchemaVersion)
        {
            return new PlannerProfileLoadResult(
                PlannerProfileLoadStatus.UnsupportedVersion,
                SuggestedDraft(),
                null,
                ["Planner Profile was written by a newer Glasswork version."]);
        }

        if (version != CurrentSchemaVersion)
            return Invalid();

        try
        {
            var profile = stored.Deserialize<PlannerProfile>();
            if (profile is null
                || !profile.IsConfirmed
                || profile.SelectedCalendarReferences is null)
                return Invalid();

            var draft = ToDraft(profile);
            var validation = Validate(draft);
            return validation.IsValid
                ? new PlannerProfileLoadResult(
                    PlannerProfileLoadStatus.Ready,
                    draft,
                    profile,
                    [])
                : Invalid();
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    public PlannerProfileValidation Validate(PlannerProfileDraft draft) => ValidateDraft(draft);

    public static PlannerProfileValidation ValidateDraft(PlannerProfileDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<PlannerProfileValidationError>();
        if (draft.DailyCapacityMinutes is < 30 or > 720
            || draft.DailyCapacityMinutes % 30 != 0)
        {
            errors.Add(PlannerProfileValidationError.DailyCapacity);
        }

        if (draft.WorkStartLocal >= draft.WorkEndLocal)
            errors.Add(PlannerProfileValidationError.WorkWindow);

        if (draft.LunchStartLocal.HasValue != draft.LunchEndLocal.HasValue
            || draft.LunchStartLocal.HasValue
            && draft.LunchStartLocal.Value >= draft.LunchEndLocal!.Value)
        {
            errors.Add(PlannerProfileValidationError.Lunch);
        }

        if (draft.TransitionBufferMinutes is < 0 or > 60)
            errors.Add(PlannerProfileValidationError.TransitionBuffer);

        if (draft.SelectedCalendarReferences is null)
            errors.Add(PlannerProfileValidationError.CalendarReferences);

        return new PlannerProfileValidation(draft, errors);
    }

    public PlannerProfile SaveConfirmed(PlannerProfileDraft draft)
    {
        var validation = Validate(draft);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Planner Profile is invalid: {string.Join(", ", validation.Errors)}.",
                nameof(draft));
        }

        var profile = new PlannerProfile
        {
            SchemaVersion = CurrentSchemaVersion,
            IsConfirmed = true,
            DailyCapacityMinutes = draft.DailyCapacityMinutes,
            WorkStartLocal = draft.WorkStartLocal,
            WorkEndLocal = draft.WorkEndLocal,
            LunchStartLocal = draft.LunchStartLocal,
            LunchEndLocal = draft.LunchEndLocal,
            TransitionBufferMinutes = draft.TransitionBufferMinutes,
            SelectedCalendarReferences = [.. draft.SelectedCalendarReferences],
        };
        _uiState.Set(UiStateKey, profile);
        return profile;
    }

    public void Reset() => _uiState.Remove(UiStateKey);

    public static PlannerProfileDraft SuggestedDraft() => new()
    {
        DailyCapacityMinutes = 360,
        WorkStartLocal = new TimeOnly(9, 0),
        WorkEndLocal = new TimeOnly(17, 0),
        LunchStartLocal = new TimeOnly(12, 0),
        LunchEndLocal = new TimeOnly(13, 0),
        TransitionBufferMinutes = 15,
    };

    private static PlannerProfileDraft ToDraft(PlannerProfile profile) => new()
    {
        DailyCapacityMinutes = profile.DailyCapacityMinutes,
        WorkStartLocal = profile.WorkStartLocal,
        WorkEndLocal = profile.WorkEndLocal,
        LunchStartLocal = profile.LunchStartLocal,
        LunchEndLocal = profile.LunchEndLocal,
        TransitionBufferMinutes = profile.TransitionBufferMinutes,
        SelectedCalendarReferences = [.. profile.SelectedCalendarReferences],
    };

    private static PlannerProfileLoadResult Invalid() =>
        new(
            PlannerProfileLoadStatus.Invalid,
            SuggestedDraft(),
            null,
            ["Planner Profile is invalid and was preserved."]);
}
