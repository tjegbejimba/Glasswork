using System;
using System.IO;
using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class ParentLinkClassifierTests
{
    [TestMethod]
    public void ResolveAsInAppTask_WhenParentMatchesExistingTaskId()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        
        // Write task file directly in vault root (VaultService.LoadAll looks there)
        var taskPath = Path.Combine(tempDir, "task-alpha.md");
        File.WriteAllText(taskPath, @"---
id: task-alpha
title: Alpha task
status: todo
created: 2026-01-01
---

Test task");
        
        try
        {
            // Configure vault and load index
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var indexService = new IndexService(vault);
            indexService.EnsureLoaded();
            
            var classifier = new ParentLinkClassifier(indexService);
            
            // Act
            var result = classifier.Classify("task-alpha", "https://dev.azure.com/org/proj");
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(ParentLinkResolution.ResolutionType.InAppTask, result.Type);
            Assert.AreEqual("task-alpha", result.TaskId);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void ResolveAsAdoUrl_WhenParentIsNumericAdoId()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Empty vault (no matching task)
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var indexService = new IndexService(vault);
            indexService.EnsureLoaded();
            
            var classifier = new ParentLinkClassifier(indexService);
            var adoBaseUrl = "https://dev.azure.com/myorg/myproject";
            
            // Act
            var result = classifier.Classify("12345", adoBaseUrl);
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(ParentLinkResolution.ResolutionType.AdoUrl, result.Type);
            Assert.IsNotNull(result.Url);
            StringAssert.Contains(result.Url, "12345");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void ResolveAsNone_WhenParentIsFreeText()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Empty vault (no matching task)
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var indexService = new IndexService(vault);
            indexService.EnsureLoaded();
            
            var classifier = new ParentLinkClassifier(indexService);
            
            // Act
            var result = classifier.Classify("Some free text parent", null);
            
            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(ParentLinkResolution.ResolutionType.None, result.Type);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
