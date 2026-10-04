namespace UrlShortener.Core.Links;

public interface ILinkRepository
{
    /// <summary>
    /// Inserts the link. Uniqueness of <see cref="Link.Code"/> is enforced by the store,
    /// so callers never need a separate existence check.
    /// </summary>
    Task<AddLinkResult> AddAsync(Link link, CancellationToken cancellationToken);

    Task<Link?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Atomically increments the click count. Returns false when the code is unknown.</summary>
    Task<bool> IncrementClicksAsync(string code, CancellationToken cancellationToken);
}
