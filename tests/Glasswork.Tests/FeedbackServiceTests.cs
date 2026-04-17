using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class FeedbackServiceTests
{
    [TestMethod]
    public void Constructor_SetsOwnerAndRepo()
    {
        // Verify service can be created without a token (unauthenticated mode)
        var service = new FeedbackService("testowner", "testrepo");
        Assert.IsNotNull(service);
    }

    [TestMethod]
    public async Task Submit_WithoutToken_ReturnsError()
    {
        // No token means GitHub API will return 401/404
        var service = new FeedbackService("tjegbejimba", "Glasswork");
        var result = await service.SubmitAsync("Test", "Test body", "Bug");

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
    }
}
