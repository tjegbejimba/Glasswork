using Glasswork.Core.VisualVerification;

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
    public void FromEnvironment_WhenPlannerStartPageIsSet_UsesHiddenVerificationPage()
    {
        var options = VerificationLaunchOptions.FromEnvironment(
            new Dictionary<string, string?>
            {
                [VerificationLaunchOptions.StartPageVariable] = "planner",
            });

        Assert.IsTrue(options.IsVerificationRun);
        Assert.AreEqual("planner", options.StartPage);
        Assert.IsTrue(options.SkipProtocolRegistration);
        Assert.IsTrue(options.SkipUpdateCheck);
    }
}
