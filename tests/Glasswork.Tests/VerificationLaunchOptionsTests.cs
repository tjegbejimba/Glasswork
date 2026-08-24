using Glasswork.Core.VisualVerification;
using Glasswork.Core.CalendarContext;

namespace Glasswork.Tests;

[TestClass]
public class VerificationLaunchOptionsTests
{
    [TestMethod]
    public void FromEnvironment_WhenUnset_UsesProductionDefaults()
    {
        var options = VerificationLaunchOptions.FromEnvironment(new Dictionary<string, string?>());

        Assert.IsFalse(options.IsVerificationRun);
        Assert.IsNull(options.VaultPath);
        Assert.IsNull(options.UiStatePath);
        Assert.AreEqual("main", options.InstanceKey);
        Assert.IsFalse(options.SkipProtocolRegistration);
        Assert.IsFalse(options.SkipUpdateCheck);
    }

    [TestMethod]
    public void FromEnvironment_WhenVerificationPathsAreSet_IsolatesLaunchAndSkipsSideEffects()
    {
        var env = new Dictionary<string, string?>
        {
            [VerificationLaunchOptions.VaultPathVariable] = @"C:\tmp\glasswork-vault\wiki\todo",
            [VerificationLaunchOptions.UiStatePathVariable] = @"C:\tmp\glasswork-ui-state.json",
            [VerificationLaunchOptions.InstanceKeyVariable] = "visual-123",
        };

        var options = VerificationLaunchOptions.FromEnvironment(env);

        Assert.IsTrue(options.IsVerificationRun);
        Assert.AreEqual(@"C:\tmp\glasswork-vault\wiki\todo", options.VaultPath);
        Assert.AreEqual(@"C:\tmp\glasswork-ui-state.json", options.UiStatePath);
        Assert.AreEqual("visual-123", options.InstanceKey);
        Assert.IsTrue(options.SkipProtocolRegistration);
        Assert.IsTrue(options.SkipUpdateCheck);
    }

