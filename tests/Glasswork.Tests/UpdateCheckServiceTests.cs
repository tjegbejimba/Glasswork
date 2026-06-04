using Microsoft.VisualStudio.TestTools.UnitTesting;
using Glasswork.Core.AppUpdate;
using System.Net;

namespace Glasswork.Tests;

[TestClass]
public class UpdateCheckServiceTests
{
    [TestMethod]
    public async Task CheckForUpdatesAsync_RemoteGreaterThanInstalled_ReturnsUpdateAvailable()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": "v1.4.0"
            }
            """);
        
        var detector = new GitHubReleaseDetector(handler);
        var installedVersion = "1.3.0";
        var repoPathProvider = new FakeRepoPathProvider("/path/to/repo");
        
        var service = new UpdateCheckService(detector, installedVersion, repoPathProvider);
        
        // Act
        var result = await service.CheckForUpdatesAsync();
        
        // Assert
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsFalse(result.IsUpToDate);
        Assert.IsFalse(result.IsCheckFailed);
        Assert.IsNotNull(result.AvailableVersion);
        Assert.AreEqual(1, result.AvailableVersion.Major);
        Assert.AreEqual(4, result.AvailableVersion.Minor);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_CachesLastResult()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": "v1.4.0"
            }
            """);
        
        var detector = new GitHubReleaseDetector(handler);
        var service = new UpdateCheckService(detector, "1.3.0", new FakeRepoPathProvider("/repo"));
        
        // Act
        var result1 = await service.CheckForUpdatesAsync();
        var cachedResult = service.LastResult;
        
        // Assert
        Assert.IsNotNull(cachedResult);
        Assert.AreSame(result1, cachedResult);
        Assert.IsTrue(cachedResult.IsUpdateAvailable);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_RemoteEqualsInstalled_ReturnsUpToDate()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.OK, """
            {
                "tag_name": "v1.3.0"
            }
            """);
        
        var detector = new GitHubReleaseDetector(handler);
        var service = new UpdateCheckService(detector, "1.3.0", new FakeRepoPathProvider("/repo"));
        
        // Act
        var result = await service.CheckForUpdatesAsync();
        
        // Assert
        Assert.IsTrue(result.IsUpToDate);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsFalse(result.IsCheckFailed);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_DetectionFails_ReturnsCheckFailed()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(HttpStatusCode.InternalServerError, "Server error");
        
        var detector = new GitHubReleaseDetector(handler);
        var service = new UpdateCheckService(detector, "1.3.0", new FakeRepoPathProvider("/repo"));
        
        // Act
        var result = await service.CheckForUpdatesAsync();
        
        // Assert
        Assert.IsTrue(result.IsCheckFailed);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsFalse(result.IsUpToDate);
        Assert.IsNotNull(result.FailureReason);
    }

    [TestMethod]
    public void MissingRepoPath_DoesNotThrow()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        var detector = new GitHubReleaseDetector(handler);
        
        // Act & Assert - construction should not throw
        var service = new UpdateCheckService(detector, "1.3.0", new FakeRepoPathProvider(null));
        Assert.IsNull(service.RepoPath);
    }
}

internal class FakeRepoPathProvider : IRepoPathProvider
{
    private readonly string? _repoPath;
    
    public FakeRepoPathProvider(string? repoPath)
    {
        _repoPath = repoPath;
    }
    
    public string? GetRepoPath() => _repoPath;
}
