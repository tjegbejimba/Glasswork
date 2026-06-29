using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// One-time, idempotent backfill of the <c>type</c> frontmatter field (ADR 0016) onto
/// task files that predate the field. The actual classification (which ADO work-item type
/// a file is) is supplied by the caller — this service owns only the safe, lossless
/// vault-side mechanics: enumerating task files, resolving a file's ADO id, surgically
/// inserting the <c>type:</c> line, and reporting.
///
/// All edits are <b>surgical single-line inserts</b> into the YAML frontmatter block.
/// We never round-trip through <see cref="FrontmatterParser.Serialize"/>, which would
/// rewrite legacy <c>ado_link:</c> files into the <c>links:</c> array form and churn the
/// vault (the very thing ADR 0016 §"Serialization avoids file churn" forbids).
/// </summary>
public partial class TaskTypeBackfillService
{
    private readonly string _todoPath;
    private readonly SelfWriteCoordinator? _selfWrites;

    /// <param name="todoPath">The vault's <c>wiki/todo</c> directory.</param>
    /// <param name="selfWrites">Registers writes so the running app's FileWatcher does not
    /// raise a spurious external-change banner (hard rule 5). Optional for dry-run/tests.</param>
    public TaskTypeBackfillService(string todoPath, SelfWriteCoordinator? selfWrites = null)
    {
        _todoPath = todoPath;
        _selfWrites = selfWrites;
    }

    [GeneratedRegex(@"\A---[ \t]*\r?\n", RegexOptions.Singleline)]
    private static partial Regex OpeningDelimiterRegex();

    [GeneratedRegex(@"(?m)^---[ \t]*\r?$")]
    private static partial Regex ClosingDelimiterRegex();

    [GeneratedRegex(@"(?m)^type:")]
    private static partial Regex TopLevelTypeKeyRegex();

    [GeneratedRegex(@"(?m)^type:[ \t]*([^\r\n]*)")]
    private static partial Regex TopLevelTypeValueRegex();

    [GeneratedRegex(@"(?m)^priority:[^\r\n]*\r?\n")]
    private static partial Regex PriorityLineRegex();

    [GeneratedRegex(@"(?m)^status:[^\r\n]*\r?\n")]
    private static partial Regex StatusLineRegex();


    [GeneratedRegex(@"(?m)^ado_link:[ \t]*(\d+)")]
    private static partial Regex AdoLinkFrontmatterRegex();

    [GeneratedRegex(@"(?m)^ADO[ \t]+(\d+)\b")]
    private static partial Regex AdoBodyMarkerRegex();

    [GeneratedRegex(@"_workitems/edit/(\d+)\b")]
    private static partial Regex WorkitemUrlRegex();

    /// <summary>
    /// Surgically insert <c>type: &lt;type&gt;</c> into the file's YAML frontmatter.
    /// Operates only within the first <c>---</c>…<c>---</c> block and matches column-0
    /// (top-level) keys, so a nested <c>links:\n- type: ado</c> entry or a <c>type:</c>
    /// string in the body is never treated as the task type.
    ///
    /// Idempotent: a no-op (returns <c>changed: false</c>) when a top-level <c>type:</c>
    /// already exists, when <paramref name="type"/> is the default <c>task</c> (omitted by
    /// convention), or when the content has no parseable frontmatter. Lossless: only the
    /// single inserted line changes; the file's newline style is preserved.
    /// </summary>
    public static (string Content, bool Changed) StampType(string content, string type)
    {
        // Default "task" is omitted to match the serializer and avoid churn (ADR 0016).
        if (type == GlassworkTask.Types.Task) return (content, false);

        if (!TryGetFrontmatterSpan(content, out var yamlStart, out var yamlEnd))
            return (content, false);

        var yaml = content[yamlStart..yamlEnd];

        // Idempotency: a top-level type already present -> leave untouched.
        if (TopLevelTypeKeyRegex().IsMatch(yaml)) return (content, false);

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var line = $"type: {type}{newline}";

        var insertAt = FindInsertionPoint(yaml, yamlStart, yamlEnd);
        var updated = content[..insertAt] + line + content[insertAt..];
        return (updated, true);
    }

