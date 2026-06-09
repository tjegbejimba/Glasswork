using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
        Directory.CreateDirectory(todoPath);

        MaterializeVault(scenario, vaultRoot, todoPath);

        var instanceKey = "visual-" + Guid.NewGuid().ToString("N");
        using var process = LaunchApp(appExe, scenario.StartUri, todoPath, uiStatePath, instanceKey);

        try
        {
            var hwnd = WaitForWindow(process, TimeSpan.FromSeconds(scenario.LaunchTimeoutSeconds));
            await Task.Delay(scenario.InitialWaitMilliseconds);

            foreach (var action in scenario.Actions)
            {
                PerformAction(hwnd, action);
            }

            var inspection = options.Inspect
                ? EmitInspection(hwnd, scenario, options.OutDir)
                : null;

            var captures = new List<CaptureResult>();
            foreach (var capture in scenario.Captures)
            {
                if (capture.WaitMilliseconds > 0)
                    await Task.Delay(capture.WaitMilliseconds);

                var path = Path.Combine(options.OutDir, SanitizeFileName(capture.Name) + ".png");
                CaptureWindow(hwnd, path);
                var imageStats = AnalyzeImage(path);
                if (imageStats.UniqueSampledColors <= 1)
                    throw new InvalidOperationException($"Capture '{capture.Name}' appears blank or uniform: {path}");

                captures.Add(new CaptureResult(capture.Name, path, imageStats.Width, imageStats.Height, imageStats.UniqueSampledColors));
            }

            return new VerificationResult(
                scenario.Name,
                options.OutDir,
                todoPath,
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

                var fileName = artifact.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? artifact.Name
                    : artifact.Name + ".md";
                File.WriteAllText(Path.Combine(artifactsDir, SanitizeFileName(fileName)), artifact.Markdown);
            }
        }
    }

    private static Process LaunchApp(string appExe, string? startUri, string vaultPath, string uiStatePath, string instanceKey)
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

    private static void PerformAction(IntPtr hwnd, VisualVerificationAction action)
    {
        switch (action.Type.Trim().ToLowerInvariant())
        {
            case "wait-for":
                _ = WaitForElement(hwnd, action);
                return;
            case "invoke":
                InvokeElement(WaitForElement(hwnd, action));
                return;
            case "set-value":
                SetElementValue(WaitForElement(hwnd, action), action.Value ?? string.Empty);
                return;
            case "select":
                SelectElement(WaitForElement(hwnd, action));
                return;
            default:
                throw new FormatException($"Unsupported visual verification action type '{action.Type}'.");
        }
    }

    private static InspectionPaths EmitInspection(IntPtr hwnd, VisualVerificationScenario scenario, string outDir)
    {
        // Capture the paired screenshot and walk the tree back-to-back so the
        // catalog and the PNG describe the same UI state.
        const string screenshotFile = "inspection.png";
        CaptureWindow(hwnd, Path.Combine(outDir, screenshotFile));

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
        if (!string.IsNullOrWhiteSpace(action.AutomationId))
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, action.AutomationId);
            var match = root.FindFirst(TreeScope.Descendants, condition);
            if (match is not null) return match;
        }

        if (!string.IsNullOrWhiteSpace(action.Name))
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, action.Name);
            return root.FindFirst(TreeScope.Descendants, condition);
        }

        throw new FormatException("UI action requires automationId or name.");
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
        using (var graphics = Graphics.FromImage(bitmap))
        {
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
    private const int DwmWindowAttributeExtendedFrameBounds = 9;

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
