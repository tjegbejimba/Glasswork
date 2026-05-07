using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class LinkUriPolicyUriSafetyTests
{
    [TestMethod]
    public void ResolveUrl_MalformedUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Other,
            Value = "ht!tp://not a valid url"
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result, "Malformed URL should return null");
    }

    [TestMethod]
    public void ResolvePr_MalformedUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "http://[invalid"
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result, "Malformed PR URL should return null");
    }

    [TestMethod]
    public void ResolveIncident_MalformedUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Incident,
            Value = "http://[::1"
        };

        var result = LinkUriPolicy.Resolve(link, null);
        Assert.IsNull(result, "Malformed incident URL should return null");
    }

    [TestMethod]
    public void ResolveAdo_MalformedBaseUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Ado,
            Value = "12345"
        };

        var result = LinkUriPolicy.Resolve(link, "not a url at all!");
        Assert.IsNull(result, "Malformed ADO base URL should return null");
    }

    [TestMethod]
    public void ResolvePr_MalformedAdoBaseUrl_ReturnsNull()
    {
        var link = new TaskLink
        {
            Type = TaskLink.Types.Pr,
            Value = "456"
        };

        var result = LinkUriPolicy.Resolve(link, "ht!tp://broken");
        Assert.IsNull(result, "Malformed ADO base URL for PR should return null");
    }
}
