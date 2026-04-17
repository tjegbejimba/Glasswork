using System.Text.Json;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class AdoServiceTests
{
    [TestMethod]
    public void ParseWorkItem_ExtractsAllFields()
    {
        var json = """
        {
            "id": 42,
            "url": "https://dev.azure.com/org/proj/_apis/wit/workitems/42",
            "fields": {
                "System.Title": "Fix login flow",
                "System.State": "Active",
                "System.WorkItemType": "User Story",
                "System.AssignedTo": {
                    "displayName": "TJ Egbejimba",
                    "uniqueName": "toegbeji@microsoft.com"
                },
                "System.AreaPath": "MyProject\\Team",
                "System.IterationPath": "MyProject\\Sprint 5"
            }
        }
        """;

        var element = JsonDocument.Parse(json).RootElement;
        var item = AdoService.ParseWorkItem(element);

        Assert.AreEqual(42, item.Id);
        Assert.AreEqual("Fix login flow", item.Title);
        Assert.AreEqual("Active", item.State);
        Assert.AreEqual("User Story", item.WorkItemType);
        Assert.AreEqual("TJ Egbejimba", item.AssignedTo);
        Assert.AreEqual("MyProject\\Team", item.AreaPath);
        Assert.AreEqual("MyProject\\Sprint 5", item.IterationPath);
        Assert.IsTrue(item.Url.Contains("42"));
    }

    [TestMethod]
    public void ParseWorkItem_HandlesStringAssignedTo()
    {
        var json = """
        {
            "id": 99,
            "fields": {
                "System.Title": "Simple task",
                "System.State": "New",
                "System.WorkItemType": "Task",
                "System.AssignedTo": "user@example.com"
            }
        }
        """;

        var element = JsonDocument.Parse(json).RootElement;
        var item = AdoService.ParseWorkItem(element);

        Assert.AreEqual("user@example.com", item.AssignedTo);
    }

    [TestMethod]
    public void ParseWorkItem_HandlesMissingFields()
    {
        var json = """
        {
            "id": 1,
            "fields": {
                "System.Title": "Minimal"
            }
        }
        """;

        var element = JsonDocument.Parse(json).RootElement;
        var item = AdoService.ParseWorkItem(element);

        Assert.AreEqual(1, item.Id);
        Assert.AreEqual("Minimal", item.Title);
        Assert.AreEqual("", item.State);
        Assert.AreEqual("", item.WorkItemType);
    }
}