    /// <summary>
    /// Where in the frontmatter to insert the <c>type:</c> line: after the top-level
    /// <c>priority:</c> line (canonical serializer order); failing that after
    /// <c>status:</c>; failing that just before the closing <c>---</c> delimiter.
    /// Returned index is absolute (into the full file content).
    /// </summary>
    private static int FindInsertionPoint(string yaml, int yamlStart, int yamlEnd)
    {
        var priority = PriorityLineRegex().Match(yaml);
        if (priority.Success) return yamlStart + priority.Index + priority.Length;

        var status = StatusLineRegex().Match(yaml);
        if (status.Success) return yamlStart + status.Index + status.Length;

        return yamlEnd;
    }

    /// <summary>
    /// Locates the YAML frontmatter content span (between the opening and closing
    /// <c>---</c> delimiters). Returns false when the file does not start with a
    /// terminated frontmatter block.
    /// </summary>
    private static bool TryGetFrontmatterSpan(string content, out int yamlStart, out int yamlEnd)
    {
        yamlStart = 0;
        yamlEnd = 0;

        var open = OpeningDelimiterRegex().Match(content);
        if (!open.Success) return false;

        yamlStart = open.Length;
        var close = ClosingDelimiterRegex().Match(content, yamlStart);
        if (!close.Success) return false;

        yamlEnd = close.Index;
        return true;
    }

    /// <summary>
    /// Resolves a task file's own ADO work-item id with strict precedence and ambiguity
    /// reporting: (1) a top-level <c>ado_link:</c> frontmatter field is authoritative;
    /// (2) otherwise a line-anchored <c>^ADO &lt;id&gt;</c> body marker, if exactly one
    /// distinct id; (3) otherwise a <c>_workitems/edit/&lt;id&gt;</c> URL, if exactly one
    /// distinct id. Multiple distinct ids within a tier yields <see cref="AdoIdStatus.Ambiguous"/>
    /// so the caller can skip and report; no reference yields <see cref="AdoIdStatus.None"/>.
    /// The line anchor on the body marker avoids picking up casual prose mentions
    /// (e.g. "same shape as ADO 123").
    /// </summary>
    public static AdoIdResolution ResolveAdoId(string content)
    {
        var adoLink = AdoLinkFrontmatterRegex().Match(content);
        if (adoLink.Success && int.TryParse(adoLink.Groups[1].Value, out var linkId))
            return AdoIdResolution.Resolved(linkId);

        var markerIds = DistinctIds(AdoBodyMarkerRegex().Matches(content));
        if (markerIds.Count == 1) return AdoIdResolution.Resolved(markerIds[0]);
        if (markerIds.Count > 1) return AdoIdResolution.Ambiguous;

        var urlIds = DistinctIds(WorkitemUrlRegex().Matches(content));
        if (urlIds.Count == 1) return AdoIdResolution.Resolved(urlIds[0]);
        if (urlIds.Count > 1) return AdoIdResolution.Ambiguous;

        return AdoIdResolution.None;
    }

    private static List<int> DistinctIds(System.Text.RegularExpressions.MatchCollection matches)
    {
        var ids = new HashSet<int>();
        foreach (Match m in matches)
            if (int.TryParse(m.Groups[1].Value, out var id))
                ids.Add(id);
        return [.. ids];
    }

