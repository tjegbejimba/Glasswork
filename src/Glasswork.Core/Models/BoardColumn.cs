using System.Collections.Generic;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents one column in the board view of the backlog.
/// Contains the column name (e.g., "To Do", "In Progress") and the filtered/sorted tasks.
/// </summary>
public sealed class BoardColumn
{
    public string ColumnName { get; }
    public List<GlassworkTask> Tasks { get; }

    public BoardColumn(string columnName, List<GlassworkTask> tasks)
    {
        ColumnName = columnName;
        Tasks = tasks;
    }
}
