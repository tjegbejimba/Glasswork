using Glasswork.Core.Research;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class LinkWayfinderDialog : ContentDialog
{
    private readonly IResearchCatalog _catalog;
    private readonly string _topicId;

    public LinkWayfinderDialog(
        IResearchCatalog catalog,
        string topicId)
    {
        _catalog = catalog;
        _topicId = topicId;
        InitializeComponent();
    }

    public ResearchRelatedWayfinder? LinkedWayfinder { get; private set; }

    private async void OnLink(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            IsPrimaryButtonEnabled = false;
            var result = await _catalog.LinkExistingWayfinderAsync(
                _topicId,
                IssueReference.Text);
            if (!result.Succeeded)
            {
                args.Cancel = true;
                LinkError.Message = result.Message;
                LinkError.IsOpen = true;
                return;
            }
            LinkedWayfinder = result.Wayfinder;
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }
}
