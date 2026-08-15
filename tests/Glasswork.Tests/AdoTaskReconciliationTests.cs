using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class AdoTaskReconciliationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 4, 30, 0, TimeSpan.Zero);

    private string _todoPath = null!;
    private SelfWriteCoordinator _selfWrites = null!;
    private VaultService _vault = null!;
    private ResourceMutationService _mutations = null!;

    [TestInitialize]
    public void Setup()
    {
        _todoPath = Path.Combine(
            Path.GetTempPath(),
            "glasswork-core-ado-reconciliation-tests",
            Guid.NewGuid().ToString("N"),
            "wiki",
            "todo");
        Directory.CreateDirectory(_todoPath);
        _selfWrites = new SelfWriteCoordinator(_todoPath);
        _vault = new VaultService(_todoPath, _selfWrites);
        _mutations = new ResourceMutationService(
            _todoPath,
            _vault,
            clock: () => Now);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var root = Directory.GetParent(Directory.GetParent(_todoPath)!.FullName)!.FullName;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void ReconcileAdoTask_ResumedActiveWritesOnlyTheFinalInProgressState()
    {
        const int adoId = 34567890;
        var task = new GlassworkTask
        {
            Id = "direct-active-restore",
            Title = "Direct active restore",
            Status = GlassworkTask.Statuses.Cancelled,
            CancelledAt = Now.AddDays(-1),
            CancellationReason = "ADO work item removed",
            AdoLink = adoId,
        };
        var taskPath = Path.Combine(_todoPath, $"{task.Id}.md");
        File.WriteAllText(taskPath, new FrontmatterParser().Serialize(task));
        task = _vault.Load(task.Id)!;

        var observedStatuses = new List<string>();
        var deleted = new List<string>();
        _vault.TaskWritten += (_, taskId) =>
            observedStatuses.Add(_vault.Load(taskId)!.Status);
        _vault.TaskDeleted += (_, taskId) => deleted.Add(taskId);

        var result = _mutations.ReconcileAdoTask(
            "direct-active-restore-1",
            task.Id,
            task.ResourceRevision,
            adoId,
            "In Review");

        Assert.AreEqual("applied", result.Outcome);
        CollectionAssert.AreEqual(
            new[] { GlassworkTask.Statuses.InProgress },
            observedStatuses);
        Assert.IsEmpty(deleted);
        Assert.IsTrue(_vault.Exists(task.Id));
        Assert.IsTrue(_selfWrites.IsOwnProcessWrite(taskPath));
    }
}
