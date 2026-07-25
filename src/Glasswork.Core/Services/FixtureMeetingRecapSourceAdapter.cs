using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public interface IMeetingRecapSourceAdapter
{
    MeetingRecapBatch FetchBatch(string? cursor, int maxMeetings, DateOnly runDate);
}

public sealed class FixtureMeetingRecapSourceAdapter : IMeetingRecapSourceAdapter
{
    private readonly IReadOnlyList<MeetingRecapFixture> _fixtures;

    public FixtureMeetingRecapSourceAdapter(IEnumerable<MeetingRecapFixture> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        _fixtures = fixtures
            .OrderBy(fixture => fixture.StartedAt)
            .ThenBy(fixture => fixture.StableMeetingId, StringComparer.Ordinal)
            .ToArray();
    }

    public MeetingRecapBatch FetchBatch(string? cursor, int maxMeetings, DateOnly runDate)
    {
        _ = runDate;
        if (maxMeetings <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMeetings), "maxMeetings must be greater than zero.");

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorIndex = _fixtures
                .Select((fixture, index) => (fixture, index))
                .FirstOrDefault(pair => string.Equals(pair.fixture.StableMeetingId, cursor, StringComparison.Ordinal));

            startIndex = cursorIndex.fixture is null ? 0 : cursorIndex.index + 1;
        }

        var meetings = _fixtures
            .Skip(startIndex)
            .Take(maxMeetings)
            .Select(fixture => fixture.ToRecap())
            .ToArray();

        return new MeetingRecapBatch(
            Meetings: meetings,
            NextCursor: meetings.Length == 0 ? cursor : meetings[^1].StableMeetingId,
            Diagnostics: []);
    }
}
