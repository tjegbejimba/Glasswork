namespace Glasswork.Core.Models;

/// <summary>
/// A structured external pointer stored in task frontmatter. Each link has a recognized
/// type (ado, pr, incident, doc, build, other), a value (URL or identifier), and an
/// optional display label.
/// </summary>
public record TaskLink
{
    public required string Type { get; init; }
    public required string Value { get; init; }
    public string? Label { get; init; }

    /// <summary>
    /// Recognized link types as constants.
    /// </summary>
    public static class Types
    {
        public const string Ado = "ado";
        public const string Pr = "pr";
        public const string Incident = "incident";
        public const string Doc = "doc";
        public const string Build = "build";
        public const string Other = "other";

        /// <summary>
        /// Normalize a raw type string to a recognized type constant.
        /// Unknown types are coerced to "other" for forward compatibility.
        /// </summary>
        public static string Normalize(string? rawType) =>
            rawType switch
            {
                Ado or Pr or Incident or Doc or Build => rawType,
                _ when !string.IsNullOrWhiteSpace(rawType) => Other,
                _ => Other
            };
    }
}
