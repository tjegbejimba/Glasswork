namespace Glasswork.Core.AppUpdate;

public sealed class AppVersion : IComparable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    private AppVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public static bool TryParse(string input, out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var versionString = input.TrimStart('v', 'V');
        
        // Strip SemVer metadata (+...) and pre-release tags (-...)
        // Examples: "1.3.0+8f3a1b2" → "1.3.0", "1.3.0-beta+abc" → "1.3.0"
        var metadataIndex = versionString.IndexOf('+');
        if (metadataIndex >= 0)
            versionString = versionString.Substring(0, metadataIndex);
        
        var preReleaseIndex = versionString.IndexOf('-');
        if (preReleaseIndex >= 0)
            versionString = versionString.Substring(0, preReleaseIndex);
        
        var parts = versionString.Split('.');
        
        // Require exactly 3 or 4 components
        if (parts.Length < 3 || parts.Length > 4)
            return false;

        // Parse and validate first three components (reject negative)
        if (!int.TryParse(parts[0], out var major) || major < 0 ||
            !int.TryParse(parts[1], out var minor) || minor < 0 ||
            !int.TryParse(parts[2], out var patch) || patch < 0)
            return false;

        // If 4th component exists, it must be valid numeric (but we ignore its value)
        if (parts.Length == 4 && !int.TryParse(parts[3], out _))
            return false;

        version = new AppVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
            return 1;

        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
            return majorComparison;

        var minorComparison = Minor.CompareTo(other.Minor);
        if (minorComparison != 0)
            return minorComparison;

        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
