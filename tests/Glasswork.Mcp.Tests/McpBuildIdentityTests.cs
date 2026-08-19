using System.Diagnostics;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class McpBuildIdentityTests
{
    [TestMethod]
    public async Task VersionSwitch_ReportsVersionAndSourceRevisionWithoutStartingServer()
    {
        var assemblyPath = typeof(GlassworkTools).Assembly.Location;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(assemblyPath);
        process.StartInfo.ArgumentList.Add("--version");

        process.Start();
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();

        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        StringAssert.Matches(output, new System.Text.RegularExpressions.Regex(
            @"^0\.\d+\.\d+\+(?:local|[0-9a-f]{40})$"));
    }
}
