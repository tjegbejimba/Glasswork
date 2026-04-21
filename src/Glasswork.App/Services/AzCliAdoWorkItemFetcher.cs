using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.Services;

namespace Glasswork.Services;

/// <summary>
/// Fetches the title of an Azure DevOps work item by shelling out to <c>az boards work-item show</c>.
/// Best-effort: any failure (az not installed, not signed in, no network, timeout, parse error,
/// missing org URL) results in <c>null</c> rather than an exception. Never blocks the UI longer
/// than the configured timeout (default 10s) and writes diagnostic noise via <see cref="Debug"/>
/// so a failed fetch isn't completely invisible.
/// </summary>
public sealed class AzCliAdoWorkItemFetcher
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<string?> TryFetchTitleAsync(int workItemId, string? baseUrl, CancellationToken ct = default)
    {
        if (workItemId <= 0) return null;
        var orgUrl = AdoOrgUrlExtractor.TryExtract(baseUrl);
        if (string.IsNullOrEmpty(orgUrl))
        {
            Debug.WriteLine($"[AdoFetch] no org url derivable from baseUrl='{baseUrl}'");
            return null;
        }

        // Resolve az.cmd / az on PATH. WinGet/MSI installs put az.cmd in
        // %ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin which is on PATH.
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "az.cmd" : "az",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("boards");
        psi.ArgumentList.Add("work-item");
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(workItemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--org");
        psi.ArgumentList.Add(orgUrl);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("json");

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AdoFetch] failed to start az: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (proc is null) return null;

        using (proc)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(DefaultTimeout);
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                if (proc.ExitCode != 0)
                {
                    Debug.WriteLine($"[AdoFetch] az exit={proc.ExitCode} stderr={Truncate(stderr)}");
                    return null;
                }

                var title = AdoWorkItemTitleParser.TryParseTitle(stdout);
                if (title is null)
                {
                    Debug.WriteLine($"[AdoFetch] parse failed for #{workItemId}; stdout={Truncate(stdout)}");
                }
                return title;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[AdoFetch] timeout/canceled for #{workItemId}");
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AdoFetch] unexpected error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private static string Truncate(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}
