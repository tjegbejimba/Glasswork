using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Glasswork.Core.VisualVerification;

public static class VisualVerificationMergeEvidence
{
    public static string NormalizeCaptureFileName(string value) =>
        Regex.Replace(value.Trim(), @"[\\/:*?""<>|]+", "-");

    public static void EnsureUniqueCaptureNames(VisualVerificationScenario scenario)
    {
        var names = scenario.Actions
            .Where(action => action.Type.Trim().Equals("capture", StringComparison.OrdinalIgnoreCase))
            .Select(action => action.Name!)
            .Concat(scenario.Captures.Select(capture => capture.Name));
        var duplicate = names
            .GroupBy(NormalizeCaptureFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException(
                $"Visual verification capture names must produce a unique output filename; '{duplicate.Key}.png' is duplicated.");
        }
    }

    public static VisualVerificationSourceSnapshot CaptureSourceSnapshot(
        string repositoryRoot,
        string scenarioPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var scenario = Path.GetFullPath(scenarioPath);
        var relativeScenario = Path.GetRelativePath(root, scenario);
        if (relativeScenario == ".."
            || relativeScenario.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Merge-evidence scenarios must be committed under the repository root.");
        }

        return new VisualVerificationSourceSnapshot(
            RunGit(root, "rev-parse", "HEAD^{commit}"),
            RunGit(root, "rev-parse", "HEAD^{tree}"),
            RunGit(root, "status", "--porcelain=v1", "--untracked-files=all"),
            HashFile(scenario),
            relativeScenario.Replace(Path.DirectorySeparatorChar, '/'));
    }

    public static VisualVerificationLaunchBundle CaptureLaunchBundle(string launchRoot)
    {
        var root = Path.GetFullPath(launchRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Launch output directory not found: {root}");

        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"Merge-evidence launch output contains a reparse point: {path}");

                return new VisualVerificationEvidenceFile(
                    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                    HashFile(path));
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
            throw new InvalidOperationException("Merge-evidence launch output is empty.");

        var canonical = string.Join(
            "\n",
            files.Select(file => $"{file.Path}\0{file.Sha256}"));
        return new VisualVerificationLaunchBundle(files, HashText(canonical));
    }

    public static void EnsureSourceUnchanged(
        VisualVerificationSourceSnapshot before,
        VisualVerificationSourceSnapshot after)
    {
        if (before != after)
        {
            throw new InvalidOperationException(
                "Repository source or scenario changed during visual verification.");
        }
    }

    public static void EnsureLaunchBundleUnchanged(
        VisualVerificationLaunchBundle before,
        VisualVerificationLaunchBundle after)
    {
        if (before.Sha256 != after.Sha256)
        {
            throw new InvalidOperationException(
                "The launched build output changed during visual verification.");
        }
    }

    public static IReadOnlyList<VisualVerificationEvidenceFile> CaptureVerifiedAuxiliaryBundle(
        VisualVerificationLaunchBundle expected,
        string installedRoot,
        string evidencePathPrefix)
    {
        var installed = CaptureLaunchBundle(installedRoot);
        if (expected.Sha256 != installed.Sha256)
        {
            throw new InvalidOperationException(
                "The installed auxiliary bundle does not match the archived source bundle.");
        }

        var prefix = evidencePathPrefix.Trim().TrimEnd('/');
        return installed.Files
            .Select(file => new VisualVerificationEvidenceFile(
                $"{prefix}/{file.Path}",
                file.Sha256))
            .ToArray();
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error.Trim()}");
        }

        return output.Trim();
    }
}

public sealed record VisualVerificationSourceSnapshot(
    string Commit,
    string Tree,
    string Status,
    string ScenarioSha256,
    string ScenarioId = "");

public sealed record VisualVerificationEvidenceFile(string Path, string Sha256);

public sealed record VisualVerificationLaunchBundle(
    IReadOnlyList<VisualVerificationEvidenceFile> Files,
    string Sha256);
