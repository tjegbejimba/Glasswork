using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class AdoTaskReconciliationToolTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 4, 30, 0, TimeSpan.Zero);

    private string _vaultRoot = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-ado-reconciliation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        _tools = new GlassworkTools(new VaultContext(_vaultRoot), clock: () => Now);
        _vault = new VaultService(Path.Combine(_vaultRoot, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    [DataRow(GlassworkTask.Statuses.Todo)]
    [DataRow(GlassworkTask.Statuses.InProgress)]
    [DataRow(GlassworkTask.Statuses.Blocked)]
    public void ReconcileAdoTask_RemovedCancelsMatchingActiveTask(string status)
    {
        const int adoId = 12345678;
        var task = new GlassworkTask
        {
            Id = "removed-from-ado",
            Title = "Removed from ADO",
            Status = status,
            Size = "future_bucket",
            MyDay = DateTime.Today,
            Description =
                $"ADO {adoId} - https://msazure.visualstudio.com/One/_workitems/edit/{adoId}",
            BlockedReason = status == GlassworkTask.Statuses.Blocked ? "Waiting" : null,
            BlockedAt = status == GlassworkTask.Statuses.Blocked ? Now.AddDays(-1) : null,
            BlockedFromStatus = status == GlassworkTask.Statuses.Blocked
                ? GlassworkTask.Statuses.InProgress
                : null,
            Subtasks =
            [
                new SubTask { Text = "Future step", Size = "next_bucket" },
            ],
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            $"ado-removed-{status}",
            task.ResourceRevision));

        Assert.AreEqual("cancelled", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("azure-devops", result.RootElement.GetProperty("source").GetString());
        Assert.AreEqual("Removed", result.RootElement.GetProperty("authoritative_state").GetString());
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            "ADO work item removed",
            result.RootElement.GetProperty("cancellation_reason").GetString());

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, persisted.Status);
        Assert.AreEqual(Now, persisted.CancelledAt);
        Assert.AreEqual("ADO work item removed", persisted.CancellationReason);
        Assert.IsNull(persisted.MyDay);
        Assert.IsNull(persisted.BlockedReason);
        Assert.IsNull(persisted.BlockedAt);
        Assert.IsNull(persisted.BlockedFromStatus);
        Assert.AreEqual("future_bucket", persisted.Size);
        Assert.AreEqual("next_bucket", persisted.Subtasks.Single().Size);
    }

    [TestMethod]
    [DataRow("Active")]
    [DataRow("In Progress")]
    [DataRow("In Review")]
    public void ReconcileAdoTask_ResumedActiveStatesRestoreCancelledTaskDirectlyToInProgress(
        string authoritativeState)
    {
        const int adoId = 23456789;
        var task = new GlassworkTask
        {
            Id = "resumed-in-ado",
            Title = "Resumed in ADO",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = Now.AddDays(-1),
            CancellationReason = "ADO work item removed",
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            authoritativeState,
            $"ado-active-{authoritativeState}",
            task.ResourceRevision));

        Assert.AreEqual("restored", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual(
            "doing",
            result.RootElement.GetProperty("status").GetString());
        Assert.IsFalse(result.RootElement.TryGetProperty("cancelled_at", out _));
        Assert.IsFalse(result.RootElement.TryGetProperty("cancellation_reason", out _));

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, persisted.Status);
        Assert.IsNull(persisted.CancelledAt);
        Assert.IsNull(persisted.CancellationReason);
    }

    [TestMethod]
    public void ReconcileAdoTask_RemovedNeverReclassifiesDoneTask()
    {
        const int adoId = 45678901;
        var completedAt = DateTime.Today.AddDays(-2);
        var task = new GlassworkTask
        {
            Id = "done-wins",
            Title = "Done wins",
            Status = GlassworkTask.Statuses.Done,
            CompletedAt = completedAt,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-done-wins-1",
            task.ResourceRevision));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("done", result.RootElement.GetProperty("status").GetString());
        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Done, persisted.Status);
        Assert.AreEqual(completedAt, persisted.CompletedAt);
        Assert.IsNull(persisted.CancelledAt);
        Assert.IsNull(persisted.CancellationReason);
    }

    [TestMethod]
    [DataRow("New")]
    [DataRow("To Do")]
    [DataRow("Committed")]
    [DataRow("Resolved")]
    [DataRow("Done")]
    [DataRow("Closed")]
    [DataRow("active")]
    [DataRow("removed")]
    [DataRow("Removed ")]
    public void ReconcileAdoTask_NonAllowlistedStatesNeverRestoreCancelledTask(
        string authoritativeState)
    {
        const int adoId = 56789012;
        var cancelledAt = Now.AddDays(-3);
        var task = new GlassworkTask
        {
            Id = "exact-state-allowlist",
            Title = "Exact state allowlist",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = cancelledAt,
            CancellationReason = "ADO work item removed",
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            authoritativeState,
            $"ado-non-restore-{authoritativeState}",
            task.ResourceRevision));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("status").GetString());
        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, persisted.Status);
        Assert.AreEqual(cancelledAt, persisted.CancelledAt);
        Assert.AreEqual("ADO work item removed", persisted.CancellationReason);
    }

    [TestMethod]
    [DataRow("removed")]
    [DataRow("Removed ")]
    [DataRow("Deleted")]
    public void ReconcileAdoTask_OnlyExactRemovedCancelsActiveTask(
        string authoritativeState)
    {
        const int adoId = 61234567;
        var task = new GlassworkTask
        {
            Id = "exact-removed-state",
            Title = "Exact Removed state",
            Status = GlassworkTask.Statuses.Todo,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            authoritativeState,
            $"ado-non-removed-{authoritativeState}",
            task.ResourceRevision));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("todo", result.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(task.Id)!.Status);
    }

    [TestMethod]
    public void ReconcileAdoTask_RejectsTaskWhoseImportedAdoIdentityDoesNotMatch()
    {
        const int importedAdoId = 67890123;
        var task = new GlassworkTask
        {
            Id = "identity-mismatch",
            Title = "Identity mismatch",
            Status = GlassworkTask.Statuses.Todo,
            AdoLink = importedAdoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            importedAdoId + 1,
            "Removed",
            "ado-identity-mismatch-1",
            task.ResourceRevision));

        Assert.AreEqual(
            "validation_error",
            result.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(task.Id)!.Status);
    }

    [TestMethod]
    public void ReconcileAdoTask_ExactReplayReturnsTheRecordedTransition()
    {
        const int adoId = 78901234;
        var task = new GlassworkTask
        {
            Id = "replay-reconciliation",
            Title = "Replay reconciliation",
            Status = GlassworkTask.Statuses.Todo,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        var first = _tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-replay-1",
            task.ResourceRevision);
        var replay = _tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-replay-1",
            task.ResourceRevision);

        Assert.AreEqual(first, replay);
        using var result = JsonDocument.Parse(replay);
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("action").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Cancelled, _vault.Load(task.Id)!.Status);
    }

    [TestMethod]
    public void ReconcileAdoTask_ExactReplaySurvivesLaterTaskRemoval()
    {
        const int adoId = 80123456;
        var task = new GlassworkTask
        {
            Id = "replay-after-removal",
            Title = "Replay after removal",
            Status = GlassworkTask.Statuses.Todo,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        var first = _tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-replay-after-removal-1",
            task.ResourceRevision);
        File.Delete(Path.Combine(_vault.VaultPath, $"{task.Id}.md"));

        var replay = _tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-replay-after-removal-1",
            task.ResourceRevision);

        Assert.AreEqual(first, replay);
    }

    [TestMethod]
    public void ReconcileAdoTask_RequiresMutationIdAndCurrentResourceRevision()
    {
        const int adoId = 89012345;
        var task = new GlassworkTask
        {
            Id = "revision-guarded-reconciliation",
            Title = "Revision guarded reconciliation",
            Status = GlassworkTask.Statuses.Todo,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var missing = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            null,
            task.ResourceRevision));
        Assert.AreEqual(
            "precondition_required",
            missing.RootElement.GetProperty("error").GetString());

        var staleRevision = task.ResourceRevision;
        task.Title = "Changed elsewhere";
        _vault.Save(task);
        using var stale = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Removed",
            "ado-stale-revision-1",
            staleRevision));
        Assert.AreEqual("conflict", stale.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(GlassworkTask.Statuses.Todo, _vault.Load(task.Id)!.Status);
    }

    [TestMethod]
    public void ReconcileAdoTask_AuthoritativeStandardKindAndParentRefreshImportMetadata()
    {
        const int adoId = 90123456;
        var task = new GlassworkTask
        {
            Id = "imported-feature",
            Title = "Imported feature",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Task,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "New",
            "ado-refresh-feature-1",
            task.ResourceRevision,
            ado_work_item_type: "Feature",
            ado_parent_work_item_id: 76543210,
            update_ado_parent: true));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(GlassworkTask.Types.Parent, persisted.Type);
        Assert.AreEqual("Feature", persisted.SourceKind);
        Assert.AreEqual("76543210", persisted.Parent);
    }

    [TestMethod]
    [DataRow("Epic", GlassworkTask.Types.Parent)]
    [DataRow("Feature", GlassworkTask.Types.Parent)]
    [DataRow("Product Backlog Item", GlassworkTask.Types.Parent)]
    [DataRow("User Story", GlassworkTask.Types.Parent)]
    [DataRow("Task", GlassworkTask.Types.Task)]
    [DataRow("Bug", GlassworkTask.Types.Bug)]
    public void ReconcileAdoTask_AuthoritativeStandardKindMapsBehavior(
        string sourceKind,
        string expectedType)
    {
        const int adoId = 93456789;
        var task = new GlassworkTask
        {
            Id = "standard-kind",
            Title = "Standard kind",
            Status = GlassworkTask.Statuses.Todo,
            Type = expectedType == GlassworkTask.Types.Task
                ? GlassworkTask.Types.Parent
                : GlassworkTask.Types.Task,
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        _tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "New",
            $"ado-standard-{expectedType}-{sourceKind}",
            task.ResourceRevision,
            ado_work_item_type: sourceKind);

        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual(expectedType, persisted.Type);
        Assert.AreEqual(sourceKind, persisted.SourceKind);
        Assert.IsNull(persisted.Parent);
    }

    [TestMethod]
    public void ReconcileAdoTask_CustomKindPreservesExplicitBehaviorAndCanonicalizesParentLater()
    {
        const int childAdoId = 91234567;
        const int parentAdoId = 92345678;
        var child = new GlassworkTask
        {
            Id = "custom-import",
            Title = "Custom import",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Bug,
            AdoLink = childAdoId,
            Parent = parentAdoId.ToString(),
        };
        _vault.Save(child);
        child = _vault.Load(child.Id)!;

        _tools.ReconcileAdoTask(
            child.Id,
            childAdoId,
            "Active",
            "ado-refresh-custom-1",
            child.ResourceRevision,
            ado_work_item_type: "Customer Escalation",
            ado_parent_work_item_id: parentAdoId,
            update_ado_parent: true);

        var unresolved = _vault.Load(child.Id)!;
        Assert.AreEqual(GlassworkTask.Types.Bug, unresolved.Type);
        Assert.AreEqual("Customer Escalation", unresolved.SourceKind);
        Assert.AreEqual(parentAdoId.ToString(), unresolved.Parent);

        var parent = new GlassworkTask
        {
            Id = "local-portfolio-parent",
            Title = "Portfolio parent",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Parent,
            SourceKind = "Portfolio Item",
            AdoLink = parentAdoId,
        };
        _vault.Save(parent);
        unresolved = _vault.Load(child.Id)!;

        _tools.ReconcileAdoTask(
            child.Id,
            childAdoId,
            "Active",
            "ado-refresh-custom-2",
            unresolved.ResourceRevision,
            ado_work_item_type: "Customer Escalation",
            ado_parent_work_item_id: parentAdoId,
            update_ado_parent: true);

        var resolved = _vault.Load(child.Id)!;
        Assert.AreEqual("local-portfolio-parent", resolved.Parent);
        Assert.AreEqual(GlassworkTask.Types.Bug, resolved.Type);
    }

    [TestMethod]
    public void ReconcileAdoTask_TypeOnlyRefreshPreservesParentAndDoesNotReportRestore()
    {
        const int adoId = 94567890;
        var task = new GlassworkTask
        {
            Id = "metadata-only-refresh",
            Title = "Metadata only refresh",
            Status = GlassworkTask.Statuses.InProgress,
            Type = GlassworkTask.Types.Task,
            Parent = "existing-parent",
            AdoLink = adoId,
        };
        _vault.Save(task);
        task = _vault.Load(task.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            task.Id,
            adoId,
            "Active",
            "ado-metadata-only-1",
            task.ResourceRevision,
            ado_work_item_type: "Customer Escalation"));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        var persisted = _vault.Load(task.Id)!;
        Assert.AreEqual("existing-parent", persisted.Parent);
        Assert.AreEqual("Customer Escalation", persisted.SourceKind);
        Assert.AreEqual(GlassworkTask.Types.Task, persisted.Type);
    }

    [TestMethod]
    public void ReconcileAdoTask_ParentReclassificationCanonicalizesExistingExternalChild()
    {
        const int parentAdoId = 95678901;
        var child = new GlassworkTask
        {
            Id = "a-external-child",
            Title = "External child",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Task,
            Parent = parentAdoId.ToString(),
        };
        var parent = new GlassworkTask
        {
            Id = "z-reconciled-parent",
            Title = "Reconciled parent",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Task,
            AdoLink = parentAdoId,
        };
        _vault.Save(child);
        _vault.Save(parent);
        parent = _vault.Load(parent.Id)!;

        using var result = JsonDocument.Parse(_tools.ReconcileAdoTask(
            parent.Id,
            parentAdoId,
            "New",
            "ado-parent-reclassification",
            parent.ResourceRevision,
            ado_work_item_type: "Feature"));

        Assert.AreEqual("unchanged", result.RootElement.GetProperty("action").GetString());
        var refreshedVault = new VaultService(_vault.VaultPath);
        Assert.AreEqual(
            parent.Id,
            refreshedVault.Load(child.Id)!.Parent,
            result.RootElement.ToString());
    }
}
