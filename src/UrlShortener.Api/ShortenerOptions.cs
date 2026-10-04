namespace UrlShortener.Api;

public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Public base URL used to build short links. Never derived from the request Host header.</summary>
    public required Uri BaseUrl { get; init; }
}
