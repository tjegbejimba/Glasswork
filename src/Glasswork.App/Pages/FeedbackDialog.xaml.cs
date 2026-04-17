using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class FeedbackDialog : ContentDialog
{
    public string? CreatedUrl { get; private set; }

    public FeedbackDialog()
    {
        InitializeComponent();
    }

    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var feedback = App.Feedback;
            if (feedback is null)
            {
                StatusBar.Message = "Feedback not configured — set GLASSWORK_GITHUB_TOKEN env var.";
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

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

            IsPrimaryButtonEnabled = false;
            StatusBar.Message = "Submitting...";
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.IsOpen = true;

            var result = await feedback.SubmitAsync(title, body, category);

            if (result.Success)
            {
                CreatedUrl = result.Url;
                // Dialog will close normally
            }
            else
            {
                StatusBar.Message = result.Error ?? "Unknown error";
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
                IsPrimaryButtonEnabled = true;
                args.Cancel = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
