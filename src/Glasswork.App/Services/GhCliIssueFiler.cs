using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Glasswork.Services;

/// <summary>
/// Files a GitHub issue by shelling out to <c>gh issue create</c>. Modeled on
/// <see cref="AzCliAdoWorkItemFetcher"/>: graceful failure with structured <see cref="GhIssueFilingResult"/>
/// so the UI can show precise guidance (install gh, authenticate, etc.) instead of a generic error.
/// Body is written to a temp file and passed via <c>--body-file</c> to avoid any argument-quoting
/// issues with long multi-line content.
/// </summary>
public sealed class GhCliIssueFiler
{
    private const string TargetRepo = "tjegbejimba/Glasswork";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// WinUI packaged apps often launch without inheriting the user PATH that contains the
    /// GitHub CLI shim, so we try common MSI/winget install locations before falling back to PATH.
    /// </summary>
    private static string ResolveGhPath()
    {
        if (!OperatingSystem.IsWindows()) return "gh";

        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\GitHub CLI\gh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\GitHub CLI\gh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Links\gh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Programs\GitHub CLI\gh.exe"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return "gh.exe";
    }

    public async Task<GhIssueFilingResult> TryFileIssueAsync(
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must not be null or whitespace.", nameof(title));

        var bodyFile = Path.Combine(Path.GetTempPath(), $"gw-issue-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(bodyFile, body ?? string.Empty, ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = ResolveGhPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("issue");
            psi.ArgumentList.Add("create");
            psi.ArgumentList.Add("--repo");
            psi.ArgumentList.Add(TargetRepo);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("--body-file");
            psi.ArgumentList.Add(bodyFile);
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                psi.ArgumentList.Add("--label");
                psi.ArgumentList.Add(label);
            }

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhFile] failed to start gh: {ex.GetType().Name}: {ex.Message}");
                return GhIssueFilingResult.Fail(GhIssueFailure.NotInstalled,
                    "GitHub CLI (gh) not found. Install from https://cli.github.com and sign in with `gh auth login`.");
            }
            if (proc is null)
            {
                return GhIssueFilingResult.Fail(GhIssueFailure.NotInstalled,
                    "GitHub CLI (gh) could not be launched.");
            }

            using (proc)
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(DefaultTimeout);
                try
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                    var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                    var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
                    var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

                    if (proc.ExitCode != 0)
                    {
                        Debug.WriteLine($"[GhFile] gh exit={proc.ExitCode} stderr={Truncate(stderr)}");
                        var kind = ClassifyError(stderr);
                        var message = kind switch
                        {
                            GhIssueFailure.NotAuthenticated =>
                                "Not signed in to GitHub. Run `gh auth login` in a terminal, then retry.",
                            _ => string.IsNullOrEmpty(stderr)
                                ? $"gh failed with exit code {proc.ExitCode}."
                                : $"gh: {Truncate(stderr, 300)}",
                        };
                        return GhIssueFilingResult.Fail(kind, message);
                    }

                    // gh issue create prints the issue URL as the final line of stdout on success.
                    var url = ExtractIssueUrl(stdout);
                    if (url is null)
                    {
                        Debug.WriteLine($"[GhFile] succeeded but no URL in stdout={Truncate(stdout)}");
                        return GhIssueFilingResult.Fail(GhIssueFailure.Unknown,
                            "Issue may have been filed, but gh did not return a URL. Check the repository.");
                    }
                    return GhIssueFilingResult.Success(url);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[GhFile] timeout/canceled");
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    return GhIssueFilingResult.Fail(GhIssueFailure.Timeout,
                        "gh timed out. Check your network connection and retry.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GhFile] unexpected error: {ex.GetType().Name}: {ex.Message}");
                    return GhIssueFilingResult.Fail(GhIssueFailure.Unknown, ex.Message);
                }
            }
        }
        finally
        {
            try { if (File.Exists(bodyFile)) File.Delete(bodyFile); } catch { }
        }
    }

    private static string? ExtractIssueUrl(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("/issues/", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

    private static GhIssueFailure ClassifyError(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return GhIssueFailure.Unknown;
        // gh auth messages vary, but consistently mention "authenticat" or "gh auth login".
        if (stderr.Contains("authenticat", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return GhIssueFailure.NotAuthenticated;
        }
        return GhIssueFailure.Unknown;
    }

    private static string Truncate(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public enum GhIssueFailure
{
    None,
    NotInstalled,
    NotAuthenticated,
    Timeout,
    Unknown,
}

public sealed record GhIssueFilingResult(bool Succeeded, string? IssueUrl, GhIssueFailure Failure, string? ErrorMessage)
{
    public static GhIssueFilingResult Success(string url) => new(true, url, GhIssueFailure.None, null);
    public static GhIssueFilingResult Fail(GhIssueFailure kind, string message) =>
        new(false, null, kind, message);
}
