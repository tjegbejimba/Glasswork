using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class IndexServiceGetChildrenTests
{
    [TestMethod]
    public void GetChildren_ReturnsTasksWhoseParentMatchesId()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write parent task
            var parentPath = Path.Combine(tempDir, "parent-task.md");
            File.WriteAllText(parentPath, @"---
id: parent-task
title: Parent Task
status: todo
created: 2026-01-01
---

Parent task");

            // Write child tasks
            var child1Path = Path.Combine(tempDir, "child-1.md");
            File.WriteAllText(child1Path, @"---
id: child-1
title: Child 1
status: todo
created: 2026-01-02
parent: parent-task
---

Child task 1");

            var child2Path = Path.Combine(tempDir, "child-2.md");
            File.WriteAllText(child2Path, @"---
id: child-2
title: Child 2
status: todo
created: 2026-01-03
parent: parent-task
---

Child task 2");

            // Write unrelated task
            var unrelatedPath = Path.Combine(tempDir, "other-task.md");
            File.WriteAllText(unrelatedPath, @"---
id: other-task
title: Other Task
status: todo
created: 2026-01-04
---

Other task");

            // Load index
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var index = new IndexService(vault);
            index.EnsureLoaded();

            // Act
            var children = index.GetChildren("parent-task");

            // Assert
            Assert.HasCount(2, children, "Should return two children");
            Assert.IsTrue(children.Any(c => c.Id == "child-1"), "Should include child-1");
            Assert.IsTrue(children.Any(c => c.Id == "child-2"), "Should include child-2");
            Assert.IsFalse(children.Any(c => c.Id == "other-task"), "Should not include unrelated task");

            // Verify sorted by title
            var titles = children.Select(c => c.Title).ToList();
            CollectionAssert.AreEqual(new[] { "Child 1", "Child 2" }, titles, "Should be sorted by title");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void GetChildren_ReturnsTaskWhoseParentMatchesParentsAdoId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "parent-task.md"), """
                ---
                id: parent-task
                title: Parent Task
                status: todo
                type: pbi
                created: 2026-01-01
                links:
                  - type: ado
                    value: 36083004
                ---
                """);

            File.WriteAllText(Path.Combine(tempDir, "child-task.md"), """
                ---
                id: child-task
                title: Child Task
                status: todo
                created: 2026-01-02
                parent: 36083004
                ---
                """);

            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var index = new IndexService(vault);
            index.EnsureLoaded();

            var children = index.GetChildren("parent-task");

            Assert.HasCount(1, children);
            Assert.AreEqual("child-task", children[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void GetChildren_ReturnsEmptyListWhenNoChildren()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write task with no children
            var parentPath = Path.Combine(tempDir, "parent-task.md");
            File.WriteAllText(parentPath, @"---
id: parent-task
title: Parent Task
status: todo
created: 2026-01-01
---

Parent task");

            // Load index
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var index = new IndexService(vault);
            index.EnsureLoaded();

            // Act
            var children = index.GetChildren("parent-task");

            // Assert
            Assert.IsEmpty(children, "Should return empty list when no children exist");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void GetChildren_UsesTrimmedParentMatching()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write parent task
            var parentPath = Path.Combine(tempDir, "parent-task.md");
            File.WriteAllText(parentPath, @"---
id: parent-task
title: Parent Task
status: todo
created: 2026-01-01
---

Parent task");

            // Write child with whitespace in parent field
            var childPath = Path.Combine(tempDir, "child-with-space.md");
            File.WriteAllText(childPath, @"---
id: child-with-space
title: Child With Space
status: todo
created: 2026-01-02
parent: ' parent-task '
---

Child task with whitespace in parent");

            // Load index
            var coordinator = new SelfWriteCoordinator(tempDir);
            var vault = new VaultService(tempDir, coordinator);
            var index = new IndexService(vault);
            index.EnsureLoaded();

            // Act
            var children = index.GetChildren("parent-task");

            // Assert
            Assert.HasCount(1, children, "Should match parent even with whitespace");
            Assert.AreEqual("child-with-space", children[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
