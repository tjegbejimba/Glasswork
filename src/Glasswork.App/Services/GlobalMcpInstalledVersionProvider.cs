using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Services;

public sealed partial class GlobalMcpInstalledVersionProvider : IMcpInstalledVersionProvider
{
    public async Task<McpInstalledVersionResult> GetInstalledVersionAsync()
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet",
            "tools",
            "glasswork-mcp.exe");
        if (!File.Exists(executablePath))
        {
            return McpInstalledVersionResult.NotInstalled();
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--version");

            process.Start();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return McpInstalledVersionResult.InstalledUnknown();
            }

            var identity = (await process.StandardOutput.ReadToEndAsync()).Trim();
            if (process.ExitCode != 0)
            {
                var error = (await process.StandardError.ReadToEndAsync()).Trim();
                return McpInstalledVersionResult.Failed(
                    string.IsNullOrWhiteSpace(error) ? "glasswork-mcp --version failed" : error);
            }

            var match = BuildIdentityPattern().Match(identity);
            if (!match.Success ||
                !AppVersion.TryParse(match.Groups["version"].Value, out var version) ||
                version is null)
            {
                return McpInstalledVersionResult.InstalledUnknown();
            }

            return McpInstalledVersionResult.Installed(version);
        }
        catch (Exception ex)
        {
            return McpInstalledVersionResult.Failed(ex.Message);
        }
    }

    [GeneratedRegex(@"^(?<version>0\.\d+\.\d+)\+(?:local|[0-9a-f]{40})$")]
    private static partial Regex BuildIdentityPattern();
}
