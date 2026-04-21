using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class AdoParentIdExtractorTests
{
    [TestMethod]
    public void Null_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId(null));

    [TestMethod]
    public void Empty_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId(""));

    [TestMethod]
    public void Whitespace_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId("   "));

    [TestMethod]
    public void Bare_digits_returns_id() => Assert.AreEqual(12345, AdoParentIdExtractor.TryExtractId("12345"));

    [TestMethod]
    public void Bare_digits_with_whitespace_returns_id()
        => Assert.AreEqual(12345, AdoParentIdExtractor.TryExtractId("  12345  "));

    [TestMethod]
    public void Zero_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId("0"));

    [TestMethod]
    public void Negative_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId("-5"));

    [TestMethod]
    public void Free_text_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId("Project Foo"));

    [TestMethod]
    public void Mixed_alphanumeric_returns_null() => Assert.IsNull(AdoParentIdExtractor.TryExtractId("abc123"));

    [TestMethod]
    public void DevAzure_url_returns_id()
        => Assert.AreEqual(37226063, AdoParentIdExtractor.TryExtractId("https://dev.azure.com/myorg/myproj/_workitems/edit/37226063"));

    [TestMethod]
    public void VisualStudio_url_returns_id()
        => Assert.AreEqual(37226063, AdoParentIdExtractor.TryExtractId("https://msazure.visualstudio.com/One/_workitems/edit/37226063"));

    [TestMethod]
    public void Url_with_trailing_slash_returns_id()
        => Assert.AreEqual(12345, AdoParentIdExtractor.TryExtractId("https://dev.azure.com/org/proj/_workitems/edit/12345/"));

    [TestMethod]
    public void Url_with_query_string_returns_id()
        => Assert.AreEqual(12345, AdoParentIdExtractor.TryExtractId("https://dev.azure.com/org/proj/_workitems/edit/12345?foo=bar"));

    [TestMethod]
    public void Url_with_fragment_returns_id()
        => Assert.AreEqual(12345, AdoParentIdExtractor.TryExtractId("https://dev.azure.com/org/proj/_workitems/edit/12345#section"));

    [TestMethod]
    public void Url_without_edit_segment_returns_null()
        => Assert.IsNull(AdoParentIdExtractor.TryExtractId("https://dev.azure.com/org/proj/_workitems/12345"));

    [TestMethod]
    public void Url_with_non_numeric_id_returns_null()
        => Assert.IsNull(AdoParentIdExtractor.TryExtractId("https://dev.azure.com/org/proj/_workitems/edit/abc"));
}
