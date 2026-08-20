namespace Glasswork.Core.Models;

public enum SizeBucket
{
    Quick,
    Short,
    Focus,
    Deep,
    BreakDown,
}

public static class SizeBuckets
{
    public static bool TryParse(string? raw, out SizeBucket bucket)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "quick":
                bucket = SizeBucket.Quick;
                return true;
            case "short":
                bucket = SizeBucket.Short;
                return true;
            case "focus":
                bucket = SizeBucket.Focus;
                return true;
            case "deep":
                bucket = SizeBucket.Deep;
                return true;
            case "break_down":
                bucket = SizeBucket.BreakDown;
                return true;
            default:
                bucket = default;
                return false;
        }
    }

    public static string Canonical(SizeBucket bucket) => bucket switch
    {
        SizeBucket.Quick => "quick",
        SizeBucket.Short => "short",
        SizeBucket.Focus => "focus",
        SizeBucket.Deep => "deep",
        SizeBucket.BreakDown => "break_down",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

    public static string? NormalizeRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return TryParse(raw, out var bucket)
            ? Canonical(bucket)
            : raw;
    }
}
