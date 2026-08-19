using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Glasswork.Core.Services;
using Glasswork.Core.VisualVerification;

var exitCode = await VisualVerificationRunner.RunAsync(args);
return exitCode;

internal static partial class VisualVerificationRunner
{
    private const string AppRelativePath = @"src\Glasswork.App\Glasswork.csproj";
    private const string AppExeRelativePath = @"src\Glasswork.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Glasswork.exe";

    public static async Task<int> RunAsync(string[] args)
    {
        // Declare Per-Monitor-V2 DPI awareness before any window/UIA/DWM call so
        // GetWindowRect/DwmGetWindowAttribute report physical pixels that match
        // UIA's BoundingRectangle and the PrintWindow capture. Without this, a
        // DPI-unaware process gets virtualized window rects on >100% displays and
        // the inspection catalog's bounds would not line up with the screenshot.
        EnsureDpiAware();

        try
        {
            var options = RunnerOptions.Parse(args);
            var scenario = VisualVerificationScenario.FromFile(options.ScenarioPath);
            Directory.CreateDirectory(options.OutDir);

            if (!options.NoBuild)
                await RunProcessAsync("dotnet", $"build \"{Path.Combine(options.RepoRoot, AppRelativePath)}\" -c Debug -p:Platform=x64 --nologo -v quiet -tl:off", options.RepoRoot);

            var appExe = Path.Combine(options.RepoRoot, AppExeRelativePath);
            if (!File.Exists(appExe))
                throw new FileNotFoundException($"Glasswork dev executable not found. Build first or remove --no-build. Expected: {appExe}", appExe);

            var workDir = Path.Combine(Path.GetTempPath(), "glasswork-visual-work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                var result = await RunScenarioAsync(scenario, options, appExe, workDir);
                var resultPath = Path.Combine(options.OutDir, "result.json");
                File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(resultPath);
                return 0;
            }
            finally
            {
                if (!options.KeepWorkingDirectory && Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<VerificationResult> RunScenarioAsync(
        VisualVerificationScenario scenario,
        RunnerOptions options,
        string appExe,
        string workDir)
    {
        var vaultRoot = Path.Combine(workDir, "Vault");
        var todoPath = Path.Combine(vaultRoot, "wiki", "todo");
        var uiStatePath = Path.Combine(workDir, "ui-state.json");
        var captureRequestPath = Path.Combine(workDir, "capture.request");
        var captureOutputPath = Path.Combine(workDir, "capture.png");
        var wayfinderFixturePath = Path.Combine(workDir, "wayfinder-fixture.json");
        Directory.CreateDirectory(todoPath);

        MaterializeVault(scenario, vaultRoot, todoPath);
        File.WriteAllText(
            wayfinderFixturePath,
            JsonSerializer.Serialize(scenario.WayfinderIssues));
        File.WriteAllText(
            uiStatePath,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["app.theme"] = scenario.Theme,
            }));

        var instanceKey = "visual-" + Guid.NewGuid().ToString("N");
        using var process = LaunchApp(
            appExe,
            scenario.StartUri,
            vaultRoot,
            uiStatePath,
            instanceKey,
            captureRequestPath,
            captureOutputPath,
            wayfinderFixturePath);

        try
        {
            var hwnd = WaitForWindow(process, TimeSpan.FromSeconds(scenario.LaunchTimeoutSeconds));
            ResizeWindow(hwnd, scenario.WindowWidth, scenario.WindowHeight);
            await Task.Delay(scenario.InitialWaitMilliseconds);

            foreach (var action in scenario.Actions)
            {
                try
                {
                    PerformAction(hwnd, vaultRoot, todoPath, action);
                }
                catch (Exception ex)
                {
                    var selector = action.AutomationId ?? action.Name ?? action.Value ?? "(none)";
                    throw new InvalidOperationException(
                        $"Action '{action.Type}' for '{selector}' failed: {ex.Message}",
                        ex);
                }
            }

            var inspection = options.Inspect
                ? EmitInspection(
                    hwnd,
                    scenario,
                    options.OutDir,
                    captureRequestPath,
                    captureOutputPath)
                : null;

            var captures = new List<CaptureResult>();
            foreach (var capture in scenario.Captures)
            {
                if (capture.WaitMilliseconds > 0)
                    await Task.Delay(capture.WaitMilliseconds);

                var path = Path.Combine(options.OutDir, SanitizeFileName(capture.Name) + ".png");
                CaptureThroughApp(
                    captureRequestPath,
                    captureOutputPath,
                    path,
                    TimeSpan.FromSeconds(10));
                var imageStats = AnalyzeImage(path);
                if (imageStats.UniqueSampledColors <= 1)
                    throw new InvalidOperationException($"Capture '{capture.Name}' appears blank or uniform: {path}");

                captures.Add(new CaptureResult(capture.Name, path, imageStats.Width, imageStats.Height, imageStats.UniqueSampledColors));
            }

            return new VerificationResult(
                scenario.Name,
                options.OutDir,
                vaultRoot,
                uiStatePath,
                instanceKey,
                captures,
                inspection?.InspectionPath,
                inspection?.SuggestedScenarioPath);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void MaterializeVault(VisualVerificationScenario scenario, string vaultRoot, string todoPath)
    {
        var vault = new VaultService(todoPath);
        var today = DateTime.Today;

        foreach (var task in scenario.Tasks)
        {
            vault.Save(task.ToGlassworkTask(today));

            if (task.Artifacts.Count == 0) continue;
            var artifactsDir = Path.Combine(vaultRoot, "wiki", "todo", task.Id + ".artifacts");
            Directory.CreateDirectory(artifactsDir);
            foreach (var artifact in task.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.Name))
                    throw new FormatException($"Artifact on task '{task.Id}' requires a non-empty name.");

                // Use the name verbatim so scenarios can seed any extension
                // (.md/.html/.txt/.svg/.png/...). Names with no extension default
                // to .md to preserve the original markdown-only behavior.
                var fileName = Path.HasExtension(artifact.Name)
                    ? artifact.Name
                    : artifact.Name + ".md";
                var fullPath = Path.Combine(artifactsDir, SanitizeFileName(fileName));

                if (!string.IsNullOrEmpty(artifact.Base64))
                {
                    File.WriteAllBytes(fullPath, Convert.FromBase64String(artifact.Base64));
                }
                else
                {
                    var text = artifact.Content ?? artifact.Markdown;
                    File.WriteAllText(fullPath, text);
                }
            }
        }

        var wikiRoot = Path.Combine(vaultRoot, "wiki");
        foreach (var page in scenario.WikiPages)
        {
            var relativePath = page.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(wikiRoot, relativePath));
            if (!fullPath.StartsWith(
                    wikiRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    $"Wiki Page path '{page.RelativePath}' escapes the scenario Vault.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var lines = new List<string>
            {
                "---",
                $"id: {YamlScalar(page.Id)}",
                $"title: {YamlScalar(page.Title)}",
                $"type: {YamlScalar(page.Type)}",
            };
            if (!string.IsNullOrWhiteSpace(page.Confidence))
                lines.Add($"confidence: {YamlScalar(page.Confidence)}");
            if (!string.IsNullOrWhiteSpace(page.Updated))
                lines.Add($"updated: {YamlScalar(page.Updated)}");
            if (!string.IsNullOrWhiteSpace(page.Expires))
                lines.Add($"expires: {YamlScalar(page.Expires)}");
            if (page.Sources.Count > 0)
            {
                lines.Add("sources:");
                lines.AddRange(page.Sources.Select(source => $"  - {YamlScalar(source)}"));
            }
            if (page.OptedIn)
            {
                lines.Add("glasswork:");
                if (page.ResearchInclude.Count == 0
                    && page.ResearchExclude.Count == 0
                    && page.ResearchRelatedWork.Count == 0
                    && page.ResearchRelatedWayfinder.Count == 0)
                {
                    lines.Add("  research: {}");
                }
                else
                {
                    lines.Add("  research:");
                    if (page.ResearchInclude.Count > 0)
                    {
                        lines.Add(
                            $"    include: [{string.Join(", ", page.ResearchInclude.Select(YamlScalar))}]");
                    }
                    if (page.ResearchExclude.Count > 0)
                    {
                        lines.Add(
                            $"    exclude: [{string.Join(", ", page.ResearchExclude.Select(YamlScalar))}]");
                    }
                    if (page.ResearchRelatedWork.Count > 0)
                    {
                        lines.Add(
                            $"    related_work: [{string.Join(", ", page.ResearchRelatedWork.Select(YamlScalar))}]");
                    }
                    if (page.ResearchRelatedWayfinder.Count > 0)
                    {
                        lines.Add(
                            $"    related_wayfinder: [{string.Join(", ", page.ResearchRelatedWayfinder.Select(YamlScalar))}]");
                    }
                }
            }
            lines.Add("---");
            lines.Add(page.Markdown);
            File.WriteAllText(fullPath, string.Join(Environment.NewLine, lines));
        }

        var researchLogRoot = Path.Combine(wikiRoot, "research-logs");
        foreach (var log in scenario.ResearchChangeLogs)
        {
            Directory.CreateDirectory(researchLogRoot);
            File.WriteAllText(
                Path.Combine(researchLogRoot, log.TopicId + ".md"),
                log.Markdown.ReplaceLineEndings(Environment.NewLine));
        }
    }

    private static string YamlScalar(string value) => JsonSerializer.Serialize(value);

    private static Process LaunchApp(
        string appExe,
        string? startUri,
        string vaultPath,
        string uiStatePath,
        string instanceKey,
        string captureRequestPath,
        string captureOutputPath,
        string wayfinderFixturePath)
    {
        var psi = new ProcessStartInfo(appExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appExe)!,
        };

        if (!string.IsNullOrWhiteSpace(startUri))
            psi.ArgumentList.Add(startUri);

        psi.Environment[VerificationLaunchOptions.VaultPathVariable] = vaultPath;
        psi.Environment[VerificationLaunchOptions.UiStatePathVariable] = uiStatePath;
        psi.Environment[VerificationLaunchOptions.InstanceKeyVariable] = instanceKey;
        psi.Environment[VerificationLaunchOptions.SkipProtocolRegistrationVariable] = "1";
        psi.Environment[VerificationLaunchOptions.SkipUpdateCheckVariable] = "1";
        psi.Environment[VerificationLaunchOptions.CaptureRequestPathVariable] = captureRequestPath;
        psi.Environment[VerificationLaunchOptions.CaptureOutputPathVariable] = captureOutputPath;
        psi.Environment["GLASSWORK_VISUAL_WAYFINDER_FIXTURE"] = wayfinderFixturePath;

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch Glasswork.");
    }

    private static IntPtr WaitForWindow(Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Glasswork exited before showing a window. Exit code: {process.ExitCode}");

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Glasswork did not show a window within {timeout.TotalSeconds:0}s.");
    }

    private static void PerformAction(
        IntPtr hwnd,
        string vaultRoot,
        string todoPath,
        VisualVerificationAction action)
    {
        switch (action.Type.Trim().ToLowerInvariant())
        {
            case "wait-for":
                _ = WaitForElement(hwnd, action);
                return;
            case "assert-not-present":
                AssertNotPresent(hwnd, action);
                return;
            case "assert-hidden":
                AssertHidden(hwnd, action);
                return;
            case "assert-disabled":
                AssertDisabled(WaitForElement(hwnd, action));
                return;
            case "assert-enabled":
                AssertEnabled(WaitForElement(hwnd, action));
                return;
            case "assert-below":
                AssertBelow(
                    WaitForElement(hwnd, action),
                    WaitForElement(
                        hwnd,
                        new VisualVerificationAction
                        {
                            Type = "wait-for",
                            AutomationId = action.Value,
                            TimeoutMilliseconds = action.TimeoutMilliseconds,
                        }));
                return;
            case "assert-invoke-blocked":
                AssertInvokeBlocked(WaitForElement(hwnd, action));
                return;
            case "invoke":
                ForegroundWindowBestEffort(hwnd);
                InvokeElement(WaitForElement(hwnd, action));
                return;
            case "set-value":
                SetElementValue(WaitForElement(hwnd, action), action.Value ?? string.Empty);
                return;
            case "select":
                SelectElement(WaitForElement(hwnd, action));
                return;
            case "focus":
                FocusElement(WaitForElement(hwnd, action));
                return;
            case "assert-focused":
                AssertFocused(hwnd, WaitForElement(hwnd, action), action);
                return;
            case "assert-name":
                AssertName(WaitForElement(hwnd, action), action.Value!);
                return;
            case "assert-clipboard-text":
                AssertClipboardText(action.Value!);
                return;
            case "assert-focus-within":
                ForegroundWindowBestEffort(hwnd);
                AssertFocusWithin(WaitForElement(hwnd, action));
                return;
            case "press-key":
                PressKey(hwnd, action.Value!);
                return;
            case "assert-single-selection":
                AssertSingleSelection(WaitForElement(hwnd, action));
                return;
            case "assert-selected":
                AssertSelected(hwnd, action);
                return;
            case "scroll-percent":
                ScrollToPercent(WaitForElement(hwnd, action), action);
                return;
            case "assert-vertical-scroll-at-least":
                AssertVerticalScrollAtLeast(WaitForElement(hwnd, action), action);
                return;
            case "assert-vertical-scroll-at-most":
                AssertVerticalScrollAtMost(WaitForElement(hwnd, action), action);
                return;
            case "expand":
                ExpandElement(WaitForElement(hwnd, action));
                return;
            case "delay":
                System.Threading.Thread.Sleep(Math.Max(0, action.TimeoutMilliseconds));
                return;
            case "replace-task-text":
                ReplaceTaskText(todoPath, action);
                return;
            case "replace-wiki-page-text":
                ReplaceWikiPageText(vaultRoot, action);
                return;
            case "delete-wiki-page":
                DeleteWikiPage(vaultRoot, action);
                return;
            default:
                throw new FormatException($"Unsupported visual verification action type '{action.Type}'.");
        }
    }

    private static void AssertClipboardText(string expected)
    {
        string? actual = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                actual = System.Windows.Forms.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException("Could not read clipboard text.", failure);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Expected clipboard text did not match the actual clipboard text. " +
                $"Expected length {expected.Length}, actual length {actual?.Length ?? 0}.");
        }
    }

