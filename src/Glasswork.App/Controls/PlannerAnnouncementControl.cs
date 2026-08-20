using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Controls;

public sealed class PlannerAnnouncementControl : Control
{
    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            var previous = _text;
            _text = value;
            if (FrameworkElementAutomationPeer.FromElement(this)
                is PlannerAnnouncementAutomationPeer peer)
            {
                peer.AnnounceTextChange(previous, value);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new PlannerAnnouncementAutomationPeer(this);
}

internal sealed class PlannerAnnouncementAutomationPeer
    : FrameworkElementAutomationPeer
{
    private readonly PlannerAnnouncementControl _owner;

    public PlannerAnnouncementAutomationPeer(PlannerAnnouncementControl owner)
        : base(owner)
    {
        _owner = owner;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Text;

    protected override string GetClassNameCore() => nameof(PlannerAnnouncementControl);

    protected override string GetNameCore() => _owner.Text;

    protected override AutomationLiveSetting GetLiveSettingCore() =>
        AutomationLiveSetting.Polite;

    public void AnnounceTextChange(string previous, string current)
    {
        RaisePropertyChangedEvent(
            AutomationElementIdentifiers.NameProperty,
            previous,
            current);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
