// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.AppUpdate;
using Glasswork.Core.Services;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Glasswork.Pages;

public sealed partial class SettingsPage : Page
{
    /// <summary>
    /// Navigation parameter that asks the page to surface the "Updates" section
    /// (bring it into view + focus the check button). Used by the announce
    /// surfaces' "Go to Settings" routes (issue #241).
    /// </summary>
    public const string UpdatesSectionParameter = "updates";

    public SettingsPage()
    {
        InitializeComponent();
        AdoBaseUrlBox.Text = App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;

        var theme = (App.UiState.Get<string>(App.ThemeKey) ?? "system").ToLowerInvariant();
        ThemeComboBox.SelectedIndex = theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        RefreshVaultInfo();
        RefreshUpdateInfo();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter as string == UpdatesSectionParameter)
        {
            // Defer until layout is ready: StartBringIntoView/Focus from OnNavigatedTo can
            // run before the ScrollViewer has measured, making the scroll a no-op.
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdatesSection?.StartBringIntoView();
                CheckForUpdatesButton?.Focus(FocusState.Programmatic);
            });
        }
    }

    // ── Vault ────────────────────────────────────────────────────────────────

    private void RefreshVaultInfo()
    {
        var path = App.VaultRoot;
        VaultPathBox.Text = path;

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            VaultInfoText.Text = string.Empty;
            return;
        }

        try
        {
            // Count via the in-memory aggregate (issue #184) — O(1) read, no
            // disk scan, no cloning. Use Count directly rather than Tasks.Count
            // to avoid materializing the dictionary.
            var taskCount = App.Index?.Count ?? 0;
            var lastWrite = Directory.GetLastWriteTime(path);
            VaultInfoText.Text = $"{taskCount} task file{(taskCount == 1 ? "" : "s")} · last modified {lastWrite:g}";
        }
        catch
        {
            VaultInfoText.Text = string.Empty;
        }
    }

    private async void SwitchVaultButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        // WinRT requires at least one entry in FileTypeFilter even for folder pickers.
        picker.FileTypeFilter.Add("*");

        // WinUI 3 requires the picker to be associated with the window handle.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var chosenPath = folder.Path;
        var validationResult = VaultValidator.Validate(chosenPath);

        VaultWarningBar.IsOpen = false;

        if (validationResult == VaultValidationResult.NotFound)
        {
            VaultWarningBar.Title = "Folder not found";
            VaultWarningBar.Message = $"'{chosenPath}' does not exist or could not be read.";
            VaultWarningBar.Severity = InfoBarSeverity.Error;
            VaultWarningBar.IsOpen = true;
            return;
        }

        if (validationResult == VaultValidationResult.HasMarkdownFiles)
        {
            VaultWarningBar.Title = "No .obsidian folder found";
            VaultWarningBar.Message =
                "This folder contains .md files but no .obsidian directory. " +
                "It may not be an Obsidian vault. The vault has been set anyway.";
            VaultWarningBar.Severity = InfoBarSeverity.Warning;
            VaultWarningBar.IsOpen = true;
        }
        else if (validationResult == VaultValidationResult.Empty)
        {
            VaultWarningBar.Title = "Folder looks empty";
            VaultWarningBar.Message =
                "This folder contains no .md files and no .obsidian directory. " +
                "Make sure this is the right location. The vault has been set anyway.";
            VaultWarningBar.Severity = InfoBarSeverity.Warning;
            VaultWarningBar.IsOpen = true;
        }

        App.SwitchVault(chosenPath);
        RefreshVaultInfo();

        // Update the status bar vault path text.
        if (App.MainWindow is MainWindow mw)
            mw.RefreshStatusBar();
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        var value = item.Tag as string ?? "system";
        var existing = (App.UiState.Get<string>(App.ThemeKey) ?? "system").ToLowerInvariant();
        if (value == existing) return;

        if (value == "system")
            App.UiState.Remove(App.ThemeKey);
        else
            App.UiState.Set(App.ThemeKey, value);

        if (App.MainWindow is not null) App.ApplyTheme(App.MainWindow);
    }

    // ── Azure DevOps ─────────────────────────────────────────────────────────

    private void AdoBaseUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var trimmed = (AdoBaseUrlBox.Text ?? string.Empty).Trim();
        var existing = App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;
        if (trimmed == existing) return;
        if (string.IsNullOrEmpty(trimmed))
        {
            App.UiState.Remove(App.AdoBaseUrlKey);
        }
        else
        {
            App.UiState.Set(App.AdoBaseUrlKey, trimmed);
        }
    }

    // ── Updates ──────────────────────────────────────────────────────────────

    private void RefreshUpdateInfo()
    {
        InstalledVersionText.Text = $"Installed version: {App.Updater.InstalledVersion}";
        UpdateStatusText.Text = UpdateStatusPresenter.Describe(App.Updater.LastResult);
        RestartToUpdateButton.IsEnabled = App.Updater.LastResult?.IsUpdateAvailable == true;

        var mcpResult = App.McpUpdater.LastResult;
        InstalledMcpVersionText.Text = mcpResult?.InstalledVersion is not null
            ? $"Installed version: {mcpResult.InstalledVersion}"
            : mcpResult?.IsInstalled == true
            ? "Installed version: Legacy build"
            : "Installed version: Not installed";
        McpUpdateStatusText.Text = DescribeMcpUpdate(mcpResult);
        UpdateMcpButton.IsEnabled = mcpResult?.IsUpdateAvailable == true;
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";

        try
        {
            var appCheck = App.Updater.CheckForUpdatesAsync();
            var mcpCheck = App.McpUpdater.CheckForUpdatesAsync();
            await Task.WhenAll(appCheck, mcpCheck);
            UpdateStatusText.Text = UpdateStatusPresenter.Describe(appCheck.Result);
            // Re-evaluate the restart button so it lights up once an update is found.
            RefreshUpdateInfo();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            UpdateStatusText.Text = UpdateStatusPresenter.CheckFailedMessage;
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void UpdateMcpButton_Click(object sender, RoutedEventArgs e)
    {
        string? updaterDirectory = null;
        UpdateMcpButton.IsEnabled = false;
        McpUpdateStatusText.Text = "Installing verified MCP update…";

        try
        {
            var bundledUpdaterDirectory = Path.Combine(AppContext.BaseDirectory, "McpUpdater");
            updaterDirectory = Path.Combine(
                Path.GetTempPath(),
                "Glasswork",
                $"mcp-updater-{Guid.NewGuid():N}");
            Directory.CreateDirectory(updaterDirectory);

            foreach (var fileName in new[]
                     {
                         "install-mcp.ps1",
                         "Install-McpTool.ps1",
                         "Validate-McpReleasePublication.ps1",
                     })
            {
                File.Copy(
                    Path.Combine(bundledUpdaterDirectory, fileName),
                    Path.Combine(updaterDirectory, fileName));
            }

            var installerScriptPath = Path.Combine(updaterDirectory, "install-mcp.ps1");
            var plan = new McpUpdateLauncher().CreatePlan(
                isUpdateAvailable: App.McpUpdater.LastResult?.IsUpdateAvailable == true,
                availableVersion: App.McpUpdater.LastResult?.AvailableVersion?.ToString(),
                installerScriptPath: installerScriptPath,
                executableResolver: new PwshExecutableResolver(),
                fileExists: File.Exists,
                workingDirectory: updaterDirectory);
            if (plan.IsOpenReleasePage || plan.ProcessSpec is null)
            {
                OpenMcpReleasePage(App.McpUpdater.LastResult?.AvailableVersion?.ToString());
                return;
            }

            var spec = plan.ProcessSpec;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = spec.FileName,
                    CreateNoWindow = spec.CreateNoWindow,
                    UseShellExecute = spec.UseShellExecute,
                    WorkingDirectory = spec.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var arg in spec.ArgumentList)
                process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start())
                throw new InvalidOperationException("The MCP updater process did not start.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "MCP update failed." : error);
            }

            await App.McpUpdater.CheckForUpdatesAsync();
            RefreshUpdateInfo();
            if (!string.IsNullOrWhiteSpace(output))
                McpUpdateStatusText.Text = output;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP update failed: {ex.Message}");
            McpUpdateStatusText.Text = $"MCP update failed: {ex.Message}";
        }
        finally
        {
            if (updaterDirectory is not null)
                DeleteUpdaterDirectory(updaterDirectory);
            UpdateMcpButton.IsEnabled = App.McpUpdater.LastResult?.IsUpdateAvailable == true;
        }
    }

    private void RestartToUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        string? updaterDirectory = null;

        try
        {
            var bundledUpdaterDirectory = Path.Combine(AppContext.BaseDirectory, "Updater");
            updaterDirectory = Path.Combine(
                Path.GetTempPath(),
                "Glasswork",
                $"updater-{Guid.NewGuid():N}");
            Directory.CreateDirectory(updaterDirectory);

            foreach (var fileName in new[] { "release-update.ps1", "Invoke-ReleaseUpdate.ps1" })
            {
                File.Copy(
                    Path.Combine(bundledUpdaterDirectory, fileName),
                    Path.Combine(updaterDirectory, fileName));
            }

            var updaterScriptPath = Path.Combine(updaterDirectory, "release-update.ps1");
            var plan = new SelfUpdateLauncher().CreatePackagedPlan(
                isUpdateAvailable: App.Updater.LastResult?.IsUpdateAvailable == true,
                availableVersion: App.Updater.LastResult?.AvailableVersion?.ToString(),
                updaterScriptPath: updaterScriptPath,
                updaterCleanupDirectory: updaterDirectory,
                installExePath: Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty,
                processId: Environment.ProcessId,
                executableResolver: new PwshExecutableResolver(),
                fileExists: File.Exists,
                workingDirectory: Path.GetTempPath());

            if (plan.IsOpenReleasePage || plan.ProcessSpec is null)
            {
                DeleteUpdaterDirectory(updaterDirectory);
                updaterDirectory = null;
                OpenReleasePage(App.Updater.LastResult?.AvailableVersion?.ToString());
                return;
            }

            var spec = plan.ProcessSpec;
            var psi = new ProcessStartInfo
            {
                FileName = spec.FileName,
                CreateNoWindow = spec.CreateNoWindow,
                UseShellExecute = spec.UseShellExecute,
                WorkingDirectory = spec.WorkingDirectory,
            };
            foreach (var arg in spec.ArgumentList)
                psi.ArgumentList.Add(arg);

            if (Process.Start(psi) is null)
                throw new InvalidOperationException("The updater process did not start.");

            // Exit only after the detached updater has started so it can replace the install.
            updaterDirectory = null;
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Self-update spawn failed: {ex.Message}");
            if (updaterDirectory is not null)
                DeleteUpdaterDirectory(updaterDirectory);
            OpenReleasePage(App.Updater.LastResult?.AvailableVersion?.ToString());
        }
    }

    private static void DeleteUpdaterDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to clean up updater files: {ex.Message}");
        }
    }

    private static void OpenReleasePage(string? version)
    {
        var url = string.IsNullOrWhiteSpace(version)
            ? "https://github.com/tjegbejimba/Glasswork/releases"
            : $"https://github.com/tjegbejimba/Glasswork/releases/tag/v{version}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open release page: {ex.Message}");
        }
    }

    private static string DescribeMcpUpdate(McpUpdateCheckResult? result)
    {
        if (result is null)
            return "Not checked yet.";
        if (result.IsCheckFailed)
            return "Unable to check glasswork-mcp releases.";
        if (result.IsUpdateAvailable && !result.IsInstalled)
            return $"Version {result.AvailableVersion} is available to install.";
        if (result.IsUpdateAvailable)
            return $"Version {result.AvailableVersion} is available.";
        return "glasswork-mcp is up to date.";
    }

    private static void OpenMcpReleasePage(string? version)
    {
        var url = string.IsNullOrWhiteSpace(version)
            ? "https://github.com/tjegbejimba/Glasswork/releases"
            : $"https://github.com/tjegbejimba/Glasswork/releases/tag/mcp-v{version}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open MCP release page: {ex.Message}");
        }
    }
}
