using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class LinkUriPolicyEdgeCaseTests
{
    [TestMethod]
    public void IncidentType_TextWithEmbeddedDigits_ShouldNotExtractId()
    {
        // This tests whether "foo123bar" incorrectly extracts "123" as an incident ID
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "foo123bar"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        // Expected: null (not a valid incident format)
        // Actual: might incorrectly build URL with "123"
        Console.WriteLine($"Result: {(result != null ? result.ToString() : "null")}");
        if (result != null)
        {
            Assert.Fail($"Expected null for 'foo123bar', but got: {result}");
        }
    }

    [TestMethod]
    public void IncidentType_IcmWithTrailingText_ShouldNotExtractId()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "ICM 123 with extra text"
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Console.WriteLine($"Result: {(result != null ? result.ToString() : "null")}");
        
        // This should be null since it's not a clean ICM ID
        if (result != null)
        {
            Assert.Fail($"Expected null for 'ICM 123 with extra text', but got: {result}");
        }
    }

    [TestMethod]
    public void PrType_IntegerValue_WithAdoBaseUrl_ChecksForDoubleSlash()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "456"
        };

        var result = LinkUriPolicy.Resolve(link, "https://dev.azure.com/org/proj");
        
        Assert.IsNotNull(result);
        var url = result.ToString();
        Console.WriteLine($"Generated URL: {url}");
        
        // Check if URL contains double slashes (bug)
        if (url.Contains("//pullrequest"))
        {
            Assert.Fail($"URL contains double slash: {url}");
        }
    }

    [TestMethod]
    public void Resolve_NullLink_ReturnsNull()
    {
        var result = LinkUriPolicy.Resolve(null, "https://base.com");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DisplayText_NullLink_ReturnsEmptyString()
    {
        var result = LinkUriPolicy.DisplayText(null);
        Assert.AreEqual(string.Empty, result);
    }
}
