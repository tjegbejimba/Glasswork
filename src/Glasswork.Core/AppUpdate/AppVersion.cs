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
        var parts = versionString.Split('.');
        
        if (parts.Length < 3)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
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
}
