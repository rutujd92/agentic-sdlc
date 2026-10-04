using UrlShortener.Core.Links;

namespace UrlShortener.Tests.Fakes;

public sealed class InMemoryLinkRepository : ILinkRepository
{
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);

    public int AddAttempts { get; private set; }

    public IReadOnlyCollection<Link> Links => _links.Values;

    public void Seed(string code) =>
        _links[code] = new Link { Code = code, LongUrl = "https://existing.example", CreatedAt = DateTimeOffset.UnixEpoch };

    public Task<AddLinkResult> AddAsync(Link link, CancellationToken cancellationToken)
    {
        AddAttempts++;
        return Task.FromResult(_links.TryAdd(link.Code, link) ? AddLinkResult.Added : AddLinkResult.DuplicateCode);
    }

    public Task<Link?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.GetValueOrDefault(code));

    public Task<bool> IncrementClicksAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.ContainsKey(code));
}
