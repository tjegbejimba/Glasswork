using System.Text.Json;
using System.Text.Json.Serialization;
using Glasswork.Core.Services;

// Glasswork maintenance CLI. Thin shell over the TDD-tested TaskTypeBackfillService
// (Glasswork.Core). Exists so one-time Parent Task maintenance can run without
// rebuilding/redeploying the MCP server, and can reach wiki/todo/done/.
//
// Usage:
//   glasswork-maintenance inventory --vault <vaultRoot>
//       -> prints JSON inventory of task files (relative_path, ado id, current type state).
//
//   glasswork-maintenance apply --vault <vaultRoot> --classifications <file.json> [--apply]
//       -> stamps `type:` per the classifications. DRY RUN by default; pass --apply to write.
//          classifications JSON: [{ "relative_path": "foo.md", "ado_id": 123, "type": "parent" }]
//
//   glasswork-maintenance parent-migration <dry-run|execute|validate|rollback> ...
//       -> plans and applies the one-time legacy PBI migration. See the operator runbook.
//
// <vaultRoot> is the Obsidian vault root (e.g. ~/Wiki); task files live under
// <vaultRoot>/wiki/todo. SelfWriteCoordinator is wired with that todo path so the running
// app's FileWatcher is suppressed (hard rule 5) — matching how App.xaml.cs constructs it.

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
};

var verb = args.Length > 0 ? args[0] : null;

try
{
    return verb switch
    {
        "inventory" => RunInventory(),
        "apply" => RunApply(),
        "parent-migration" => ParentMigrationCommand.Run(args.Skip(1).ToArray(), json),
        _ => Usage($"Unknown or missing command: '{verb ?? "(none)"}'."),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int RunInventory()
{
    if (ResolveTodoPath() is not { } todoPath)
        return Usage("inventory requires --vault <vaultRoot>.");

    var svc = new TaskTypeBackfillService(todoPath);
    Console.WriteLine(JsonSerializer.Serialize(svc.Inventory(), json));
    return 0;
}

int RunApply()
{
    if (ResolveTodoPath() is not { } todoPath)
        return Usage("apply requires --vault <vaultRoot>.");

    var classificationsPath = GetOption("--classifications");
    if (string.IsNullOrWhiteSpace(classificationsPath))
        return Usage("apply requires --classifications <file.json>.");
    if (!File.Exists(classificationsPath))
        return Usage($"classifications file not found: {classificationsPath}");

    List<BackfillClassification>? classifications;
    try
    {
        classifications = JsonSerializer.Deserialize<List<BackfillClassification>>(
            File.ReadAllText(classificationsPath), json);
    }
    catch (JsonException ex)
    {
        return Usage($"could not parse classifications JSON: {ex.Message}");
    }

    if (classifications is null || classifications.Count == 0)
        return Usage("classifications file contained no entries.");

    var commit = HasFlag("--apply");
    var selfWrites = new SelfWriteCoordinator(todoPath);
    var svc = new TaskTypeBackfillService(todoPath, selfWrites);

    Console.Error.WriteLine(commit
        ? $"APPLYING — writing up to {classifications.Count} classification(s) to {todoPath}"
        : $"DRY RUN — previewing {classifications.Count} classification(s) (pass --apply to write)");

    var report = svc.Run(classifications, dryRun: !commit);
    Console.WriteLine(JsonSerializer.Serialize(report, json));
    return 0;
}

// --- helpers ---------------------------------------------------------------

string? ResolveTodoPath()
{
    var vault = GetOption("--vault");
    return string.IsNullOrWhiteSpace(vault)
        ? null
        : Path.Combine(vault, "wiki", "todo");
}

string? GetOption(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;

int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  glasswork-maintenance inventory --vault <vaultRoot>");
    Console.Error.WriteLine("  glasswork-maintenance apply --vault <vaultRoot> --classifications <file.json> [--apply]");
    Console.Error.WriteLine("  glasswork-maintenance parent-migration <dry-run|execute|validate|rollback> [options]");
    return 2;
}
