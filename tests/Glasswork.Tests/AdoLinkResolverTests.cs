using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AdoLinkResolverTests
{
    [TestMethod]
    public void NullParent_ReturnsNull()
    {
        Assert.IsNull(AdoLinkResolver.TryResolve(null, "https://dev.azure.com/org/proj"));
    }

    [TestMethod]
    public void EmptyParent_ReturnsNull()
    {
        Assert.IsNull(AdoLinkResolver.TryResolve("", "https://dev.azure.com/org/proj"));
        Assert.IsNull(AdoLinkResolver.TryResolve("   ", "https://dev.azure.com/org/proj"));
    }

    [TestMethod]
    public void HttpsUrl_ReturnedAsIs()
    {
        Assert.AreEqual(
            "https://example.com/foo",
            AdoLinkResolver.TryResolve("https://example.com/foo", null));
    }

    [TestMethod]
    public void HttpUrl_ReturnedAsIs()
    {
        Assert.AreEqual(
            "http://example.com/foo",
            AdoLinkResolver.TryResolve("http://example.com/foo", null));
    }

    [TestMethod]
    public void HttpsUrl_TrimmedOfWhitespace()
    {
        Assert.AreEqual(
            "https://example.com/foo",
            AdoLinkResolver.TryResolve("  https://example.com/foo  ", null));
    }

    [TestMethod]
    public void Numeric_WithBaseUrl_BuildsAdoEditUrl()
    {
        Assert.AreEqual(
            "https://dev.azure.com/org/proj/_workitems/edit/12345",
            AdoLinkResolver.TryResolve("12345", "https://dev.azure.com/org/proj"));
    }

    [TestMethod]
    public void Numeric_WithBaseUrl_TrailingSlashTrimmed()
    {
        Assert.AreEqual(
            "https://dev.azure.com/org/proj/_workitems/edit/12345",
            AdoLinkResolver.TryResolve("12345", "https://dev.azure.com/org/proj/"));
    }

    [TestMethod]
    public void Numeric_WithoutBaseUrl_ReturnsNull()
    {
        Assert.IsNull(AdoLinkResolver.TryResolve("12345", null));
        Assert.IsNull(AdoLinkResolver.TryResolve("12345", ""));
        Assert.IsNull(AdoLinkResolver.TryResolve("12345", "   "));
    }

    [TestMethod]
    public void NonNumericNonUrl_ReturnsNull()
    {
        Assert.IsNull(AdoLinkResolver.TryResolve("PBI 1", "https://dev.azure.com/org/proj"));
        Assert.IsNull(AdoLinkResolver.TryResolve("Project Phoenix", "https://dev.azure.com/org/proj"));
    }

    [TestMethod]
    public void Numeric_WithSurroundingWhitespace_StillResolves()
    {
        Assert.AreEqual(
            "https://dev.azure.com/org/proj/_workitems/edit/42",
            AdoLinkResolver.TryResolve("  42  ", "https://dev.azure.com/org/proj"));
    }

    [TestMethod]
    public void Numeric_WithBaseUrlWhitespace_Trimmed()
    {
        Assert.AreEqual(
            "https://dev.azure.com/org/proj/_workitems/edit/42",
            AdoLinkResolver.TryResolve("42", "  https://dev.azure.com/org/proj  "));
    }
}
