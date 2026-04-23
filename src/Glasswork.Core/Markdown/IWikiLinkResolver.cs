namespace Glasswork.Core.Markdown;

/// <summary>
/// Resolves a wiki-link stem to a target classification. Implementations are
/// expected to be cheap (called once per <c>[[stem]]</c> at parse time) and
/// thread-safe enough for the parser's single-threaded consumption.
/// </summary>
public interface IWikiLinkResolver
{
    WikiLinkResolution Resolve(string stem);
}