    /// <summary>
    /// Enumerates every task file with its ADO-id resolution and current <c>type</c> state.
    /// Scope is exactly <c>wiki/todo/*.md</c> + <c>wiki/todo/done/*.md</c> — NOT recursive,
    /// so artifact subfolders (<c>&lt;id&gt;.artifacts/</c>) are never included. Relative
    /// paths use forward slashes (e.g. <c>done/foo.md</c>).
    /// </summary>
    public IReadOnlyList<TaskInventoryItem> Inventory()
    {
        var items = new List<TaskInventoryItem>();
        foreach (var (fullPath, relativePath) in EnumerateTaskFiles())
        {
            string content;
            try { content = File.ReadAllText(fullPath); }
            catch { continue; }

            var (hasType, rawType) = ReadTopLevelType(content);
            items.Add(new TaskInventoryItem(
                RelativePath: relativePath,
                Ado: ResolveAdoId(content),
                RawType: rawType,
                HasType: hasType,
                NormalizedType: GlassworkTask.Types.Normalize(rawType)));
        }
        return items;
    }

    /// <summary>
    /// Yields <c>(fullPath, relativePath)</c> for task files in scope: the top level of
    /// <c>wiki/todo</c> and the top level of its <c>done/</c> subfolder only.
    /// </summary>
    private IEnumerable<(string FullPath, string RelativePath)> EnumerateTaskFiles()
    {
        if (!Directory.Exists(_todoPath)) yield break;

        foreach (var p in Directory
            .EnumerateFiles(_todoPath, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return (p, Path.GetFileName(p));
        }

        var doneDir = Path.Combine(_todoPath, "done");
        if (!Directory.Exists(doneDir)) yield break;

        foreach (var p in Directory
            .EnumerateFiles(doneDir, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return (p, "done/" + Path.GetFileName(p));
        }
    }

    /// <summary>
    /// Reads the top-level <c>type:</c> frontmatter value. Returns <c>has: false</c> when
    /// the key is absent (so callers can distinguish a missing field from an explicit
    /// <c>type: task</c> — both normalize to <c>task</c>). Only the frontmatter block is
    /// inspected, never the body.
    /// </summary>
    private static (bool Has, string? Raw) ReadTopLevelType(string content)
    {
        if (!TryGetFrontmatterSpan(content, out var yamlStart, out var yamlEnd))
            return (false, null);

        var yaml = content[yamlStart..yamlEnd];
        var m = TopLevelTypeValueRegex().Match(yaml);
        return m.Success ? (true, m.Groups[1].Value.Trim()) : (false, null);
    }

    /// <summary>
    /// Applies a caller-supplied classification (which files are <c>pbi</c>/<c>bug</c>) to
    /// the vault, surgically stamping the <c>type:</c> field. Idempotent and safe to re-run.
    ///
    /// <para>Preflight (no writes): rejects duplicate paths, types other than <c>pbi</c>/
    /// <c>bug</c>, and paths not present in the in-scope inventory.</para>
    /// <para>Per file: a file that already carries a top-level <c>type:</c> is skipped
    /// (<see cref="BackfillReport.SkippedAlreadyTyped"/>); a file whose current ADO id no
    /// longer matches the classification (edited/renamed since classification) is skipped as
    /// drift (<see cref="BackfillReport.SkippedDrift"/>); otherwise the <c>type:</c> line is
    /// inserted. When <paramref name="dryRun"/> is true the report is computed identically
    /// but nothing is written.</para>
    /// </summary>
    public BackfillReport Run(IReadOnlyList<BackfillClassification> classifications, bool dryRun)
    {
        var fileMap = EnumerateTaskFiles()
            .ToDictionary(t => t.RelativePath, t => t.FullPath, StringComparer.Ordinal);

        var stamped = new List<string>();
        var alreadyTyped = new List<string>();
        var drift = new List<string>();
        var invalid = new List<BackfillRejection>();

        var duplicatePaths = classifications
            .GroupBy(c => c.RelativePath, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var c in classifications)
        {
            if (duplicatePaths.Contains(c.RelativePath))
            {
                invalid.Add(new BackfillRejection(c.RelativePath, "duplicate_classification"));
                continue;
            }
            if (c.Type != GlassworkTask.Types.Pbi && c.Type != GlassworkTask.Types.Bug)
            {
                invalid.Add(new BackfillRejection(c.RelativePath, $"invalid_type:{c.Type}"));
                continue;
            }
            if (!fileMap.TryGetValue(c.RelativePath, out var fullPath))
            {
                invalid.Add(new BackfillRejection(c.RelativePath, "unknown_path"));
                continue;
            }

            string content;
            try { content = File.ReadAllText(fullPath); }
            catch { invalid.Add(new BackfillRejection(c.RelativePath, "read_error")); continue; }

            // Idempotency: a top-level type already present -> nothing to do.
            if (ReadTopLevelType(content).Has)
            {
                alreadyTyped.Add(c.RelativePath);
                continue;
            }

            // Drift: the file must still resolve to the ADO id it was classified under.
            var ado = ResolveAdoId(content);
            if (ado.Status != AdoIdStatus.Resolved || ado.Id != c.AdoId)
            {
                drift.Add(c.RelativePath);
                continue;
            }

            var (updated, changed) = StampType(content, c.Type);
            if (!changed)
            {
                alreadyTyped.Add(c.RelativePath);
                continue;
            }

            if (!dryRun)
            {
                _selfWrites?.RegisterWrite(fullPath);
                File.WriteAllText(fullPath, updated);
            }
            stamped.Add(c.RelativePath);
        }

        return new BackfillReport(stamped, alreadyTyped, drift, invalid, dryRun);
    }
}

/// <summary>A caller's decision that the file at <paramref name="RelativePath"/> (whose own
/// ADO id is <paramref name="AdoId"/>) should be stamped <paramref name="Type"/>
/// (<c>pbi</c> or <c>bug</c>).</summary>
public sealed record BackfillClassification(string RelativePath, int AdoId, string Type);

/// <summary>A classification rejected during preflight, with a machine-readable reason.</summary>
public sealed record BackfillRejection(string RelativePath, string Reason);

/// <summary>Outcome of <see cref="TaskTypeBackfillService.Run"/>. In a dry run the
/// <see cref="Stamped"/> list is what <em>would</em> be stamped.</summary>
public sealed record BackfillReport(
    IReadOnlyList<string> Stamped,
    IReadOnlyList<string> SkippedAlreadyTyped,
    IReadOnlyList<string> SkippedDrift,
    IReadOnlyList<BackfillRejection> Invalid,
    bool DryRun);

/// <summary>One task file's backfill-relevant state (from <see cref="TaskTypeBackfillService.Inventory"/>).</summary>
/// <param name="RelativePath">Path relative to <c>wiki/todo</c>, forward-slashed (e.g. <c>done/foo.md</c>).</param>
/// <param name="Ado">Resolution of the file's own ADO work-item id.</param>
/// <param name="RawType">Exact top-level <c>type:</c> value, or null when the key is absent.</param>
/// <param name="HasType">True when a top-level <c>type:</c> key is present.</param>
/// <param name="NormalizedType">Normalized type (missing/invalid/explicit <c>task</c> all → <c>task</c>).</param>
public sealed record TaskInventoryItem(
    string RelativePath,
    AdoIdResolution Ado,
    string? RawType,
    bool HasType,
    string NormalizedType);

/// <summary>Outcome of <see cref="TaskTypeBackfillService.ResolveAdoId"/>.</summary>
public enum AdoIdStatus
{
    /// <summary>No ADO reference found in the file.</summary>
    None,
    /// <summary>Exactly one ADO id resolved.</summary>
    Resolved,
    /// <summary>Multiple distinct ADO ids found in the highest-precedence tier present.</summary>
    Ambiguous,
}

/// <summary>The resolved ADO id (when <see cref="AdoIdStatus.Resolved"/>) plus its status.</summary>
public readonly record struct AdoIdResolution(AdoIdStatus Status, int? Id)
{
    public static readonly AdoIdResolution None = new(AdoIdStatus.None, null);
    public static readonly AdoIdResolution Ambiguous = new(AdoIdStatus.Ambiguous, null);
    public static AdoIdResolution Resolved(int id) => new(AdoIdStatus.Resolved, id);
}
