using Glasswork.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class FeedbackDialog : ContentDialog
{
    public FeedbackDialog()
    {
        InitializeComponent();
    }

    private void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var title = TitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title))
        {
            StatusBar.Message = "Please provide a title.";
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
            args.Cancel = true;
            return;
        }

        var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "General Feedback";
        var body = BodyBox.Text?.Trim() ?? "";

        // Compose a single description block. The triage-issue skill will parse it,
        // investigate the codebase, and file a GitHub issue with proper context.
        var description = string.IsNullOrEmpty(body)
            ? $"[{category}] {title}"
            : $"[{category}] {title}\n\n{body}";

        var invocation = TaskInvocationFormatter.FormatTriageReport(description);

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(invocation);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        StatusBar.Message = "Copied — paste into Copilot CLI to file the issue.";
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.IsOpen = true;
        // Dialog closes normally via the primary button.
    }
}
