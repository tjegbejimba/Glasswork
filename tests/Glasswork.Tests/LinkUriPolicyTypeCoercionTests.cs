using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class LinkUriPolicyTypeCoercionTests
{
    [TestMethod]
    public void UnknownType_NonUrlValue_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = "future-unknown-type",
            Value = "not a url"
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result, "Unknown type with non-URL value should return null (treated as 'other')");
    }

    [TestMethod]
    public void UnknownType_EmptyValue_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = "future-unknown-type",
            Value = ""
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void UnknownType_WhitespaceValue_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = "future-unknown-type",
            Value = "   "
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void DisplayText_UnknownType_ValidUrl_ShowsHost()
    {
        var link = new TaskLink
        {
            Type = "future-unknown-type",
            Value = "https://example.com/path"
        };

        var display = LinkUriPolicy.DisplayText(link);
        Assert.Contains("example.com", display, $"Unknown type should show host, got: {display}");
    }

    [TestMethod]
    public void DisplayText_UnknownType_NonUrl_ShowsValue()
    {
        var link = new TaskLink
        {
            Type = "future-unknown-type",
            Value = "some identifier"
        };

        var display = LinkUriPolicy.DisplayText(link);
        Assert.AreEqual("some identifier", display);
    }
}
