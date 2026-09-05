using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Glasswork.Core.Services;
using Glasswork.Core.VisualVerification;
using Microsoft.VisualBasic.FileIO;

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

        RunnerOptions? options = null;
        var failureStage = "preflight";
        try
        {
            options = RunnerOptions.Parse(args);
            Directory.CreateDirectory(options.OutDir);
            if (options.MergeEvidence)
            {
                File.Delete(Path.Combine(options.OutDir, "result.json"));
                File.Delete(Path.Combine(options.OutDir, "failure.json"));
                if (options.NoBuild)
                    throw new FormatException("--no-build cannot be used with --merge-evidence.");
            }

            var workDir = Path.Combine(Path.GetTempPath(), "glasswork-visual-work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                VisualVerificationSourceSnapshot? sourceBefore = null;
                VisualVerificationLaunchBundle? launchBefore = null;
                VisualVerificationScenario scenario;
                var buildSourceRoot = options.RepoRoot;
                if (options.MergeEvidence)
                {
                    sourceBefore = VisualVerificationMergeEvidence.CaptureSourceSnapshot(
                        options.RepoRoot,
                        options.ScenarioPath);
                    if (!string.IsNullOrEmpty(sourceBefore.Status))
                    {
                        throw new InvalidOperationException(
                            "Merge evidence requires a clean repository checkout.");
                    }

                    buildSourceRoot = await MaterializeSourceSnapshotAsync(
                        options.RepoRoot,
                        sourceBefore.Commit,
                        workDir);
                    var snapshotScenarioPath = Path.Combine(
                        buildSourceRoot,
                        sourceBefore.ScenarioId.Replace('/', Path.DirectorySeparatorChar));
                    if (VisualVerificationMergeEvidence.HashFile(snapshotScenarioPath)
                        != sourceBefore.ScenarioSha256)
                    {
                        throw new InvalidOperationException(
                            "The committed scenario does not match the reviewed checkout.");
                    }
                    scenario = VisualVerificationScenario.FromFile(snapshotScenarioPath);
                }
                else
                    scenario = VisualVerificationScenario.FromFile(options.ScenarioPath);

                failureStage = "build";
                string appExe;
                string? launchRoot = null;
                if (options.MergeEvidence)
                {
                    var artifactsRoot = Path.Combine(workDir, "artifacts");
                    await RunProcessAsync(
                        "dotnet",
                        [
                            "build",
                            Path.Combine(buildSourceRoot, AppRelativePath),
                            "-c", "Debug",
                            "-p:Platform=x64",
                            "--artifacts-path", artifactsRoot,
                            "--nologo",
                            "-v", "quiet",
                            "-tl:off",
                        ],
                        buildSourceRoot);
                    appExe = Directory
                        .EnumerateFiles(
                            artifactsRoot,
                            "Glasswork.exe",
                            System.IO.SearchOption.AllDirectories)
                        .SingleOrDefault()
                        ?? throw new FileNotFoundException(
                            $"Fresh merge-evidence build did not produce Glasswork.exe under {artifactsRoot}.");
                    launchRoot = Path.GetDirectoryName(appExe)!;
                    launchBefore = VisualVerificationMergeEvidence.CaptureLaunchBundle(launchRoot);
                }
                else
                {
                    if (!options.NoBuild)
                    {
                        await RunProcessAsync(
                            "dotnet",
                            $"build \"{Path.Combine(options.RepoRoot, AppRelativePath)}\" -c Debug -p:Platform=x64 --nologo -v quiet -tl:off",
                            options.RepoRoot);
                    }

                    appExe = Path.Combine(options.RepoRoot, AppExeRelativePath);
                    if (!File.Exists(appExe))
                    {
                        throw new FileNotFoundException(
                            $"Glasswork dev executable not found. Build first or remove --no-build. Expected: {appExe}",
                            appExe);
                    }
                }

                failureStage = "verification";
                var result = await RunScenarioAsync(scenario, options, appExe, workDir);
                if (options.MergeEvidence)
                {
                    failureStage = "postflight";
                    var sourceAfter = VisualVerificationMergeEvidence.CaptureSourceSnapshot(
                        options.RepoRoot,
                        options.ScenarioPath);
                    VisualVerificationMergeEvidence.EnsureSourceUnchanged(sourceBefore!, sourceAfter);
                    var launchAfter = VisualVerificationMergeEvidence.CaptureLaunchBundle(launchRoot!);
                    VisualVerificationMergeEvidence.EnsureLaunchBundleUnchanged(launchBefore!, launchAfter);
                    result = result with
                    {
                        Evidence = CreateEvidence(
                            sourceBefore!,
                            launchBefore!,
                            result.Captures),
                    };
                }

                var resultPath = Path.Combine(options.OutDir, "result.json");
                File.WriteAllText(
                    resultPath,
                    JsonSerializer.Serialize(
                        result,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        }));
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
            if (options?.MergeEvidence == true)
            {
                Directory.CreateDirectory(options.OutDir);
                File.Delete(Path.Combine(options.OutDir, "result.json"));
                File.WriteAllText(
                    Path.Combine(options.OutDir, "failure.json"),
                    JsonSerializer.Serialize(
                        new VerificationFailure(false, failureStage, ex.Message),
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static VerificationEvidence CreateEvidence(
        VisualVerificationSourceSnapshot source,
        VisualVerificationLaunchBundle launchBundle,
        IReadOnlyList<CaptureResult> captures) =>
        new(
            1,
            new EvidenceSource(
                source.Commit,
                source.Tree,
                source.ScenarioId,
                source.ScenarioSha256),
            new EvidenceBuild(
                "Debug",
                "x64",
                "net10.0-windows10.0.26100.0",
                launchBundle.Sha256,
                launchBundle.Files),
            captures
                .Select(capture => new VisualVerificationEvidenceFile(
                    Path.GetFileName(capture.Path),
                    VisualVerificationMergeEvidence.HashFile(capture.Path)))
                .ToArray());

    private static async Task<string> MaterializeSourceSnapshotAsync(
        string repositoryRoot,
        string commit,
        string workDir)
    {
        var archivePath = Path.Combine(workDir, "source.zip");
        var sourceRoot = Path.Combine(workDir, "source");
        await RunProcessAsync(
            "git",
            ["archive", "--format=zip", $"--output={archivePath}", commit],
            repositoryRoot);
        Directory.CreateDirectory(sourceRoot);
        ZipFile.ExtractToDirectory(archivePath, sourceRoot);
        File.Delete(archivePath);
        return sourceRoot;
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
        var canvasExtensionsRoot = MaterializeCanvasExtensionState(scenario, workDir);
        var canvasRetrySourcePath = scenario.CanvasExtensionState?.RetryBundleAvailable == true
            ? await MaterializeCanvasExtensionRetryBundleAsync(options, workDir)
            : null;
        var initialUiState = new Dictionary<string, object?>
        {
            ["app.theme"] = scenario.Theme,
        };
        if (scenario.PlannerProfile is not null)
            initialUiState[PlannerProfileService.UiStateKey] = scenario.PlannerProfile;
        File.WriteAllText(uiStatePath, JsonSerializer.Serialize(initialUiState));

        var instanceKey = "visual-" + Guid.NewGuid().ToString("N");
        using var process = LaunchApp(
            appExe,
            scenario.StartUri,
            scenario.StartPage,
            vaultRoot,
            uiStatePath,
            instanceKey,
            captureRequestPath,
            captureOutputPath,
            wayfinderFixturePath,
            canvasExtensionsRoot,
            canvasRetrySourcePath);

        try
        {
            var hwnd = WaitForWindow(process, TimeSpan.FromSeconds(scenario.LaunchTimeoutSeconds));
            ResizeWindow(hwnd, scenario.WindowWidth, scenario.WindowHeight);
            await Task.Delay(scenario.InitialWaitMilliseconds);
            var captures = new List<CaptureResult>();

            foreach (var action in scenario.Actions)
            {
                try
                {
                    PerformAction(
                        hwnd,
                        vaultRoot,
                        todoPath,
                        uiStatePath,
                        options.OutDir,
                        captureRequestPath,
                        captureOutputPath,
                        process.Id,
                        action,
                        captures);
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
                process.Id,
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
            for (var copy = 1; copy <= task.Repeat; copy++)
            {
                var materializedTask = task.ToGlassworkTask(today);
                if (task.Repeat > 1)
                {
                    materializedTask.Id = $"{task.Id}-{copy:D3}";
                    materializedTask.Title = $"{task.Title} {copy:D3}";
                }
                vault.Save(materializedTask);

                if (task.Artifacts.Count == 0) continue;
                var artifactsDir = Path.Combine(
                    vaultRoot,
                    "wiki",
                    "todo",
                    materializedTask.Id + ".artifacts");
                Directory.CreateDirectory(artifactsDir);
                foreach (var artifact in task.Artifacts)
                {
                    if (string.IsNullOrWhiteSpace(artifact.Name))
                        throw new FormatException(
                            $"Artifact on task '{materializedTask.Id}' requires a non-empty name.");

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
                        File.WriteAllText(
                            fullPath,
                            string.Concat(Enumerable.Repeat(text, artifact.RepeatContent)));
                    }
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

    /// <summary>
    /// Seeds a deterministic canvas-extension <c>current.json</c> under a
    /// temporary extensions root when the scenario requests one (issue #562),
    /// so a Settings scenario can reproduce healthy/failed/never-installed
    /// states without touching the real Copilot extensions directory.
    /// Returns null when the scenario doesn't opt in, leaving the app to read
    /// the real (default) location as usual.
    /// </summary>
    private static string? MaterializeCanvasExtensionState(VisualVerificationScenario scenario, string workDir)
    {
        var state = scenario.CanvasExtensionState;
        if (state is null) return null;

        var extensionsRoot = Path.Combine(workDir, "canvas-extensions");
        var extensionDirectory = Path.Combine(extensionsRoot, Glasswork.Core.AppUpdate.CanvasExtensionHealthReader.ExtensionName);
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(
            Path.Combine(extensionDirectory, "current.json"),
            JsonSerializer.Serialize(new
            {
                version = state.Version,
                identity = state.Identity,
                sourceRevision = state.SourceRevision,
                sha256 = state.Sha256,
                hostExecutablePath = state.HostExecutablePath,
                lastAttempt = new
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    version = state.LastAttemptVersion,
                    status = state.LastAttemptStatus,
                    message = state.LastAttemptMessage,
                },
            }));
        return extensionsRoot;
    }

    /// <summary>
    /// Builds a real self-contained canvas host bundle (mirroring the Pester
    /// integration test's approach) so a "retry-success" scenario can click
    /// Settings' Retry button and observe a genuine transition to a healthy
    /// state, not just a seeded static fixture.
    /// </summary>
    private static async Task<string> MaterializeCanvasExtensionRetryBundleAsync(RunnerOptions options, string workDir)
    {
        const string version = "9.9.9";
        const string sourceRevision = "ffffffffffffffffffffffffffffffffffffffff";
        var bundle = Path.Combine(workDir, "canvas-retry-bundle");
        var versionDirectory = Path.Combine(bundle, "host", version);
        Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "extension.mjs"),
            "// visual-verification fixture adapter (not exercised)");

        var canvasHostProject = Path.Combine(options.RepoRoot, "tools", "Glasswork.CanvasHost", "Glasswork.CanvasHost.csproj");
        await RunProcessAsync(
            "dotnet",
            $"publish \"{canvasHostProject}\" --configuration Release --self-contained --runtime win-x64 " +
            $"--output \"{versionDirectory}\" -p:Version={version} -p:RepositoryCommit={sourceRevision} --nologo --verbosity quiet",
            options.RepoRoot);

        await File.WriteAllTextAsync(Path.Combine(bundle, "host", "active.txt"), version);
        var hostExe = Path.Combine(versionDirectory, "Glasswork.CanvasHost.exe");
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hostExe)));
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "manifest.json"),
            JsonSerializer.Serialize(new { version, sourceRevision, sha256 }));

        return bundle;
    }

    private static Process LaunchApp(
        string appExe,
        string? startUri,
        string? startPage,
        string vaultPath,
        string uiStatePath,
        string instanceKey,
        string captureRequestPath,
        string captureOutputPath,
        string wayfinderFixturePath,
        string? canvasExtensionsRoot,
        string? canvasRetrySourcePath)
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
        if (startPage is not null)
            psi.Environment[VerificationLaunchOptions.StartPageVariable] = startPage;
        psi.Environment["GLASSWORK_VISUAL_WAYFINDER_FIXTURE"] = wayfinderFixturePath;
        if (canvasExtensionsRoot is not null)
        {
            psi.Environment[Glasswork.Core.AppUpdate.CanvasExtensionHealthReader.ExtensionsRootOverrideVariable] = canvasExtensionsRoot;
        }
        if (canvasRetrySourcePath is not null)
        {
            psi.Environment[Glasswork.Core.AppUpdate.CanvasExtensionHealthReader.RetrySourcePathOverrideVariable] = canvasRetrySourcePath;
        }

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
        string uiStatePath,
        string outDir,
        string captureRequestPath,
        string captureOutputPath,
        int processId,
        VisualVerificationAction action,
        ICollection<CaptureResult> captures)
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
            case "assert-live-setting":
                _ = WaitForElement(hwnd, action);
                AssertLiveSetting(hwnd, action.AutomationId!, action.Value!);
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
            case "assert-ui-state-missing":
                AssertUiStateMissing(uiStatePath, action);
                return;
            case "assert-ui-state-json":
                AssertUiStateJson(uiStatePath, action);
                return;
            case "capture":
            {
                var path = Path.Combine(outDir, SanitizeFileName(action.Name!) + ".png");
                CaptureThroughApp(
                    captureRequestPath,
                    captureOutputPath,
                    path,
                    TimeSpan.FromSeconds(10));
                var imageStats = AnalyzeImage(path);
                if (imageStats.UniqueSampledColors <= 1)
                    throw new InvalidOperationException($"Capture '{action.Name}' appears blank or uniform: {path}");
                captures.Add(
                    new CaptureResult(
                        action.Name!,
                        path,
                        imageStats.Width,
                        imageStats.Height,
                        imageStats.UniqueSampledColors));
                return;
            }
            case "assert-single-selection":
                AssertSingleSelection(WaitForElement(hwnd, action));
                return;
            case "assert-selected":
                AssertSelected(hwnd, action);
                return;
            case "assert-checked":
                AssertChecked(WaitForElement(hwnd, action));
                return;
            case "scroll-percent":
                ScrollToPercent(WaitForElement(hwnd, action), action);
                return;
            case "assert-scroll-xaml-frame-budget":
                AssertScrollXamlFrameBudget(
                    hwnd,
                    WaitForElement(hwnd, action),
                    action,
                    outDir,
                    processId);
                return;
            case "assert-navigation-latency":
                AssertNavigationLatency(
                    hwnd,
                    action,
                    outDir,
                    processId);
                return;
            case "assert-nav-selection-latency":
                AssertNavSelectionLatency(
                    hwnd,
                    action,
                    outDir,
                    processId);
                return;
            case "assert-vertical-scroll-at-least":
                AssertVerticalScrollAtLeast(WaitForElement(hwnd, action), action);
                return;
            case "assert-vertical-scroll-at-most":
                AssertVerticalScrollAtMost(WaitForElement(hwnd, action), action);
                return;
            case "expand":
                ForegroundWindowBestEffort(hwnd);
                Thread.Sleep(100);
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

    private static void AssertLiveSetting(
        IntPtr hwnd,
        string automationId,
        string expected)
    {
        var expectedValue = expected switch
        {
            "Off" => AutomationLiveSetting.Off,
            "Polite" => AutomationLiveSetting.Polite,
            "Assertive" => AutomationLiveSetting.Assertive,
            _ => throw new FormatException($"Unsupported live setting '{expected}'."),
        };
        var automation = (INativeUiAutomation)(object)new NativeUiAutomation();
        var root = automation.ElementFromHandle(hwnd);
        var condition = automation.CreatePropertyCondition(
            AutomationElementIdentifiers.AutomationIdProperty.Id,
            automationId);
        var element = root.FindFirst(0x4, condition)
            ?? throw new InvalidOperationException(
                $"Native UI Automation could not find '{automationId}'.");
        var actual = element.GetCurrentPropertyValue(
            AutomationElementIdentifiers.LiveSettingProperty.Id);
        if (Convert.ToInt32(actual, CultureInfo.InvariantCulture) != (int)expectedValue)
        {
            throw new InvalidOperationException(
                $"Expected live setting '{expectedValue}', actual '{actual}'.");
        }
    }

    [ComImport]
    [Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class NativeUiAutomation
    {
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeUiAutomation
    {
        IntPtr CompareElements();
        IntPtr CompareRuntimeIds();
        IntPtr GetRootElement();
        INativeUiAutomationElement ElementFromHandle(IntPtr hwnd);
        IntPtr ElementFromPoint();
        IntPtr GetFocusedElement();
        IntPtr GetRootElementBuildCache();
        IntPtr ElementFromHandleBuildCache();
        IntPtr ElementFromPointBuildCache();
        IntPtr GetFocusedElementBuildCache();
        IntPtr CreateTreeWalker();
        IntPtr GetControlViewWalker();
        IntPtr GetContentViewWalker();
        IntPtr GetRawViewWalker();
        IntPtr GetRawViewCondition();
        IntPtr GetControlViewCondition();
        IntPtr GetContentViewCondition();
        IntPtr CreateCacheRequest();
        IntPtr CreateTrueCondition();
        IntPtr CreateFalseCondition();
        INativeUiAutomationCondition CreatePropertyCondition(
            int propertyId,
            [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeUiAutomationElement
    {
        void SetFocus();
        IntPtr GetRuntimeId();
        INativeUiAutomationElement? FindFirst(
            int scope,
            INativeUiAutomationCondition condition);
        IntPtr FindAll();
        IntPtr FindFirstBuildCache();
        IntPtr FindAllBuildCache();
        IntPtr BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }

    [ComImport]
    [Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeUiAutomationCondition
    {
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
        if (key == "Alt+U")
        {
            PressAltU(hwnd);
            return;
        }
        var virtualKey = key switch
        {
            "Escape" => (byte)0x1B,
            "Tab" => (byte)0x09,
            "Space" => (byte)0x20,
            "PageDown" => (byte)0x22,
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

    private static void PressAltU(IntPtr hwnd)
    {
        const byte u = 0x55;
        ForegroundWindowBestEffort(hwnd);
        Thread.Sleep(100);
        var inputWindow = GetFocusedInputWindow(hwnd);
        var scanCode = (byte)MapVirtualKey(u, MapVirtualKeyToScanCode);
        var downState = new IntPtr(1 | (scanCode << 16) | (1 << 29));
        var upState = new IntPtr(
            1 | (scanCode << 16) | (1 << 29) | (1 << 30) | unchecked((int)0x80000000));
        SendMessage(inputWindow, WindowMessageSystemKeyDown, new IntPtr(u), downState);
        SendMessage(inputWindow, WindowMessageSystemKeyUp, new IntPtr(u), upState);
        Thread.Sleep(150);
    }

    private static void AssertUiStateMissing(string path, VisualVerificationAction action)
    {
        var key = ResolveUiStateKey(action.Name!);
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds <= action.TimeoutMilliseconds)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty(key, out _))
                    return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Auto-saving UI State replaces the file atomically; retry the transient window.
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"UI state key '{key}' was present.");
    }

    private static void AssertUiStateJson(string path, VisualVerificationAction action)
    {
        var key = ResolveUiStateKey(action.Name!);
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds <= action.TimeoutMilliseconds)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty(key, out var actual))
                {
                    var actualNode = JsonNode.Parse(actual.GetRawText());
                    var expectedNode = JsonNode.Parse(action.Value!);
                    if (JsonNode.DeepEquals(actualNode, expectedNode))
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Auto-saving UI State replaces the file atomically; retry the transient window.
            }
            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"UI state key '{key}' did not match the expected JSON.");
    }

    private static string ResolveUiStateKey(string key) =>
        key.Replace(
            "{today}",
            DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

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

    private static void AssertChecked(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
            || pattern is not TogglePattern toggle)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' does not support checked state.");
        }
        if (toggle.Current.ToggleState != ToggleState.On)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' is not checked.");
        }
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

    private static void AssertScrollXamlFrameBudget(
        IntPtr hwnd,
        AutomationElement element,
        VisualVerificationAction action,
        string outDir,
        int processId)
    {
        const int scrollSteps = 20;
        var frameBudgetMilliseconds = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        var scroll = FindScrollPattern(element);
        if (scroll is null || !scroll.Current.VerticallyScrollable)
        {
            throw new InvalidOperationException(
                $"Element '{element.Current.Name}' does not expose vertical scrolling.");
        }

        ForegroundWindowBestEffort(hwnd);
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        Thread.Sleep(300);
        var focusTarget = FindFocusableScrollDescendant(element);
        FocusElement(focusTarget);

        var durations = CaptureXamlFrameDurations(
            outDir,
            "scroll",
            processId,
            () =>
            {
            for (var i = 0; i < scrollSteps; i++)
                PressKey(hwnd, "PageDown");
            Thread.Sleep(300);
            });
        if (durations.Count < 10)
        {
            throw new InvalidOperationException(
                $"XAML trace captured only {durations.Count} frames for process {processId}.");
        }

        durations.Sort();
        var median = Percentile(durations, 0.50);
        var p95 = Percentile(durations, 0.95);
        var p99 = Percentile(durations, 0.99);
        var maximum = durations[^1];
        var overBudgetCount = durations.Count(value => value > frameBudgetMilliseconds);
        var report = new
        {
            element = element.Current.Name,
            scrollSteps,
            frameCount = durations.Count,
            medianMilliseconds = median,
            p95Milliseconds = p95,
            p99Milliseconds = p99,
            maximumMilliseconds = maximum,
            frameBudgetMilliseconds,
            overBudgetCount,
        };
        var reportPath = Path.Combine(outDir, "scroll-xaml-frame-budget.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"Scroll XAML frames: {durations.Count}, median {median:0.00} ms, p95 {p95:0.00} ms, p99 {p99:0.00} ms, max {maximum:0.00} ms, over budget {overBudgetCount}.");

        if (p95 > frameBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Scroll XAML-frame p95 was {p95:0.00} ms, exceeding the {frameBudgetMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
    }

    private static void AssertNavigationLatency(
        IntPtr hwnd,
        VisualVerificationAction action,
        string outDir,
        int processId)
    {
        var budgetMilliseconds = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        var source = WaitForElement(
            hwnd,
            new VisualVerificationAction
            {
                Type = "wait-for",
                Name = action.Name,
                TimeoutMilliseconds = action.TimeoutMilliseconds,
            });
        var stopwatch = new Stopwatch();
        double shellElapsedMilliseconds = 0;
        double? completionElapsedMilliseconds = null;
        var durations = CaptureXamlFrameDurations(
            outDir,
            "navigation",
            processId,
            () =>
            {
                stopwatch.Start();
                InvokeElement(source);
                _ = WaitForElement(
                    hwnd,
                    new VisualVerificationAction
                    {
                        Type = "wait-for",
                        AutomationId = action.AutomationId,
                        TimeoutMilliseconds = action.TimeoutMilliseconds,
                    });
                stopwatch.Stop();
                shellElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                if (!string.IsNullOrWhiteSpace(action.CompletionAutomationId)
                    || !string.IsNullOrWhiteSpace(action.CompletionName))
                {
                    stopwatch.Start();
                    _ = WaitForElement(
                        hwnd,
                        new VisualVerificationAction
                        {
                            Type = "wait-for",
                            AutomationId = action.CompletionAutomationId,
                            Name = action.CompletionName,
                            TimeoutMilliseconds = action.TimeoutMilliseconds,
                        });
                    stopwatch.Stop();
                    completionElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                }
                Thread.Sleep(300);
            });
        durations.Sort();
        if (durations.Count == 0)
            throw new InvalidOperationException(
                $"XAML trace captured no navigation frames for process {processId}.");
        var maximum = durations[^1];
        var report = new
        {
            source = action.Name,
            destination = action.AutomationId,
            elapsedMilliseconds = shellElapsedMilliseconds,
            completionElapsedMilliseconds,
            xamlFrameCount = durations.Count,
            maximumXamlFrameMilliseconds = maximum,
            action.MaximumXamlFrameMilliseconds,
            budgetMilliseconds,
        };
        var reportPath = Path.Combine(outDir, "navigation-latency.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"Navigation latency: {shellElapsedMilliseconds:0.00} ms, completion {completionElapsedMilliseconds:0.00} ms, max XAML frame {maximum:0.00} ms.");
        if (shellElapsedMilliseconds > budgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation took {shellElapsedMilliseconds:0.00} ms, exceeding the {budgetMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
        if (completionElapsedMilliseconds > action.CompletionBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation completion took {completionElapsedMilliseconds:0.00} ms, exceeding the {action.CompletionBudgetMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
        if (maximum > action.MaximumXamlFrameMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation maximum XAML frame was {maximum:0.00} ms, exceeding the {action.MaximumXamlFrameMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
    }

    private static void AssertNavSelectionLatency(
        IntPtr hwnd,
        VisualVerificationAction action,
        string outDir,
        int processId)
    {
        var budgetMilliseconds = double.Parse(
            action.Value!,
            CultureInfo.InvariantCulture);
        var source = WaitForElement(
            hwnd,
            new VisualVerificationAction
            {
                Type = "wait-for",
                AutomationId = action.Name,
                TimeoutMilliseconds = action.TimeoutMilliseconds,
            });
        var stopwatch = new Stopwatch();
        double shellElapsedMilliseconds = 0;
        double? completionElapsedMilliseconds = null;
        var durations = CaptureXamlFrameDurations(
            outDir,
            "nav-selection",
            processId,
            () =>
            {
                stopwatch.Start();
                SelectElement(source);
                _ = WaitForElement(
                    hwnd,
                    new VisualVerificationAction
                    {
                        Type = "wait-for",
                        AutomationId = action.AutomationId,
                        TimeoutMilliseconds = action.TimeoutMilliseconds,
                    });
                stopwatch.Stop();
                shellElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                if (!string.IsNullOrWhiteSpace(action.CompletionAutomationId)
                    || !string.IsNullOrWhiteSpace(action.CompletionName))
                {
                    stopwatch.Start();
                    _ = WaitForElement(
                        hwnd,
                        new VisualVerificationAction
                        {
                            Type = "wait-for",
                            AutomationId = action.CompletionAutomationId,
                            Name = action.CompletionName,
                            TimeoutMilliseconds = action.TimeoutMilliseconds,
                        });
                    stopwatch.Stop();
                    completionElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                }
                Thread.Sleep(300);
            });
        durations.Sort();
        if (durations.Count == 0)
            throw new InvalidOperationException(
                $"XAML trace captured no navigation frames for process {processId}.");
        var maximum = durations[^1];
        var report = new
        {
            source = action.Name,
            destination = action.AutomationId,
            elapsedMilliseconds = shellElapsedMilliseconds,
            completionElapsedMilliseconds,
            xamlFrameCount = durations.Count,
            maximumXamlFrameMilliseconds = maximum,
            action.MaximumXamlFrameMilliseconds,
            budgetMilliseconds,
        };
        var reportPath = Path.Combine(outDir, "nav-selection-latency.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"Navigation selection latency: {shellElapsedMilliseconds:0.00} ms, completion {completionElapsedMilliseconds:0.00} ms, max XAML frame {maximum:0.00} ms.");
        if (shellElapsedMilliseconds > budgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation selection took {shellElapsedMilliseconds:0.00} ms, exceeding the {budgetMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
        if (completionElapsedMilliseconds > action.CompletionBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation selection completion took {completionElapsedMilliseconds:0.00} ms, exceeding the {action.CompletionBudgetMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
        if (maximum > action.MaximumXamlFrameMilliseconds)
        {
            throw new InvalidOperationException(
                $"Navigation selection maximum XAML frame was {maximum:0.00} ms, exceeding the {action.MaximumXamlFrameMilliseconds:0.00} ms budget. Report: {reportPath}");
        }
    }

    private static List<double> CaptureXamlFrameDurations(
        string outDir,
        string fileStem,
        int processId,
        Action action)
    {
        var sessionName = $"GwXaml-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];
        var etlPath = Path.Combine(outDir, $"{fileStem}-xaml.etl");
        var csvPath = Path.Combine(outDir, $"{fileStem}-xaml.csv");
        File.Delete(etlPath);
        File.Delete(csvPath);

        var traceStarted = false;
        try
        {
            RunProcess(
                "logman",
                "start",
                sessionName,
                "-p",
                "{531A35AB-63CE-4BCF-AA98-F88C7A89E455}",
                "0xffffffffffffffff",
                "5",
                "-o",
                etlPath,
                "-ets");
            traceStarted = true;
            action();
        }
        finally
        {
            if (traceStarted)
                RunProcess("logman", "stop", sessionName, "-ets");
        }

        RunProcess("tracerpt", etlPath, "-of", "CSV", "-o", csvPath, "-y");
        return ReadXamlFrameDurations(csvPath, processId);
    }

    private static AutomationElement FindFocusableScrollDescendant(AutomationElement element)
    {
        var listItem = element.FindFirst(
                   TreeScope.Descendants,
                   new AndCondition(
                       new PropertyCondition(
                           AutomationElement.ControlTypeProperty,
                           ControlType.ListItem),
                       new PropertyCondition(
                           AutomationElement.IsKeyboardFocusableProperty,
                           true)));
        if (listItem is not null)
            return listItem;
        return element.FindFirst(
                   TreeScope.Descendants,
                   new PropertyCondition(
                       AutomationElement.IsKeyboardFocusableProperty,
                       true))
               ?? throw new InvalidOperationException(
                   "Scrollable surface has no focusable descendant.");
    }

    private static List<double> ReadXamlFrameDurations(string csvPath, int processId)
    {
        using var parser = new TextFieldParser(csvPath)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields()
            ?? throw new InvalidOperationException("XAML trace CSV has no header.");
        var indexes = headers
            .Select((header, index) => (header: header.Trim(), index))
            .ToDictionary(item => item.header, item => item.index, StringComparer.Ordinal);
        var events = new List<(string Type, string ThreadId, long Timestamp)>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length < headers.Length)
                continue;
            if (!int.TryParse(fields[indexes["Task"]].Trim(), out var task)
                || task != 22
                || !TryParseHexProcessId(fields[indexes["PID"]], out var eventProcessId)
                || eventProcessId != processId
                || !long.TryParse(
                    fields[indexes["Clock-Time"]].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var timestamp))
            {
                continue;
            }
            events.Add((
                fields[indexes["Type"]].Trim(),
                fields[indexes["TID"]].Trim(),
                timestamp));
        }

        var stacks = new Dictionary<string, Stack<long>>(StringComparer.Ordinal);
        var durations = new List<double>();
        foreach (var frameEvent in events.OrderBy(item => item.Timestamp))
        {
            if (!stacks.TryGetValue(frameEvent.ThreadId, out var stack))
            {
                stack = new Stack<long>();
                stacks[frameEvent.ThreadId] = stack;
            }
            if (frameEvent.Type.Equals("Start", StringComparison.OrdinalIgnoreCase))
            {
                stack.Push(frameEvent.Timestamp);
            }
            else if (frameEvent.Type.Equals("Stop", StringComparison.OrdinalIgnoreCase)
                     && stack.Count > 0)
            {
                durations.Add(
                    (frameEvent.Timestamp - stack.Pop())
                    / (double)TimeSpan.TicksPerMillisecond);
            }
        }
        return durations;
    }

    private static bool TryParseHexProcessId(string value, out int processId)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return int.TryParse(
            trimmed,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out processId);
    }

    private static void RunProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with {process.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
        }
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        var rank = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sortedValues[lower];
        var weight = rank - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * weight);
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

    private static async Task RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
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
        VisualVerificationMergeEvidence.NormalizeCaptureFileName(value);

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
    private const uint WindowMessageSystemKeyDown = 0x0104;
    private const uint WindowMessageSystemKeyUp = 0x0105;

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
    bool Inspect,
    bool MergeEvidence)
{
    public static RunnerOptions Parse(string[] args)
    {
        string? scenario = null;
        string? repoRoot = null;
        string? outDir = null;
        var noBuild = false;
        var keepWorkingDir = false;
        var inspect = false;
        var mergeEvidence = false;

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
                case "--merge-evidence":
                    mergeEvidence = true;
                    break;
                default:
                    throw new FormatException($"Unknown argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(scenario))
            throw new FormatException("Usage: Glasswork.VisualVerification --scenario <path> [--repo-root <path>] [--out-dir <path>] [--no-build] [--inspect] [--merge-evidence]");

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
            inspect,
            mergeEvidence);
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
    int ProcessId,
    string VaultPath,
    string UiStatePath,
    string InstanceKey,
    IReadOnlyList<CaptureResult> Captures,
    string? InspectionPath = null,
    string? SuggestedScenarioPath = null,
    VerificationEvidence? Evidence = null);

internal sealed record InspectionPaths(string InspectionPath, string SuggestedScenarioPath);

internal sealed record CaptureResult(
    string Name,
    string Path,
    int Width,
    int Height,
    int UniqueSampledColors);

internal sealed record ImageStats(int Width, int Height, int UniqueSampledColors);

internal sealed record VerificationEvidence(
    int SchemaVersion,
    EvidenceSource Source,
    EvidenceBuild Build,
    IReadOnlyList<VisualVerificationEvidenceFile> Captures);

internal sealed record EvidenceSource(
    string Commit,
    string Tree,
    string ScenarioId,
    string ScenarioSha256);

internal sealed record EvidenceBuild(
    string Configuration,
    string Platform,
    string TargetFramework,
    string LaunchManifestSha256,
    IReadOnlyList<VisualVerificationEvidenceFile> Outputs);

internal sealed record VerificationFailure(bool Success, string Stage, string Message);