    [TestMethod]
    public void FromEnvironment_WhenPlannerStartPageLacksIsolation_RejectsLaunch()
    {
        Assert.Throws<FormatException>(() =>
            VerificationLaunchOptions.FromEnvironment(new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.StartPageVariable] = "planner",
            }));
    }

    [TestMethod]
    public void FromEnvironment_WhenPlannerStartPageHasCompleteIsolation_UsesHiddenVerificationPage()
    {
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.StartPageVariable] = "planner",
                [VerificationLaunchOptions.VaultPathVariable] =
                    @"C:\tmp\glasswork-vault\wiki\todo",
                [VerificationLaunchOptions.UiStatePathVariable] =
                    @"C:\tmp\glasswork-ui-state.json",
                [VerificationLaunchOptions.InstanceKeyVariable] = "visual-planner",
            });

        Assert.IsTrue(options.IsVerificationRun);
        Assert.AreEqual("planner", options.StartPage);
        Assert.IsTrue(options.SkipProtocolRegistration);
        Assert.IsTrue(options.SkipUpdateCheck);
    }

    [TestMethod]
    public void FromEnvironment_WhenProductionCalendarProbeIsFullyIsolated_EnablesProbeMode()
    {
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.StartPageVariable] = "planner",
                [VerificationLaunchOptions.VaultPathVariable] =
                    @"C:\tmp\glasswork-probe\vault",
                [VerificationLaunchOptions.UiStatePathVariable] =
                    @"C:\tmp\glasswork-probe\ui-state.json",
                [VerificationLaunchOptions.InstanceKeyVariable] = "calendar-probe-123",
                [VerificationLaunchOptions.ProductionCalendarProbeVariable] = "1",
            });

        Assert.IsTrue(options.IsProductionCalendarProbe);
        Assert.IsTrue(options.IsVerificationRun);
        Assert.AreEqual("planner", options.StartPage);
        Assert.IsTrue(options.SkipProtocolRegistration);
        Assert.IsTrue(options.SkipUpdateCheck);
    }

    [TestMethod]
    public void FromEnvironment_WhenProductionCalendarProbeLacksPlannerIsolation_RejectsLaunch()
    {
        Assert.Throws<FormatException>(() =>
            VerificationLaunchOptions.FromEnvironment(
                new Dictionary<string, string?>
                {
                    [VerificationLaunchOptions.ProductionCalendarProbeVariable] = "1",
                }));
    }

    [TestMethod]
    public void FromEnvironment_WhenProductionCalendarProbeAlsoHasVisualFixture_RejectsWithoutEcho()
    {
        const string fixturePath = @"C:\tmp\do-not-echo\calendar-fixture.json";
        var exception = Assert.Throws<FormatException>(() =>
            VerificationLaunchOptions.FromEnvironment(
                new Dictionary<string, string?>
                {
                    [VerificationLaunchOptions.StartPageVariable] = "planner",
                    [VerificationLaunchOptions.VaultPathVariable] =
                        @"C:\tmp\glasswork-probe\vault",
                    [VerificationLaunchOptions.UiStatePathVariable] =
                        @"C:\tmp\glasswork-probe\ui-state.json",
                    [VerificationLaunchOptions.InstanceKeyVariable] = "calendar-probe-123",
                    [VerificationLaunchOptions.ProductionCalendarProbeVariable] = "1",
                    [VerificationLaunchOptions.CalendarContextFixturePathVariable] = fixturePath,
                }));

        Assert.DoesNotContain(fixturePath, exception.Message);
    }

    [TestMethod]
    public async Task CreateCalendarContext_VerificationWithoutFixture_FailsClosedWithoutProductionFactory()
    {
        var productionFactoryCalls = 0;
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.VaultPathVariable] =
                    @"C:\tmp\glasswork-vault\wiki\todo",
                [VerificationLaunchOptions.UiStatePathVariable] =
                    @"C:\tmp\glasswork-ui-state.json",
                [VerificationLaunchOptions.InstanceKeyVariable] = "visual-no-calendar",
            });

        var calendarContext = VerificationLaunchOptions.CreateCalendarContext(
            options,
            fixturePath: null,
            _ => throw new AssertFailedException("Fixture factory must not run."),
            () =>
            {
                productionFactoryCalls++;
                return new UnavailableCalendarContext();
            });
        var result = await calendarContext.GetTodayAsync(
            new CalendarContextRequest(
                new DateOnly(2026, 8, 20),
                TimeZoneInfo.Utc),
            CancellationToken.None);

        Assert.AreEqual(CalendarContextStatus.Unavailable, result.Status);
        Assert.AreEqual(0, productionFactoryCalls);
    }

    [TestMethod]
    public void CreateCalendarContext_ProductionCalendarProbe_UsesProductionFactory()
    {
        var productionFactoryCalls = 0;
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.StartPageVariable] = "planner",
                [VerificationLaunchOptions.VaultPathVariable] =
                    @"C:\tmp\glasswork-probe\vault",
                [VerificationLaunchOptions.UiStatePathVariable] =
                    @"C:\tmp\glasswork-probe\ui-state.json",
                [VerificationLaunchOptions.InstanceKeyVariable] = "calendar-probe-123",
                [VerificationLaunchOptions.ProductionCalendarProbeVariable] = "1",
            });
        var production = new UnavailableCalendarContext();

        var calendarContext = VerificationLaunchOptions.CreateCalendarContext(
            options,
            fixturePath: null,
            _ => throw new AssertFailedException("Fixture factory must not run."),
            () =>
            {
                productionFactoryCalls++;
                return production;
            });

        Assert.AreSame(production, calendarContext);
        Assert.AreEqual(1, productionFactoryCalls);
    }

    [TestMethod]
    public void CreateCalendarContext_SkipOnlyProductionLaunch_UsesProductionFactory()
    {
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.SkipUpdateCheckVariable] = "1",
                [VerificationLaunchOptions.SkipProtocolRegistrationVariable] = "1",
            });
        var production = new UnavailableCalendarContext();

        var calendarContext = VerificationLaunchOptions.CreateCalendarContext(
            options,
            fixturePath: null,
            _ => throw new AssertFailedException("Fixture factory must not run."),
            () => production);

        Assert.IsFalse(options.IsVerificationRun);
        Assert.AreSame(production, calendarContext);
    }
}
