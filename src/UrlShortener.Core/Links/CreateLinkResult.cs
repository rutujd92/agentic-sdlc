namespace UrlShortener.Core.Links;

/// <summary>Outcome of <see cref="LinkService.CreateAsync"/>; each case maps to one HTTP response.</summary>
public abstract record CreateLinkResult
{
    private CreateLinkResult()
    {
    }

    public sealed record Created(Link Link) : CreateLinkResult;

    public sealed record InvalidUrl(string Message) : CreateLinkResult;

    public sealed record InvalidAlias(string Message) : CreateLinkResult;

    public sealed record AliasTaken : CreateLinkResult;

    /// <summary>Every generated code collided; practically unreachable with 62^7 codes unless the store is near full.</summary>
    public sealed record CodeSpaceExhausted : CreateLinkResult;
}
