using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using MuxWebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace Glasswork.Services;

/// <summary>
/// Outcome of <see cref="HtmlPreviewService.ActivateAsync"/>.
/// </summary>
public enum HtmlPreviewActivation
{
    /// <summary>A sandboxed preview was created and navigation started.</summary>
    Activated,

    /// <summary>The WebView2 runtime is unavailable; caller should fall back to Source.</summary>
    RuntimeMissing,

    /// <summary>A newer activation superseded this one while it was initializing.</summary>
    Superseded,
}

/// <summary>
/// Owns the single, app-wide live <see cref="MuxWebView2"/> used for sandboxed
/// HTML artifact preview (#324). Only one preview can be active at a time:
/// activating a second tears the first down and notifies it via its
/// <c>onEvicted</c> callback ("Preview closed — another preview is active").
///
/// A fresh WebView2 is created on every activation and disposed on the next
/// activation, on <see cref="Release"/>, or on <see cref="ReleaseAll"/>. The
/// instance is never re-parented between hosts (re-parenting inside a list is
/// fragile and leaks visual-tree references). All methods must be called on the
/// UI thread.
///
/// Sandbox hardening, applied before navigating: script, host objects, web
/// messages, script dialogs, and dev tools are all disabled; new windows,
/// permission requests, and every web-resource request are blocked; and any
/// navigation after the initial <see cref="CoreWebView2.NavigateToString"/> is
/// cancelled. The document is supplied inline, so every web-resource request is
/// an external subresource and is denied — local structure/text renders while
/// external fetches and navigations do not.
/// </summary>
public sealed class HtmlPreviewService
{
    private ContentControl? _activeHost;
    private Action? _activeOnEvicted;
    private MuxWebView2? _webView;
    private CoreWebView2? _core;
    private bool _sawInitialNav;

    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navStarting;
    private TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs>? _newWindow;
    private TypedEventHandler<CoreWebView2, CoreWebView2PermissionRequestedEventArgs>? _permission;
    private TypedEventHandler<CoreWebView2, CoreWebView2WebResourceRequestedEventArgs>? _resource;

    /// <summary>
    /// Activates a sandboxed preview of <paramref name="html"/> inside
    /// <paramref name="host"/>. If a different host is currently previewing, it
    /// is torn down first and its <paramref name="onEvicted"/> equivalent is
    /// invoked. <paramref name="onEvicted"/> is stored for this host and called
    /// if a later activation evicts it.
    /// </summary>
    public async Task<HtmlPreviewActivation> ActivateAsync(ContentControl host, string html, Action onEvicted)
    {
        if (_activeHost is not null && !ReferenceEquals(_activeHost, host))
        {
            var prior = _activeOnEvicted;
            DisposeWebView();
            _activeHost = null;
            _activeOnEvicted = null;
            prior?.Invoke();
        }
        else if (ReferenceEquals(_activeHost, host))
        {
            DisposeWebView();
        }

        _activeHost = host;
        _activeOnEvicted = onEvicted;

        var webView = new MuxWebView2();
        _webView = webView;
        host.Content = webView;

        try
        {
            await webView.EnsureCoreWebView2Async();
        }
        catch (Exception)
        {
            if (ReferenceEquals(_webView, webView))
            {
                DisposeWebView();
                if (ReferenceEquals(host.Content, webView))
                {
                    host.Content = null;
                }

                _activeHost = null;
                _activeOnEvicted = null;
            }

            return HtmlPreviewActivation.RuntimeMissing;
        }

        if (!ReferenceEquals(_webView, webView))
        {
            try { webView.Close(); } catch { /* best effort */ }
            return HtmlPreviewActivation.Superseded;
        }

        var core = webView.CoreWebView2;
        _core = core;
        _sawInitialNav = false;

        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        _navStarting = (_, e) =>
        {
            if (_sawInitialNav)
            {
                e.Cancel = true;
            }
            else
            {
                _sawInitialNav = true;
            }
        };
        core.NavigationStarting += _navStarting;

        _newWindow = (_, e) => e.Handled = true;
        core.NewWindowRequested += _newWindow;

        _permission = (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.PermissionRequested += _permission;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _resource = (_, e) =>
        {
            try
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
            catch
            {
                // Best effort: if a response can't be synthesized, the request still
                // cannot run script and external navigation is already cancelled.
            }
        };
        core.WebResourceRequested += _resource;

        core.NavigateToString(html);
        return HtmlPreviewActivation.Activated;
    }

    /// <summary>Tears down the preview if <paramref name="host"/> is the active one.</summary>
    public void Release(ContentControl host)
    {
        if (!ReferenceEquals(_activeHost, host))
        {
            return;
        }

        var webView = _webView;
        DisposeWebView();
        if (ReferenceEquals(host.Content, webView))
        {
            host.Content = null;
        }

        _activeHost = null;
        _activeOnEvicted = null;
    }

    /// <summary>Tears down any active preview. Does not invoke the eviction callback.</summary>
    public void ReleaseAll()
    {
        if (_activeHost is null)
        {
            return;
        }

        var host = _activeHost;
        DisposeWebView();
        if (host.Content is MuxWebView2)
        {
            host.Content = null;
        }

        _activeHost = null;
        _activeOnEvicted = null;
    }

    private void DisposeWebView()
    {
        if (_core is not null)
        {
            if (_navStarting is not null) _core.NavigationStarting -= _navStarting;
            if (_newWindow is not null) _core.NewWindowRequested -= _newWindow;
            if (_permission is not null) _core.PermissionRequested -= _permission;
            if (_resource is not null) _core.WebResourceRequested -= _resource;
        }

        _navStarting = null;
        _newWindow = null;
        _permission = null;
        _resource = null;
        _core = null;
        _sawInitialNav = false;

        if (_webView is not null)
        {
            try { _webView.Close(); } catch { /* best effort */ }
            _webView = null;
        }
    }
}
