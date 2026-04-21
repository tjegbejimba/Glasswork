using System;
using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class ArtifactLinkPolicyTests
{
    [TestMethod]
    public void Https_IsAllowed()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("https://example.com"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("https://github.com/foo/bar"));
    }

    [TestMethod]
    public void Http_IsAllowed()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("http://example.com"));
    }

    [TestMethod]
    public void Obsidian_IsAllowed()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("obsidian://open?vault=Wiki&file=foo"));
    }

    [TestMethod]
    public void File_IsBlocked()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("file:///C:/Windows/System32/cmd.exe"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("file://server/share"));
    }

    [TestMethod]
    public void UnknownScheme_IsBlocked()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("javascript:alert(1)"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("vbscript:foo"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("data:text/html,<h1>x</h1>"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("ftp://x"));
    }

    [TestMethod]
    public void NullOrEmpty_IsBlocked()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide(null));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide(""));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("   "));
    }

    [TestMethod]
    public void Malformed_IsBlocked()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("not a url at all"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("://missing-scheme"));
    }

    [TestMethod]
    public void CaseInsensitiveScheme()
    {
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("HTTPS://example.com"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Allow, ArtifactLinkPolicy.Decide("Obsidian://open"));
        Assert.AreEqual(ArtifactLinkPolicy.Decision.Block, ArtifactLinkPolicy.Decide("FILE:///c:/foo"));
    }
}
