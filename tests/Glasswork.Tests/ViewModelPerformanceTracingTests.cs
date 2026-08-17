using Glasswork.Core.Queries;
using Glasswork.Core.Services;
using Glasswork.ViewModels;

namespace Glasswork.Tests;

[TestClass]
public class ViewModelPerformanceTracingTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private IndexService _index = null!;
    private TaskService _taskService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-viewmodel-performance-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _vault = new VaultService(_tempDir);
        _index = new IndexService(_vault);
        _index.EnsureLoaded();
        _taskService = new TaskService(_vault, _index);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void MyDayRefresh_EmitsDataPreparationCounts()
    {
        var task = _taskService.CreateTask("Today");
        _taskService.ToggleMyDay(task);
        var tracer = new CapturingPerformanceTracer();
        var viewModel = new MyDayViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            taskQuery: null,
            performanceTracer: tracer);

        viewModel.Refresh();

        var record = tracer.Single("my_day.refresh_data");
        Assert.AreEqual(1, record.Counts["today_count"]);
        Assert.AreEqual(0, record.Counts["recently_completed_count"]);
        Assert.AreEqual(0, record.Counts["suggestion_count"]);
    }

    [TestMethod]
    public void MyDayRefresh_ExcludesCancelledTaskFromSuggestions()
    {
        var task = _taskService.CreateTask("Cancelled suggestion");
        task.Priority = Glasswork.Core.Models.GlassworkTask.Priorities.Urgent;
        _vault.Save(task);
        _taskService.Cancel(task, "Superseded");
        var viewModel = new MyDayViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            taskQuery: null);

        viewModel.Refresh();

        Assert.IsFalse(viewModel.Suggestions.Any(candidate => candidate.Id == task.Id));
    }

    [TestMethod]
    public void BacklogRefresh_EmitsDataPreparationShape()
    {
        _taskService.CreateTask("Backlog");
        var tracer = new CapturingPerformanceTracer();
        var viewModel = new BacklogViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            savedTaskViews: null,
            taskQuery: null,
            performanceTracer: tracer);

        viewModel.Refresh();

        var record = tracer.Single("backlog.refresh_data");
        Assert.AreEqual("list", record.Tags["view_mode"]);
        Assert.AreEqual("true", record.Tags["is_grouped"]);
        Assert.AreEqual(1, record.Counts["task_count"]);
        Assert.IsGreaterThanOrEqualTo(1, record.Counts["row_count"]);
        Assert.AreEqual(0, record.Counts["board_column_count"]);
    }

    [TestMethod]
    public void BacklogRefresh_WhenDataPreparationFails_RecordsError()
    {
        _taskService.CreateTask("Backlog");
        var tracer = new CapturingPerformanceTracer();
        var viewModel = new BacklogViewModel(
            _vault,
            _taskService,
            _index,
            uiState: null,
            savedTaskViews: null,
            taskQuery: new ThrowingTaskQuery(),
            performanceTracer: tracer);

        Assert.ThrowsExactly<InvalidOperationException>(() => viewModel.Refresh());

        Assert.AreEqual("error", tracer.Single("backlog.refresh_data").Outcome);
    }

    private sealed class CapturingPerformanceTracer : IPerformanceTracer
    {
        private readonly List<Record> _records = [];

        public bool IsEnabled => true;
        public string? OutputPath => null;
        public IPerformanceTraceScope BeginSpan(string eventName) => new Scope(eventName, _records);
        public void EmitMilestone(string eventName) { }
        public void Dispose() { }

        public Record Single(string eventName) =>
            _records.Single(record => record.EventName == eventName);

        public sealed record Record(
            string EventName,
            IReadOnlyDictionary<string, int> Counts,
            IReadOnlyDictionary<string, string> Tags,
            string Outcome);

        private sealed class Scope : IPerformanceTraceScope
        {
            private readonly string _eventName;
            private readonly List<Record> _sink;
            private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);
            private string _outcome = "ok";

            public Scope(string eventName, List<Record> sink)
            {
                _eventName = eventName;
                _sink = sink;
            }

            public void SetCount(string name, int value) => _counts[name] = value;
            public void SetTag(string name, string value) => _tags[name] = value;
            public void SetTag(string name, bool value) => _tags[name] = value ? "true" : "false";
            public void SetOutcome(string outcome) => _outcome = outcome;
            public void Cancel() { }

            public void Dispose()
            {
                _sink.Add(new Record(
                    _eventName,
                    new Dictionary<string, int>(_counts),
                    new Dictionary<string, string>(_tags),
                    _outcome));
            }
        }
    }

    private sealed class ThrowingTaskQuery : ITaskQuery
    {
        public TaskQueryResult Execute(TaskQueryRequest request) =>
            throw new InvalidOperationException("Test failure");
    }
}
