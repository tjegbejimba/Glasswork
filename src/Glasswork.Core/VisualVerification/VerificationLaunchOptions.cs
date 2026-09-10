using System;
using System.Collections;
using System.Collections.Generic;

namespace Glasswork.Core.VisualVerification;

public sealed record VerificationLaunchOptions(
    string? VaultPath,
    string? UiStatePath,
    string InstanceKey,
    bool SkipProtocolRegistration,
    bool SkipUpdateCheck,
    string? StartPage = null,
    string? StartupGatePath = null,
    string? SupplementalGatePath = null,
    int FailStartupAttempts = 0,
    string? UpdateInstallState = null)
{
    public const string VaultPathVariable = "GLASSWORK_VERIFY_VAULT_PATH";
    public const string UiStatePathVariable = "GLASSWORK_VERIFY_UI_STATE_PATH";
    public const string InstanceKeyVariable = "GLASSWORK_VERIFY_INSTANCE_KEY";
    public const string SkipProtocolRegistrationVariable = "GLASSWORK_SKIP_PROTOCOL_REGISTRATION";
    public const string SkipUpdateCheckVariable = "GLASSWORK_SKIP_UPDATE_CHECK";
    public const string CaptureRequestPathVariable = "GLASSWORK_VERIFY_CAPTURE_REQUEST";
    public const string CaptureOutputPathVariable = "GLASSWORK_VERIFY_CAPTURE_OUTPUT";
    public const string StartPageVariable = "GLASSWORK_VERIFY_START_PAGE";
    public const string StartupGatePathVariable = "GLASSWORK_VERIFY_STARTUP_GATE_PATH";
    public const string SupplementalGatePathVariable =
        "GLASSWORK_VERIFY_SUPPLEMENTAL_GATE_PATH";
    public const string FailStartupAttemptsVariable = "GLASSWORK_VERIFY_FAIL_STARTUP_ATTEMPTS";
    public const string UpdateInstallStateVariable = "GLASSWORK_VERIFY_UPDATE_INSTALL_STATE";

    public bool IsVerificationRun =>
        !string.IsNullOrWhiteSpace(VaultPath) ||
        !string.IsNullOrWhiteSpace(UiStatePath) ||
        !string.IsNullOrWhiteSpace(InstanceKey) && InstanceKey != "main" ||
        SkipProtocolRegistration ||
        SkipUpdateCheck ||
        StartPage is not null ||
        StartupGatePath is not null ||
        SupplementalGatePath is not null ||
        FailStartupAttempts > 0 ||
        UpdateInstallState is not null;

    public static VerificationLaunchOptions FromProcessEnvironment() =>
        FromEnvironment(ToStringDictionary(Environment.GetEnvironmentVariables()));

    public static VerificationLaunchOptions FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var vaultPath = Read(environment, VaultPathVariable);
        var uiStatePath = Read(environment, UiStatePathVariable);
        var instanceKey = Read(environment, InstanceKeyVariable) ?? "main";
        var startPage = Read(environment, StartPageVariable);
        var startupGatePath = Read(environment, StartupGatePathVariable);
        var supplementalGatePath = Read(environment, SupplementalGatePathVariable);
        var failStartupAttempts = ReadNonNegativeInt(
            environment,
            FailStartupAttemptsVariable);
        var updateInstallState = Read(environment, UpdateInstallStateVariable)?.ToLowerInvariant();
        if (updateInstallState is not null and not ("app" or "mcp"))
            throw new FormatException($"{UpdateInstallStateVariable} must be app or mcp.");
        if (startPage is not null && startPage != "planner")
            throw new FormatException($"Unsupported verification start page '{startPage}'.");
        if (startPage == "planner"
            && (string.IsNullOrWhiteSpace(vaultPath)
                || string.IsNullOrWhiteSpace(uiStatePath)
                || instanceKey == "main"))
        {
            throw new FormatException(
                "Planner verification start requires isolated Vault, UI state, and instance paths.");
        }

        var explicitSkipProtocol = ReadBool(environment, SkipProtocolRegistrationVariable);
        var explicitSkipUpdate = ReadBool(environment, SkipUpdateCheckVariable);

        var isVerificationRun =
            !string.IsNullOrWhiteSpace(vaultPath) ||
            !string.IsNullOrWhiteSpace(uiStatePath) ||
            instanceKey != "main" ||
            startPage is not null;
        if ((startupGatePath is not null
                || supplementalGatePath is not null
                || failStartupAttempts > 0)
            && (string.IsNullOrWhiteSpace(vaultPath)
                || string.IsNullOrWhiteSpace(uiStatePath)
                || instanceKey == "main"))
        {
            throw new FormatException(
                "Startup verification controls require isolated Vault, UI state, and instance paths.");
        }

        return new VerificationLaunchOptions(
            vaultPath,
            uiStatePath,
            instanceKey,
            explicitSkipProtocol || isVerificationRun,
            explicitSkipUpdate || isVerificationRun,
            startPage,
            startupGatePath,
            supplementalGatePath,
            failStartupAttempts,
            updateInstallState);
    }

    private static string? Read(IReadOnlyDictionary<string, string?> environment, string key)
    {
        if (environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        foreach (var entry in environment)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Value))
                return entry.Value;
        }

        return null;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string?> environment, string key)
    {
        var value = Read(environment, key);
        return value is not null &&
               (value == "1" ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static int ReadNonNegativeInt(
        IReadOnlyDictionary<string, string?> environment,
        string key)
    {
        var value = Read(environment, key);
        if (value is null)
            return 0;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new FormatException($"{key} must be a non-negative integer.");
        return parsed;
    }

    private static Dictionary<string, string?> ToStringDictionary(IDictionary source)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is string key)
                result[key] = entry.Value as string;
        }
        return result;
    }
}