    private static void ReplaceTaskText(string todoPath, VisualVerificationAction action)
    {
        var path = Path.Combine(todoPath, action.TaskId + ".md");
        var original = File.ReadAllText(path);
        var updated = original.Replace(action.OldValue!, action.Value!, StringComparison.Ordinal);
        if (updated == original)
            throw new InvalidOperationException(
                $"replace-task-text did not find '{action.OldValue}' in task '{action.TaskId}'.");
        File.WriteAllText(path, updated);
    }

    private static void ReplaceWikiPageText(
        string vaultRoot,
        VisualVerificationAction action)
    {
        var wikiRoot = Path.Combine(vaultRoot, "wiki");
        var path = Path.GetFullPath(Path.Combine(
            wikiRoot,
            action.WikiPagePath!.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
                wikiRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Wiki Page path '{action.WikiPagePath}' escapes the scenario Vault.");
        }

        var original = File.ReadAllText(path);
        var updated = original.Replace(
            action.OldValue!,
            action.Value!,
            StringComparison.Ordinal);
        if (updated == original)
        {
            throw new InvalidOperationException(
                $"replace-wiki-page-text did not find '{action.OldValue}' in '{action.WikiPagePath}'.");
        }
        File.WriteAllText(path, updated);
    }

    private static void DeleteWikiPage(
        string vaultRoot,
        VisualVerificationAction action)
    {
        var wikiRoot = Path.Combine(vaultRoot, "wiki");
        var path = Path.GetFullPath(Path.Combine(
            wikiRoot,
            action.WikiPagePath!.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
                wikiRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Wiki Page path '{action.WikiPagePath}' escapes the scenario Vault.");
        }
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Wiki Page '{action.WikiPagePath}' does not exist.");
        }
        File.Delete(path);
    }

    private static InspectionPaths EmitInspection(
        IntPtr hwnd,
        VisualVerificationScenario scenario,
        string outDir,
        string captureRequestPath,
        string captureOutputPath)
    {
        // Capture the paired screenshot and walk the tree back-to-back so the
        // catalog and the PNG describe the same UI state.
        const string screenshotFile = "inspection.png";
        CaptureThroughApp(
            captureRequestPath,
            captureOutputPath,
            Path.Combine(outDir, screenshotFile),
            TimeSpan.FromSeconds(10));

        var (rawElements, warnings) = WalkUiaTree(hwnd);

        var snapshot = UiInspectionBuilder.Build(new UiInspectionInput
        {
            ScreenName = scenario.Name,
            StartUri = scenario.StartUri,
            WindowTitle = TryGet(() => AutomationElement.FromHandle(hwnd).Current.Name),
            ScreenshotFile = screenshotFile,
            WindowBounds = GetWindowBounds(hwnd),
            DpiScale = GetDpiScale(hwnd),
            Warnings = warnings,
            RawElements = rawElements,
        });

        var inspectionPath = Path.Combine(outDir, "inspection.json");
        File.WriteAllText(inspectionPath, JsonSerializer.Serialize(snapshot, InspectionJsonOptions));

        var suggestedPath = Path.Combine(outDir, "scenario.suggested.json");
        File.WriteAllText(suggestedPath, ScenarioScaffolder.ToScenarioJson(ScenarioScaffolder.FromInspection(snapshot)));

        return new InspectionPaths(inspectionPath, suggestedPath);
    }

    private static (List<RawInspectedElement> Elements, List<string> Warnings) WalkUiaTree(IntPtr hwnd)
    {
        const int maxDepth = 40;
        const int maxNodes = 4000;
        var walker = TreeWalker.ControlViewWalker;
        var elements = new List<RawInspectedElement>();
        var skipped = 0;
        var hitMaxDepth = false;
        var hitMaxNodes = false;

        void Visit(AutomationElement element, int depth)
        {
            if (elements.Count >= maxNodes)
            {
                hitMaxNodes = true;
                return;
            }
            if (depth > maxDepth)
            {
                hitMaxDepth = true;
                return;
            }

            try { elements.Add(ToRawElement(element, depth)); }
            catch { skipped++; }

            AutomationElement? child;
            try { child = walker.GetFirstChild(element); }
            catch { skipped++; return; }

            while (child is not null && elements.Count < maxNodes)
            {
                Visit(child, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch { skipped++; break; }
            }
        }

        try { Visit(AutomationElement.FromHandle(hwnd), 0); }
        catch { skipped++; }

        var warnings = new List<string>();
        if (skipped > 0)
            warnings.Add($"{skipped} UI element(s) were skipped due to UI Automation errors.");
        if (hitMaxNodes)
            warnings.Add($"UI Automation traversal stopped at maxNodes={maxNodes}; some elements were omitted.");
        if (hitMaxDepth)
            warnings.Add($"UI Automation traversal reached maxDepth={maxDepth}; deeper descendants were omitted.");

        return (elements, warnings);
    }

    private static RawInspectedElement ToRawElement(AutomationElement element, int depth)
    {
        var patterns = new List<string>();
        try
        {
            foreach (var pattern in element.GetSupportedPatterns())
                patterns.Add(pattern.ProgrammaticName);
        }
        catch { /* enumeration can fail on transient elements; keep what we have. */ }

        return new RawInspectedElement
        {
            AutomationId = TryGet(() => element.Current.AutomationId),
            Name = TryGet(() => element.Current.Name),
            ControlType = TryGet(() => element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty)),
            Depth = depth,
            IsOffscreen = TryGet(() => (bool?)element.Current.IsOffscreen) ?? false,
            IsEnabled = TryGet(() => (bool?)element.Current.IsEnabled) ?? true,
            PatternNames = patterns,
            ScreenBounds = TryGet<ElementBounds?>(() =>
            {
                var rect = element.Current.BoundingRectangle;
                return rect.IsEmpty ? null : new ElementBounds(rect.X, rect.Y, rect.Width, rect.Height);
            }),
        };
    }

    private static ElementBounds GetWindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmWindowAttributeExtendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) != 0)
        {
            if (!GetWindowRect(hwnd, out rect))
                throw new InvalidOperationException("GetWindowRect failed.");
        }

        return new ElementBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private static T? TryGet<T>(Func<T?> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static readonly JsonSerializerOptions InspectionJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AutomationElement WaitForElement(IntPtr hwnd, VisualVerificationAction action)
    {
        var timeout = TimeSpan.FromMilliseconds(action.TimeoutMilliseconds <= 0 ? 5000 : action.TimeoutMilliseconds);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var root = AutomationElement.FromHandle(hwnd);
            var element = FindElement(root, action);
            if (element is not null)
                return element;

            Thread.Sleep(200);
        }

        var selector = action.AutomationId is not null
            ? $"AutomationId='{action.AutomationId}'"
            : $"Name='{action.Name}'";
        throw new TimeoutException($"Timed out waiting for UI element with {selector}.");
    }

    private static AutomationElement? FindElement(AutomationElement root, VisualVerificationAction action)
    {
        var hasAutomationId = !string.IsNullOrWhiteSpace(action.AutomationId);
        var hasName = !string.IsNullOrWhiteSpace(action.Name);
        if (!hasAutomationId && !hasName)
            throw new FormatException("UI action requires automationId or name.");

        if (hasAutomationId)
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, action.AutomationId);
            var match = root.FindFirst(TreeScope.Descendants, condition);
            if (match is not null) return match;
        }

        if (hasName)
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, action.Name);
            var match = root.FindFirst(TreeScope.Descendants, condition);
            if (match is not null) return match;
        }

        // FindFirst(Descendants) does a synchronous cross-process descendant walk
        // that can stall on (and fail to traverse past) a live out-of-process
        // WebView2 subtree (#324). Fall back to a manual sibling-by-sibling
        // ControlView traversal, which reliably steps over such nodes.
        return ManualFind(root, action);
    }

    private static void AssertNotPresent(
        IntPtr hwnd,
        VisualVerificationAction action)
    {
        var timeout = TimeSpan.FromMilliseconds(
            action.TimeoutMilliseconds <= 0 ? 5000 : action.TimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (FindElement(root, action) is null)
                return;
            Thread.Sleep(100);
        }

        var selector = action.AutomationId is not null
            ? $"AutomationId='{action.AutomationId}'"
            : $"Name='{action.Name}'";
        throw new InvalidOperationException(
            $"UI element with {selector} remained present.");
    }

    private static void AssertHidden(
        IntPtr hwnd,
        VisualVerificationAction action)
    {
        var timeout = TimeSpan.FromMilliseconds(
            action.TimeoutMilliseconds <= 0 ? 5000 : action.TimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var element = FindElement(AutomationElement.FromHandle(hwnd), action);
            if (element is null)
                return;
            if (element.Current.IsOffscreen)
                return;
            Thread.Sleep(100);
        }

        throw new InvalidOperationException("The target UI element remained visible.");
    }

    private static void AssertDisabled(AutomationElement element)
    {
        if (element.Current.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' remained enabled.");
        }
    }

    private static void AssertEnabled(AutomationElement element)
    {
        if (!element.Current.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' remained disabled.");
        }
    }

    private static void AssertBelow(
        AutomationElement element,
        AutomationElement reference)
    {
        var elementBounds = element.Current.BoundingRectangle;
        var referenceBounds = reference.Current.BoundingRectangle;
        if (elementBounds.Top < referenceBounds.Bottom)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' starts at {elementBounds.Top:0.##}, "
                + $"above the bottom of '{reference.Current.Name}' at "
                + $"{referenceBounds.Bottom:0.##}.");
        }
    }

    private static void AssertInvokeBlocked(AutomationElement element)
    {
        if (element.Current.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' remained enabled.");
        }

        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
            || pattern is not InvokePattern invoke)
        {
            return;
        }

        try
        {
            invoke.Invoke();
        }
        catch (ElementNotEnabledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Element '{element.Current.Name}' accepted Invoke while disabled.");
    }

    private static AutomationElement? ManualFind(AutomationElement root, VisualVerificationAction action)
    {
        const int maxDepth = 40;
        var walker = TreeWalker.ControlViewWalker;

        bool Matches(AutomationElement element)
        {
            if (!string.IsNullOrWhiteSpace(action.AutomationId)
                && string.Equals(TryGet(() => element.Current.AutomationId), action.AutomationId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(action.Name)
                && string.Equals(TryGet(() => element.Current.Name), action.Name, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        AutomationElement? Search(AutomationElement element, int depth)
        {
            if (depth > maxDepth)
            {
                return null;
            }

            if (Matches(element))
            {
                return element;
            }

            AutomationElement? child;
            try { child = walker.GetFirstChild(element); }
            catch { return null; }

            while (child is not null)
            {
                var found = Search(child, depth + 1);
                if (found is not null)
                {
                    return found;
                }

                try { child = walker.GetNextSibling(child); }
                catch { break; }
            }

            return null;
        }

        try { return Search(root, 0); }
        catch { return null; }
    }

    private static void InvokeElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) &&
            pattern is InvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }

        throw new InvalidOperationException($"Element '{element.Current.Name}' does not support InvokePattern.");
    }

    private static void SetElementValue(AutomationElement element, string value)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
            pattern is ValuePattern valuePattern)
        {
            valuePattern.SetValue(value);
            return;
        }

        throw new InvalidOperationException($"Element '{element.Current.Name}' does not support ValuePattern.");
    }

    private static void SelectElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) &&
            selectionPattern is SelectionItemPattern selectionItem)
        {
            selectionItem.Select();
            return;
        }

        InvokeElement(element);
    }

    private static void FocusElement(AutomationElement element)
    {
        element.SetFocus();
        Thread.Sleep(100);
        if (!element.Current.HasKeyboardFocus)
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' did not receive keyboard focus.");
    }

    private static void AssertFocused(
        IntPtr hwnd,
        AutomationElement element,
        VisualVerificationAction action)
    {
        ForegroundWindowBestEffort(hwnd);
        var timeout = TimeSpan.FromMilliseconds(
            action.TimeoutMilliseconds <= 0 ? 5000 : action.TimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (element.Current.HasKeyboardFocus)
                return;
            Thread.Sleep(100);
        }

        var focused = AutomationElement.FocusedElement;
        var focusedDescription = focused is null
            ? "No element"
            : $"'{focused.Current.Name}' ({focused.Current.AutomationId})";
        throw new InvalidOperationException(
            $"Element '{element.Current.Name}' does not have keyboard focus. " +
            $"{focusedDescription} is focused.");
    }

    private static void AssertName(AutomationElement element, string expectedName)
    {
        var actualName = element.Current.Name;
        if (!string.Equals(actualName, expectedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.AutomationId}' has name '{actualName}', " +
                $"expected '{expectedName}'.");
        }
    }

    private static void AssertFocusWithin(AutomationElement ancestor)
    {
        Thread.Sleep(100);
        var focused = AutomationElement.FocusedElement
            ?? throw new InvalidOperationException("No element has keyboard focus.");
        var walker = TreeWalker.ControlViewWalker;
        for (var current = focused; current is not null;)
        {
            if (current.Equals(ancestor))
                return;
            try { current = walker.GetParent(current); }
            catch { break; }
        }

        throw new InvalidOperationException(
            $"Keyboard focus escaped '{ancestor.Current.Name}'.");
    }

    private static void PressKey(IntPtr hwnd, string key)
    {
        var virtualKey = key switch
        {
            "Escape" => (byte)0x1B,
            "Tab" => (byte)0x09,
            "Space" => (byte)0x20,
            _ => throw new FormatException($"Unsupported key '{key}'."),
        };
        ForegroundWindowBestEffort(hwnd);
        Thread.Sleep(100);
        var scanCode = (byte)MapVirtualKey(virtualKey, MapVirtualKeyToScanCode);
        var downState = new IntPtr(1 | (scanCode << 16));
        var upState = new IntPtr(
            1 | (scanCode << 16) | (1 << 30) | unchecked((int)0x80000000));
        var inputWindow = GetFocusedInputWindow(hwnd);
        SendMessage(inputWindow, WindowMessageKeyDown, new IntPtr(virtualKey), downState);
        SendMessage(inputWindow, WindowMessageKeyUp, new IntPtr(virtualKey), upState);
        Thread.Sleep(150);
    }

    private static IntPtr GetFocusedInputWindow(IntPtr hwnd)
    {
        var threadId = GetWindowThreadProcessId(hwnd, out _);
        var info = new GuiThreadInfo
        {
            Size = Marshal.SizeOf<GuiThreadInfo>(),
        };
        return GetGUIThreadInfo(threadId, ref info) && info.Focus != IntPtr.Zero
            ? info.Focus
            : hwnd;
    }

    private static void AssertSingleSelection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)
            || pattern is not SelectionPattern selection)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' does not expose accessible selection.");
        }

        if (selection.Current.CanSelectMultiple)
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' allows multiple accessible selections.");
        if (selection.Current.GetSelection().Length != 1)
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' must expose exactly one selected item.");
    }

    private static void AssertSelected(
        IntPtr hwnd,
        VisualVerificationAction action)
    {
        var timeout = TimeSpan.FromMilliseconds(
            action.TimeoutMilliseconds <= 0 ? 5000 : action.TimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var root = AutomationElement.FromHandle(hwnd);
            var matches = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    action.Name));
            foreach (AutomationElement match in matches)
            {
                if (match.TryGetCurrentPattern(
                        SelectionItemPattern.Pattern,
                        out var pattern)
                    && pattern is SelectionItemPattern selection
                    && selection.Current.IsSelected)
                {
                    return;
                }
            }

            Thread.Sleep(200);
        }

        throw new InvalidOperationException(
            $"No selected accessible item named '{action.Name}' was found.");
    }

    private static void ScrollToPercent(
        AutomationElement element,
        VisualVerificationAction action)
    {
        var percent = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        var scroll = FindScrollPattern(element);
        if (scroll is null
            || !scroll.Current.VerticallyScrollable)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' does not expose vertical scrolling.");
        }

        scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
        Thread.Sleep(200);
    }

    private static void AssertVerticalScrollAtLeast(
        AutomationElement element,
        VisualVerificationAction action)
    {
        var minimum = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        var scroll = FindScrollPattern(element);
        if (scroll is null)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' does not expose scrolling.");
        }

        var actual = scroll.Current.VerticalScrollPercent;
        if (actual < minimum)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' vertical scroll was {actual:0.##}%, expected at least {minimum:0.##}%.");
        }
    }

    private static void AssertVerticalScrollAtMost(
        AutomationElement element,
        VisualVerificationAction action)
    {
        var expected = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
            || pattern is not ScrollPattern scroll
            || scroll.Current.VerticallyScrollable is false)
        {
            return;
        }

        var actual = scroll.Current.VerticalScrollPercent;
        if (actual > expected)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' vertical scroll was {actual:0.0}%, " +
                $"expected at most {expected:0.0}%.");
        }
    }

    private static ScrollPattern? FindScrollPattern(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var direct)
            && direct is ScrollPattern directScroll)
        {
            return directScroll;
        }

        var descendants = element.FindAll(
            TreeScope.Descendants,
            Condition.TrueCondition);
        foreach (AutomationElement descendant in descendants)
        {
            if (descendant.TryGetCurrentPattern(
                    ScrollPattern.Pattern,
                    out var nested)
                && nested is ScrollPattern nestedScroll)
            {
                return nestedScroll;
            }
        }

        return null;
    }

    private static void ExpandElement(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) &&
            pattern is ExpandCollapsePattern expand)
        {
            if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                expand.Expand();
            return;
        }

        throw new InvalidOperationException($"Element '{element.Current.Name}' does not support ExpandCollapsePattern.");
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with {process.ExitCode}.\n{stdout}\n{stderr}");
    }

    private static void CaptureThroughApp(
        string requestPath,
        string outputPath,
        string destinationPath,
        TimeSpan timeout)
    {
        var errorPath = outputPath + ".error";
        File.Delete(requestPath);
        File.Delete(outputPath);
        File.Delete(errorPath);
        File.WriteAllText(requestPath, Guid.NewGuid().ToString("N"));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(errorPath))
            {
                throw new InvalidOperationException(
                    $"App-render capture failed: {File.ReadAllText(errorPath)}");
            }
            if (File.Exists(outputPath)
                && new FileInfo(outputPath).Length > 0)
            {
                File.Copy(outputPath, destinationPath, overwrite: true);
                return;
            }
            Thread.Sleep(100);
        }

        throw new TimeoutException("Timed out waiting for the app-render capture.");
    }

    private static void CaptureWindow(IntPtr hwnd, string path)
    {
        if (DwmGetWindowAttribute(hwnd, DwmWindowAttributeExtendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) != 0)
        {
            if (!GetWindowRect(hwnd, out rect))
                throw new InvalidOperationException("GetWindowRect failed.");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Window has zero size.");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        // PrintWindow (even with PW_RENDERFULLCONTENT) cannot rasterize this app's
        // WinUI 3 client content: it is hosted in a DesktopChildSiteBridge
        // DirectComposition surface that PrintWindow leaves blank, yielding only the
        // non-client caption chrome. Prefer a screen-region BitBlt of the foregrounded
        // window, which captures the already-composited desktop (real WinUI pixels)
        // when the session is interactive and the window is unobscured. Fall back to
        // PrintWindow if the screen grab is unavailable or uniform (e.g. locked or
        // non-interactive session, or the window is occluded).
        if (!TryCaptureFromScreen(hwnd, rect, width, height, bitmap))
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PrintWindowRenderFullContent))
                    throw new InvalidOperationException("PrintWindow returned false.");
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool TryCaptureFromScreen(IntPtr hwnd, Rect rect, int width, int height, Bitmap bitmap)
    {
        try
        {
            ForegroundWindowBestEffort(hwnd);
            // Allow the compositor to settle and any foreground/restore animation to finish.
            Thread.Sleep(600);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return !IsUniformBitmap(bitmap);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUniformBitmap(Bitmap bitmap)
    {
        var colors = new HashSet<int>();
        var stepX = Math.Max(1, bitmap.Width / 80);
        var stepY = Math.Max(1, bitmap.Height / 80);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                colors.Add(bitmap.GetPixel(x, y).ToArgb());
                if (colors.Count > 1)
                    return false;
            }
        }

        return true;
    }

    private static void ResizeWindow(IntPtr hwnd, int? width, int? height)
    {
        // Long pages (e.g. a task with an Artifacts section near the bottom) lay
        // their lower content out below the page ScrollViewer's viewport, where it
        // is scroll-clipped and never gets a UI Automation peer. Growing the window
        // so the whole page fits without scrolling brings that content into the
        // live visual tree so both the UIA walk and screen capture can see it.
        // Explicit dimensions are XAML DIPs so responsive breakpoints remain stable
        // across display scaling; omitted dimensions retain the legacy tall default.
        try
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, ShowWindowRestore);

            var physicalWidth = 1500;
            var physicalHeight = 2400;
            if (width is { } widthDips && height is { } heightDips)
            {
                var dpiScale = GetDpiForWindow(hwnd) / 96d;
                physicalWidth = checked((int)Math.Round(widthDips * dpiScale));
                physicalHeight = checked((int)Math.Round(heightDips * dpiScale));
            }
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                physicalWidth,
                physicalHeight,
                SwpShowWindow);
        }
        catch
        {
            // Best-effort: verification still works at the default size for short pages.
        }
    }

    private static void ForegroundWindowBestEffort(IntPtr hwnd)
    {
        try
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, ShowWindowRestore);

            // Topmost flip is the most reliable way to surface a window without
            // requiring foreground-activation rights, then drop back to non-topmost.
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Best-effort only; screen capture falls back to PrintWindow if this fails.
        }
    }

    private static ImageStats AnalyzeImage(string path)
    {
        using var bitmap = new Bitmap(path);
        var colors = new HashSet<int>();
        var stepX = Math.Max(1, bitmap.Width / 80);
        var stepY = Math.Max(1, bitmap.Height / 80);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
                colors.Add(bitmap.GetPixel(x, y).ToArgb());
        }

        return new ImageStats(bitmap.Width, bitmap.Height, colors.Count);
    }

    private static string SanitizeFileName(string value) =>
        InvalidFileNameCharsRegex().Replace(value.Trim(), "-");

    [GeneratedRegex(@"[\\/:*?""<>|]+")]
    private static partial Regex InvalidFileNameCharsRegex();

    private static void EnsureDpiAware()
    {
        // Best-effort: fails harmlessly if awareness was already set by a host/manifest.
        try { SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); }
        catch { /* older OS without the API — fall back to process default. */ }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hwnd,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(
        uint threadId,
        ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4.
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    private const uint PrintWindowRenderFullContent = 2;
    private const uint MapVirtualKeyToScanCode = 0;
    private const uint WindowMessageKeyDown = 0x0100;
    private const uint WindowMessageKeyUp = 0x0101;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public Rect CaretRect;
    }
    private const int DwmWindowAttributeExtendedFrameBounds = 9;

    private const int ShowWindowRestore = 9;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record RunnerOptions(
    string ScenarioPath,
    string RepoRoot,
    string OutDir,
    bool NoBuild,
    bool KeepWorkingDirectory,
    bool Inspect)
{
    public static RunnerOptions Parse(string[] args)
    {
        string? scenario = null;
        string? repoRoot = null;
        string? outDir = null;
        var noBuild = false;
        var keepWorkingDir = false;
        var inspect = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    scenario = RequireValue(args, ref i, "--scenario");
                    break;
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i, "--repo-root");
                    break;
                case "--out-dir":
                    outDir = RequireValue(args, ref i, "--out-dir");
                    break;
                case "--no-build":
                    noBuild = true;
                    break;
                case "--keep-working-directory":
                    keepWorkingDir = true;
                    break;
                case "--inspect":
                    inspect = true;
                    break;
                default:
                    throw new FormatException($"Unknown argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(scenario))
            throw new FormatException("Usage: Glasswork.VisualVerification --scenario <path> [--repo-root <path>] [--out-dir <path>] [--no-build] [--inspect]");

        repoRoot ??= FindRepoRoot(Environment.CurrentDirectory);
        outDir ??= Path.Combine(
            Path.GetTempPath(),
            "Glasswork-visual-results-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));

        return new RunnerOptions(
            Path.GetFullPath(scenario),
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(outDir),
            noBuild,
            keepWorkingDir,
            inspect);
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new FormatException($"{name} requires a value.");
        index++;
        return args[index];
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glasswork.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Glasswork.slnx. Pass --repo-root.");
    }
}

internal sealed record VerificationResult(
    string Scenario,
    string OutputDirectory,
    string VaultPath,
    string UiStatePath,
    string InstanceKey,
    IReadOnlyList<CaptureResult> Captures,
    string? InspectionPath = null,
    string? SuggestedScenarioPath = null);

internal sealed record InspectionPaths(string InspectionPath, string SuggestedScenarioPath);

internal sealed record CaptureResult(
    string Name,
    string Path,
    int Width,
    int Height,
    int UniqueSampledColors);

internal sealed record ImageStats(int Width, int Height, int UniqueSampledColors);
