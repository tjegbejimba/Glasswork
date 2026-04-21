using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AdoOrgUrlExtractorTests
{
    [TestMethod]
    public void Null_ReturnsNull() => Assert.IsNull(AdoOrgUrlExtractor.TryExtract(null));

    [TestMethod]
    public void Empty_ReturnsNull()
    {
        Assert.IsNull(AdoOrgUrlExtractor.TryExtract(""));
        Assert.IsNull(AdoOrgUrlExtractor.TryExtract("   "));
    }

    [TestMethod]
    public void NotAUrl_ReturnsNull() => Assert.IsNull(AdoOrgUrlExtractor.TryExtract("just some text"));

    [TestMethod]
    public void VisualStudioCom_WithProject_StripsProject() =>
        Assert.AreEqual(
            "https://msazure.visualstudio.com",
            AdoOrgUrlExtractor.TryExtract("https://msazure.visualstudio.com/One"));

    [TestMethod]
    public void VisualStudioCom_WithProjectAndTrailingSlash_StripsBoth() =>
        Assert.AreEqual(
            "https://msazure.visualstudio.com",
            AdoOrgUrlExtractor.TryExtract("https://msazure.visualstudio.com/One/"));

    [TestMethod]
    public void VisualStudioCom_HostOnly_ReturnedAsScheme() =>
        Assert.AreEqual(
            "https://msazure.visualstudio.com",
            AdoOrgUrlExtractor.TryExtract("https://msazure.visualstudio.com"));

    [TestMethod]
    public void VisualStudioCom_HostOnlyWithSlash_StripsSlash() =>
        Assert.AreEqual(
            "https://msazure.visualstudio.com",
            AdoOrgUrlExtractor.TryExtract("https://msazure.visualstudio.com/"));

    [TestMethod]
    public void DevAzureCom_OrgAndProject_StripsProject() =>
        Assert.AreEqual(
            "https://dev.azure.com/myorg",
            AdoOrgUrlExtractor.TryExtract("https://dev.azure.com/myorg/myproject"));

    [TestMethod]
    public void DevAzureCom_OrgOnly_ReturnedAsIs() =>
        Assert.AreEqual(
            "https://dev.azure.com/myorg",
            AdoOrgUrlExtractor.TryExtract("https://dev.azure.com/myorg"));

    [TestMethod]
    public void DevAzureCom_OrgWithTrailingSlash_StripsSlash() =>
        Assert.AreEqual(
            "https://dev.azure.com/myorg",
            AdoOrgUrlExtractor.TryExtract("https://dev.azure.com/myorg/"));

    [TestMethod]
    public void DevAzureCom_NoOrg_ReturnsNull()
    {
        Assert.IsNull(AdoOrgUrlExtractor.TryExtract("https://dev.azure.com"));
        Assert.IsNull(AdoOrgUrlExtractor.TryExtract("https://dev.azure.com/"));
    }

    [TestMethod]
    public void Whitespace_IsTrimmed() =>
        Assert.AreEqual(
            "https://dev.azure.com/myorg",
            AdoOrgUrlExtractor.TryExtract("  https://dev.azure.com/myorg/proj  "));
}
