using System.Text.Json.Serialization;

namespace Glasswork.Core.Models;

public sealed record PlannerProfileDraft
{
    public int DailyCapacityMinutes { get; init; }
    public TimeOnly WorkStartLocal { get; init; }
    public TimeOnly WorkEndLocal { get; init; }
    public TimeOnly? LunchStartLocal { get; init; }
    public TimeOnly? LunchEndLocal { get; init; }
    public int TransitionBufferMinutes { get; init; }
    public IReadOnlyList<string> SelectedCalendarReferences { get; init; } = [];
}

public sealed record PlannerProfile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("isConfirmed")]
    public bool IsConfirmed { get; init; }

    [JsonPropertyName("dailyCapacityMinutes")]
    public int DailyCapacityMinutes { get; init; }

    [JsonPropertyName("workStartLocal")]
    public TimeOnly WorkStartLocal { get; init; }

    [JsonPropertyName("workEndLocal")]
    public TimeOnly WorkEndLocal { get; init; }

    [JsonPropertyName("lunchStartLocal")]
    public TimeOnly? LunchStartLocal { get; init; }

    [JsonPropertyName("lunchEndLocal")]
    public TimeOnly? LunchEndLocal { get; init; }

    [JsonPropertyName("transitionBufferMinutes")]
    public int TransitionBufferMinutes { get; init; }

    [JsonPropertyName("selectedCalendarReferences")]
    public IReadOnlyList<string> SelectedCalendarReferences { get; init; } = [];
}

public enum PlannerProfileLoadStatus
{
    SetupRequired,
    Ready,
    Invalid,
    UnsupportedVersion,
}

public sealed record PlannerProfileLoadResult(
    PlannerProfileLoadStatus Status,
    PlannerProfileDraft Draft,
    PlannerProfile? Profile,
    IReadOnlyList<string> Errors);

public enum PlannerProfileValidationError
{
    DailyCapacity,
    WorkWindow,
    Lunch,
    TransitionBuffer,
    CalendarReferences,
}

public sealed record PlannerProfileValidation(
    PlannerProfileDraft Draft,
    IReadOnlyList<PlannerProfileValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
