using System.Net;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests;

[TestClass]
public sealed class McpUpdateCheckServiceTests
{
    [TestMethod]
    public async Task CheckForUpdatesAsync_NewerMcpRelease_ReturnsUpdateAvailable()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            [
                {
                    "tag_name": "mcp-v0.11.0",
                    "draft": false,
                    "prerelease": false
                },
                {
                    "tag_name": "v1.4.7",
                    "draft": false,
                    "prerelease": false
                }
            ]
            """);
        var installed = McpInstalledVersionResult.Installed(ParseVersion("0.10.0"));
        var service = new McpUpdateCheckService(
            new GitHubReleaseDetector(handler),
            new FakeMcpInstalledVersionProvider(installed));

        var result = await service.CheckForUpdatesAsync();

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("0.10.0", result.InstalledVersion!.ToString());
        Assert.AreEqual("0.11.0", result.AvailableVersion!.ToString());
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_LegacyInstalledBuild_ReturnsUpdateAvailable()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            [
                {
                    "tag_name": "mcp-v0.11.0",
                    "draft": false,
                    "prerelease": false
                }
            ]
            """);
        var service = new McpUpdateCheckService(
            new GitHubReleaseDetector(handler),
            new FakeMcpInstalledVersionProvider(McpInstalledVersionResult.InstalledUnknown()));

        var result = await service.CheckForUpdatesAsync();

        Assert.IsTrue(result.IsInstalled);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsNull(result.InstalledVersion);
        Assert.AreEqual("0.11.0", result.AvailableVersion!.ToString());
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReleaseDiscoveryFails_PreservesInstalledVersion()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.Forbidden, "rate limited");
        var installed = McpInstalledVersionResult.Installed(ParseVersion("0.11.0"));
        var service = new McpUpdateCheckService(
            new GitHubReleaseDetector(handler),
            new FakeMcpInstalledVersionProvider(installed));

        var result = await service.CheckForUpdatesAsync();

        Assert.IsTrue(result.IsCheckFailed);
        Assert.IsTrue(result.IsInstalled);
        Assert.AreEqual("0.11.0", result.InstalledVersion!.ToString());
    }

    private static AppVersion ParseVersion(string value)
    {
        Assert.IsTrue(AppVersion.TryParse(value, out var version));
        return version!;
    }

    private sealed class FakeMcpInstalledVersionProvider(McpInstalledVersionResult result)
        : IMcpInstalledVersionProvider
    {
        public Task<McpInstalledVersionResult> GetInstalledVersionAsync() =>
            Task.FromResult(result);
    }
}
