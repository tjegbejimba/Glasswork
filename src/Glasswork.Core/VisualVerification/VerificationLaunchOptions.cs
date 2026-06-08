using System;
using System.Collections;
using System.Collections.Generic;

namespace Glasswork.Core.VisualVerification;

public sealed record VerificationLaunchOptions(
    string? VaultPath,
    string? UiStatePath,
    string InstanceKey,
    bool SkipProtocolRegistration,
    bool SkipUpdateCheck)
{
    public const string VaultPathVariable = "GLASSWORK_VERIFY_VAULT_PATH";
    public const string UiStatePathVariable = "GLASSWORK_VERIFY_UI_STATE_PATH";
    public const string InstanceKeyVariable = "GLASSWORK_VERIFY_INSTANCE_KEY";
    public const string SkipProtocolRegistrationVariable = "GLASSWORK_SKIP_PROTOCOL_REGISTRATION";
    public const string SkipUpdateCheckVariable = "GLASSWORK_SKIP_UPDATE_CHECK";

    public bool IsVerificationRun =>
        !string.IsNullOrWhiteSpace(VaultPath) ||
        !string.IsNullOrWhiteSpace(UiStatePath) ||
        !string.IsNullOrWhiteSpace(InstanceKey) && InstanceKey != "main" ||
        SkipProtocolRegistration ||
        SkipUpdateCheck;

    public static VerificationLaunchOptions FromProcessEnvironment() =>
        FromEnvironment(ToStringDictionary(Environment.GetEnvironmentVariables()));

    public static VerificationLaunchOptions FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var vaultPath = Read(environment, VaultPathVariable);
        var uiStatePath = Read(environment, UiStatePathVariable);
        var instanceKey = Read(environment, InstanceKeyVariable) ?? "main";

        var explicitSkipProtocol = ReadBool(environment, SkipProtocolRegistrationVariable);
        var explicitSkipUpdate = ReadBool(environment, SkipUpdateCheckVariable);

        var isVerificationRun =
            !string.IsNullOrWhiteSpace(vaultPath) ||
            !string.IsNullOrWhiteSpace(uiStatePath) ||
            instanceKey != "main";

        return new VerificationLaunchOptions(
            vaultPath,
            uiStatePath,
            instanceKey,
            explicitSkipProtocol || isVerificationRun,
            explicitSkipUpdate || isVerificationRun);
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
