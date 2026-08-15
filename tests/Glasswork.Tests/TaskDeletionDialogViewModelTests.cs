using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

[TestClass]
public sealed class TaskDeletionDialogViewModelTests
{
    [TestMethod]
    public void CanDelete_RequiresTheExactTitleAndExplicitCascadeWhenDescendantsExist()
    {
        var root = new TaskDeletionTask("root", "Exact Root Title", "rr1-root");
        var child = new TaskDeletionTask("child", "Child", "rr1-child");
        var viewModel = new TaskDeletionDialogViewModel(new TaskDeletionPreflight(
            root,
            [child],
            [],
            [],
            [],
            "dpr1-dialog"));

        viewModel.ConfirmationTitle = "exact root title";
        viewModel.CascadeChildren = true;
        Assert.IsFalse(viewModel.CanDelete);

        viewModel.ConfirmationTitle = "Exact Root Title";
        viewModel.CascadeChildren = false;
        Assert.IsFalse(viewModel.CanDelete);

        viewModel.CascadeChildren = true;
        Assert.IsTrue(viewModel.CanDelete);
    }

    [TestMethod]
    public void ImpactSummary_ReportsDescendantsArtifactsAndBacklinkPages()
    {
        var root = new TaskDeletionTask("root", "Root", "rr1-root");
        var viewModel = new TaskDeletionDialogViewModel(new TaskDeletionPreflight(
            root,
            [new TaskDeletionTask("child", "Child", "rr1-child")],
            [
                new TaskDeletionArtifact("root", "wiki/todo/root.artifacts/one.md"),
                new TaskDeletionArtifact("child", "wiki/todo/child.artifacts/two.png"),
            ],
            [new TaskDeletionBacklinkPage("wiki/concepts/page.md", 2)],
            [
                "wiki/todo/child.artifacts",
                "wiki/todo/root.artifacts",
            ],
            "dpr1-impact"));

        Assert.AreEqual(
            "2 Tasks, 2 Artifacts, and 1 vault page will be permanently affected.",
            viewModel.ImpactSummary);
        Assert.AreEqual("child", viewModel.DescendantIds);
        Assert.IsTrue(viewModel.RequiresCascade);
    }
}
