using System;
using System.Collections.Generic;
using Glasswork.Services;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class FeedbackDialog : ContentDialog
{
    private readonly GhCliIssueFiler _filer;

    public FeedbackDialog() : this(new GhCliIssueFiler())
    {
    }

    internal FeedbackDialog(GhCliIssueFiler filer)
    {
        _filer = filer;
        InitializeComponent();
    }

    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
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
        var labels = LabelsFor(category);

        // Keep the dialog open during the shell-out so the user sees progress
        // and we can re-enable the form on failure without re-opening.
        var deferral = args.GetDeferral();
        try
        {
            IsPrimaryButtonEnabled = false;
            StatusBar.Message = "Filing issue on GitHub…";
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.IsOpen = true;

            var issueBody = BuildIssueBody(category, body);
            var result = await _filer.TryFileIssueAsync(title, issueBody, labels);

            if (result.Succeeded)
            {
                StatusBar.Message = $"Filed: {result.IssueUrl}";
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.IsOpen = true;
                // Let the dialog close normally via the primary button.
            }
            else
            {
                StatusBar.Message = result.ErrorMessage ?? "Failed to file issue.";
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
                args.Cancel = true; // keep dialog open so user can retry or copy text
            }
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private static IReadOnlyList<string> LabelsFor(string category) => category switch
    {
        // Every issue filed from the dialog gets 'user-report' so the
        // auto-triage GitHub Action fires. Category-specific labels stack on top.
        "Bug" => new[] { "user-report", "bug" },
        "Feature Request" => new[] { "user-report", "feature" },
        _ => new[] { "user-report" },
    };

    private static string BuildIssueBody(string category, string body)
    {
        // Tag provenance so triage can tell real bugs from feature requests without
        // re-reading the title, and preserve the user's body verbatim below.
        var header = $"_Filed from Glasswork feedback dialog — category: **{category}**_";
        return string.IsNullOrEmpty(body) ? header : $"{header}\n\n{body}";
    }
}
