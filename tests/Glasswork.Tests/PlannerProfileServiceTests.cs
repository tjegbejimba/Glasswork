using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class PlannerProfileServiceTests
{
    [TestMethod]
    public void Load_MissingProfile_ReturnsSetupRequiredWithoutPersistingSuggestions()
    {
        var uiState = new RecordingUiStateService();
        var service = new PlannerProfileService(uiState);

        var result = service.Load();

        Assert.AreEqual(PlannerProfileLoadStatus.SetupRequired, result.Status);
        Assert.IsNull(result.Profile);
        Assert.AreEqual(new TimeOnly(9, 0), result.Draft.WorkStartLocal);
        Assert.AreEqual(new TimeOnly(17, 0), result.Draft.WorkEndLocal);
        Assert.AreEqual(360, result.Draft.DailyCapacityMinutes);
        Assert.AreEqual(new TimeOnly(12, 0), result.Draft.LunchStartLocal);
        Assert.AreEqual(new TimeOnly(13, 0), result.Draft.LunchEndLocal);
        Assert.AreEqual(15, result.Draft.TransitionBufferMinutes);
        Assert.IsFalse(uiState.Contains(PlannerProfileService.UiStateKey));
    }

    [TestMethod]
    public void Validate_InvalidSettings_RejectsEveryInvalidFieldWithoutClamping()
    {
        var service = new PlannerProfileService(new RecordingUiStateService());
        var draft = new PlannerProfileDraft
        {
            DailyCapacityMinutes = 35,
            WorkStartLocal = new TimeOnly(17, 0),
            WorkEndLocal = new TimeOnly(9, 0),
            LunchStartLocal = new TimeOnly(13, 0),
            LunchEndLocal = null,
            TransitionBufferMinutes = 61,
        };

        var validation = service.Validate(draft);

        Assert.IsFalse(validation.IsValid);
        CollectionAssert.AreEquivalent(
            new[]
            {
                PlannerProfileValidationError.DailyCapacity,
                PlannerProfileValidationError.WorkWindow,
                PlannerProfileValidationError.Lunch,
                PlannerProfileValidationError.TransitionBuffer,
            },
            validation.Errors.ToArray());
        Assert.AreEqual(draft, validation.Draft);
    }

    [TestMethod]
    public void Validate_NullCalendarReferences_IsRejectedBeforePersistence()
    {
        var draft = PlannerProfileService.SuggestedDraft() with
        {
            SelectedCalendarReferences = null!,
        };

        var validation = PlannerProfileService.ValidateDraft(draft);

        Assert.IsFalse(validation.IsValid);
        CollectionAssert.Contains(
            validation.Errors.ToArray(),
            PlannerProfileValidationError.CalendarReferences);
    }

    [TestMethod]
    public void SaveConfirmed_ValidDraft_PersistsVersionedEnvelopeThatLoadsReady()
    {
        var uiState = new RecordingUiStateService();
        var service = new PlannerProfileService(uiState);
        var draft = PlannerProfileService.SuggestedDraft() with
        {
            SelectedCalendarReferences = ["calendar-ref"],
        };

        var saved = service.SaveConfirmed(draft);
        var loaded = service.Load();

        Assert.AreEqual(PlannerProfileService.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.IsTrue(saved.IsConfirmed);
        Assert.AreEqual(PlannerProfileLoadStatus.Ready, loaded.Status);
        Assert.AreEqual(saved.DailyCapacityMinutes, loaded.Profile!.DailyCapacityMinutes);
        Assert.AreEqual(saved.WorkStartLocal, loaded.Profile.WorkStartLocal);
        Assert.AreEqual(saved.WorkEndLocal, loaded.Profile.WorkEndLocal);
        Assert.AreEqual(saved.LunchStartLocal, loaded.Profile.LunchStartLocal);
        Assert.AreEqual(saved.LunchEndLocal, loaded.Profile.LunchEndLocal);
        Assert.AreEqual(saved.TransitionBufferMinutes, loaded.Profile.TransitionBufferMinutes);
        CollectionAssert.AreEqual(
            new[] { "calendar-ref" },
            loaded.Profile.SelectedCalendarReferences.ToArray());
        Assert.IsTrue(uiState.Contains(PlannerProfileService.UiStateKey));
    }

    [TestMethod]
    public void Load_NewerProfile_FailsClosedWithoutOverwritingStoredValue()
    {
        const string raw = """
        {
          "schemaVersion": 99,
          "isConfirmed": true,
          "futureField": "preserve-me"
        }
        """;
        var uiState = new RecordingUiStateService();
        uiState.SeedRaw(PlannerProfileService.UiStateKey, raw);
        var service = new PlannerProfileService(uiState);

        var result = service.Load();

        Assert.AreEqual(PlannerProfileLoadStatus.UnsupportedVersion, result.Status);
        Assert.IsNull(result.Profile);
        Assert.AreEqual(
            JsonDocument.Parse(raw).RootElement.GetRawText(),
            uiState.Raw(PlannerProfileService.UiStateKey));
    }

    [TestMethod]
    public void Load_NullCalendarReferences_FailsClosedWithoutOverwritingStoredValue()
    {
        const string raw = """
        {
          "schemaVersion": 1,
          "isConfirmed": true,
          "dailyCapacityMinutes": 360,
          "workStartLocal": "09:00:00",
          "workEndLocal": "17:00:00",
          "lunchStartLocal": "12:00:00",
          "lunchEndLocal": "13:00:00",
          "transitionBufferMinutes": 15,
          "selectedCalendarReferences": null
        }
        """;
        var uiState = new RecordingUiStateService();
        uiState.SeedRaw(PlannerProfileService.UiStateKey, raw);

        var result = new PlannerProfileService(uiState).Load();

        Assert.AreEqual(PlannerProfileLoadStatus.Invalid, result.Status);
        Assert.AreEqual(
            JsonDocument.Parse(raw).RootElement.GetRawText(),
            uiState.Raw(PlannerProfileService.UiStateKey));
    }

    [TestMethod]
    public void Reset_RemovesOnlyPlannerProfile()
    {
        var uiState = new RecordingUiStateService();
        uiState.Set("unrelated", "keep");
        uiState.Set(PlannerProfileService.UiStateKey, new { schemaVersion = 1 });
        var service = new PlannerProfileService(uiState);

        service.Reset();

        Assert.IsFalse(uiState.Contains(PlannerProfileService.UiStateKey));
        Assert.IsTrue(uiState.Contains("unrelated"));
    }

    private sealed class RecordingUiStateService : IUiStateService
    {
        private readonly Dictionary<string, JsonElement> _state = [];

        public bool Contains(string key) => _state.ContainsKey(key);

        public void SeedRaw(string key, string json) =>
            _state[key] = JsonDocument.Parse(json).RootElement.Clone();

        public string Raw(string key) => _state[key].GetRawText();

        public T? Get<T>(string key) =>
            _state.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;

        public void Set<T>(string key, T value) =>
            _state[key] = JsonSerializer.SerializeToElement(value);

        public void Remove(string key) => _state.Remove(key);

        public void Save() { }

        public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes) { }
    }
}
