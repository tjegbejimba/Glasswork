namespace Glasswork.Core.AppUpdate;

public interface IMcpInstalledVersionProvider
{
    Task<McpInstalledVersionResult> GetInstalledVersionAsync();
}

public sealed record McpInstalledVersionResult(
    bool IsSuccess,
    bool IsInstalled,
    AppVersion? Version,
    string? FailureReason)
{
    public static McpInstalledVersionResult Installed(AppVersion version) =>
        new(true, true, version, null);

    public static McpInstalledVersionResult NotInstalled() =>
        new(true, false, null, null);

    public static McpInstalledVersionResult InstalledUnknown() =>
        new(true, true, null, null);

    public static McpInstalledVersionResult Failed(string reason) =>
        new(false, false, null, reason);
}
