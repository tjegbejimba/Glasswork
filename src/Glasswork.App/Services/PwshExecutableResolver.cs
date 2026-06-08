using System;
using System.IO;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Services;

/// <summary>
/// Resolves the PowerShell 7 (<c>pwsh</c>) executable for the self-update launcher.
/// Modeled on <see cref="GhCliIssueFiler"/>'s ResolveGhPath: WinUI apps often launch
/// without inheriting the user PATH that contains pwsh, so we probe the common install
/// location first and fall back to the bare command name on PATH.
/// </summary>
public sealed class PwshExecutableResolver : IExecutableResolver
{
    public string? Resolve(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;

        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WinGet\Links\pwsh.exe"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return "pwsh.exe";
    }
}
