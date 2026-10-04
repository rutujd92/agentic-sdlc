namespace UrlShortener.Core.Links;

public sealed class Link
{
    public long Id { get; private set; }

    public required string Code { get; init; }

    public required string LongUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public long ClickCount { get; private set; }
}
