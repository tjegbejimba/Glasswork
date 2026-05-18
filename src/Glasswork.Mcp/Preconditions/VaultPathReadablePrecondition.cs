namespace Glasswork.Mcp.Preconditions;

/// <summary>
/// Returns <see cref="ToolPreconditionResult.Ok"/> when the configured vault
/// directory exists. Any other state (null path, directory missing) returns
/// <see cref="ToolPreconditionResult.Unavailable"/> so vault-dependent tools
/// are filtered out of <c>ListTools</c> responses.
/// </summary>
public sealed class VaultPathReadablePrecondition : IToolPrecondition
{
    public const string PreconditionName = "vault-path-readable";

    private readonly VaultContext _vaultContext;

    public VaultPathReadablePrecondition(VaultContext vaultContext)
    {
        _vaultContext = vaultContext;
    }

    public string Name => PreconditionName;

    public ToolPreconditionResult Evaluate()
    {
        var path = _vaultContext.VaultPath;
        if (string.IsNullOrWhiteSpace(path))
            return ToolPreconditionResult.Unavailable(
                "Vault path is not configured. Set GLASSWORK_VAULT or create a vault.");

        if (!Directory.Exists(path))
            return ToolPreconditionResult.Unavailable(
                $"Vault directory does not exist: {path}");

        return ToolPreconditionResult.Ok();
    }
}
