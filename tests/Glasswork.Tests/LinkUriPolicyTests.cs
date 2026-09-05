using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class LinkUriPolicyTests
{
    // ── ADO type ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void AdoType_NumericValue_WithBaseUrl_BuildsWorkItemUrl()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Ado,
            Value = "12345"
        };

        var result = LinkUriPolicy.Resolve(link, "https://dev.azure.com/myorg/myproj");

        Assert.IsNotNull(result);
        Assert.AreEqual("https://dev.azure.com/myorg/myproj/_workitems/edit/12345", result.ToString());
    }

    [TestMethod]
    public void AdoType_WithoutBaseUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Ado,
            Value = "12345"
        };

        Assert.IsNull(LinkUriPolicy.Resolve(link, null));
        Assert.IsNull(LinkUriPolicy.Resolve(link, ""));
        Assert.IsNull(LinkUriPolicy.Resolve(link, "   "));
    }

    // ── PR type ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void PrType_UrlValue_PassesThrough()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "https://github.com/owner/repo/pull/123"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://github.com/owner/repo/pull/123", result.ToString());
    }

    [TestMethod]
    public void PrType_IntegerValue_WithAdoBaseUrl_BuildsAdoPrUrl()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "456"
        };

        var result = LinkUriPolicy.Resolve(link, "https://dev.azure.com/myorg/myproj");

        Assert.IsNotNull(result);
        Assert.AreEqual("https://dev.azure.com/myorg/myproj/_git/pullrequest/456", result.ToString());
    }

    [TestMethod]
    public void PrType_IntegerValue_WithoutAdoBaseUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "456"
        };

        Assert.IsNull(LinkUriPolicy.Resolve(link, null));
    }

    // ── Incident type ─────────────────────────────────────────────────────────

    [TestMethod]
    public void IncidentType_IcmPrefix_BuildsIcmPortalUrl()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "ICM 965114"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://portal.microsofticm.com/imp/v5/incidents/details/965114/home", result.ToString());
    }

    [TestMethod]
    public void IncidentType_BareInteger_BuildsIcmPortalUrl()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "965114"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://portal.microsofticm.com/imp/v5/incidents/details/965114/home", result.ToString());
    }

    [TestMethod]
    public void IncidentType_UrlValue_PassesThrough()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "https://portal.microsofticm.com/imp/v5/incidents/details/12345/home"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://portal.microsofticm.com/imp/v5/incidents/details/12345/home", result.ToString());
    }

    // ── Doc/Build/Other types ─────────────────────────────────────────────────

    [TestMethod]
    public void DocType_ValidUrl_PassesThrough()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Doc,
            Value = "https://eng.ms/docs/products/arm"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://eng.ms/docs/products/arm", result.ToString());
    }

    [TestMethod]
    public void BuildType_ValidUrl_PassesThrough()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Build,
            Value = "https://dev.azure.com/org/proj/_build/results?buildId=123"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://dev.azure.com/org/proj/_build/results?buildId=123", result.ToString());
    }

    [TestMethod]
    public void OtherType_ValidUrl_PassesThrough()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Other,
            Value = "https://example.com/resource"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://example.com/resource", result.ToString());
    }

    [TestMethod]
    public void DocType_InvalidUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Doc,
            Value = "not a url"
        };

        Assert.IsNull(LinkUriPolicy.Resolve(link, null));
    }

    // ── Unknown type ──────────────────────────────────────────────────────────

    [TestMethod]
    public void UnknownType_ValidUrl_TreatedAsOther()
    {
        var link = new TaskLink
        {
            Type = "future-type",
            Value = "https://example.com/future"
        };

        var result = LinkUriPolicy.Resolve(link, null);

        Assert.IsNotNull(result);
        Assert.AreEqual("https://example.com/future", result.ToString());
    }

    // ── DisplayText ───────────────────────────────────────────────────────────

    [TestMethod]
    public void DisplayText_WithLabel_ReturnsLabel()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Ado,
            Value = "12345",
            Label = "My custom label"
        };

        Assert.AreEqual("My custom label", LinkUriPolicy.DisplayText(link));
    }

    [TestMethod]
    public void DisplayText_AdoType_NoLabel_ReturnsFormattedText()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Ado,
            Value = "12345"
        };

        Assert.AreEqual("ADO #12345", LinkUriPolicy.DisplayText(link));
    }

    [TestMethod]
    public void Resolve_IncidentType_EmbeddedDigits_ReturnsNull()
    {
        // Regex should reject "foo123bar" - digits must be standalone
        var link = new TaskLink { Type = TaskLink.Types.Incident, Value = "foo123bar" };
        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Resolve_IncidentType_DigitsWithExtraText_ReturnsNull()
    {
        // Regex should reject "ICM 123 with extra" - no trailing text allowed
        var link = new TaskLink { Type = TaskLink.Types.Incident, Value = "ICM 123 with extra" };
        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Resolve_PrType_Integer_NoDoubleSlash()
    {
        // Verify ADO PR URL doesn't have double slash after _git
        var link = new TaskLink { Type = TaskLink.Types.Pr, Value = "456" };
        var result = LinkUriPolicy.Resolve(link, "https://dev.azure.com/org/proj");
        Assert.IsNotNull(result);
        Assert.AreEqual("https://dev.azure.com/org/proj/_git/pullrequest/456", result.ToString());
    }

    [TestMethod]
    public void DisplayText_IncidentType_NoLabel_ReturnsFormattedText()
    {
        var link1 = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "ICM 965114"
        };
        var link2 = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "965114"
        };

        Assert.AreEqual("ICM 965114", LinkUriPolicy.DisplayText(link1));
        Assert.AreEqual("ICM 965114", LinkUriPolicy.DisplayText(link2));
    }

    [TestMethod]
    public void DisplayText_PrType_Url_ReturnsHostLabel()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "https://github.com/owner/repo/pull/123"
        };

        var result = LinkUriPolicy.DisplayText(link);
        Assert.Contains("github.com", result, $"Expected host in display text, got: {result}");
    }

    [TestMethod]
    public void DisplayText_PrType_Integer_ReturnsFormattedText()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "456"
        };

        Assert.AreEqual("PR #456", LinkUriPolicy.DisplayText(link));
    }

    [TestMethod]
    public void DisplayText_DocType_ReturnsHostLabel()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Doc,
            Value = "https://eng.ms/docs/products/arm"
        };

        var result = LinkUriPolicy.DisplayText(link);
        Assert.Contains("eng.ms", result, $"Expected host in display text, got: {result}");
    }

    [TestMethod]
    public void DisplayText_BuildType_ReturnsHostLabel()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Build,
            Value = "https://dev.azure.com/org/proj/_build/results?buildId=123"
        };

        var result = LinkUriPolicy.DisplayText(link);
        Assert.Contains("dev.azure.com", result, $"Expected host in display text, got: {result}");
    }

    [TestMethod]
    public void DisplayText_OtherType_ReturnsHostOrValue()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Other,
            Value = "https://example.com/resource"
        };

        var result = LinkUriPolicy.DisplayText(link);
        Assert.Contains("example.com", result, $"Expected host in display text, got: {result}");
    }
}
