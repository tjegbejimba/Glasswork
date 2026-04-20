using System;
using Glasswork.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

/// <summary>
/// Modal editor for a single subtask. Captures all rich fields (text, status, due, blocker,
/// notes, ADO id, my_day) into <see cref="Result"/> for the caller to apply via the
/// reload-mutate-Save pattern. Also exposes <see cref="Promote"/> and <see cref="Delete"/>
/// flags that the caller should action after the dialog closes.
/// </summary>
public sealed partial class SubtaskDetailDialog : ContentDialog
{
    public sealed class EditedValues
    {
        public string Text { get; set; } = string.Empty;
        public string? Status { get; set; }
        public DateTime? Due { get; set; }
        public string? BlockerReason { get; set; }
        public string Notes { get; set; } = string.Empty;
        public int? AdoId { get; set; }
        public bool IsMyDay { get; set; }
        public bool IsCompleted { get; set; }
    }

    public EditedValues Result { get; private set; } = new();
    public bool Promote { get; private set; }
    public bool Delete { get; private set; }

    public SubtaskDetailDialog(SubTask source)
    {
        InitializeComponent();

        TextBox.Text = source.Text ?? string.Empty;
        SetComboByTag(StatusBox, source.Status ?? "todo");
        BlockerBox.Text = source.Metadata.TryGetValue("blocker", out var b) ? b : string.Empty;
        UpdateBlockerVisibility();
        DuePicker.Date = source.Due.HasValue
            ? new DateTimeOffset(source.Due.Value)
            : (DateTimeOffset?)null;

        if (source.Metadata.TryGetValue("ado", out var ado) && int.TryParse(ado, out var adoId))
            AdoBox.Text = adoId.ToString();
        else
            AdoBox.Text = string.Empty;

        NotesBox.Text = source.Notes ?? string.Empty;
        MyDayBox.IsChecked = source.IsMyDay;

        PrimaryButtonClick += OnSave;
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var status = (StatusBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(status) || status == "todo") status = null;

        int? adoId = null;
        var raw = AdoBox.Text?.Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            if (!int.TryParse(raw, out var parsed) || parsed <= 0)
            {
                args.Cancel = true;
                return;
            }
            adoId = parsed;
        }

        Result = new EditedValues
        {
            Text = (TextBox.Text ?? string.Empty).Trim(),
            Status = status,
            Due = DuePicker.Date?.DateTime,
            // Blocker only meaningful when status == blocked; status-leaves-blocked
            // invariant is enforced by the caller as well, but mirror it here.
            BlockerReason = status == "blocked"
                ? (string.IsNullOrWhiteSpace(BlockerBox.Text) ? null : BlockerBox.Text.Trim())
                : null,
            Notes = NotesBox.Text ?? string.Empty,
            AdoId = adoId,
            IsMyDay = MyDayBox.IsChecked == true,
            IsCompleted = status is "done" or "dropped",
        };
    }

    private void StatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateBlockerVisibility();

    private void UpdateBlockerVisibility()
    {
        var tag = (StatusBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        BlockerBox.Visibility = tag == "blocked" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PromoteButton_Click(object sender, RoutedEventArgs e)
    {
        Promote = true;
        Hide();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Delete = true;
        Hide();
    }

    private static void SetComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }
}
