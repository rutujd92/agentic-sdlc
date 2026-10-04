namespace UrlShortener.Core.Links;

public sealed class LinkService(
    ILinkRepository repository,
    IShortCodeGenerator codeGenerator,
    LinkValidator validator,
    TimeProvider timeProvider)
{
    public const int MaxCodeGenerationAttempts = 5;

    public async Task<CreateLinkResult> CreateAsync(string? url, string? alias, CancellationToken cancellationToken)
    {
        var longUrl = url?.Trim();

        var urlValidation = validator.ValidateUrl(longUrl);
        if (!urlValidation.IsValid)
        {
            return new CreateLinkResult.InvalidUrl(urlValidation.Error!);
        }

        var aliasValidation = validator.ValidateAlias(alias);
        if (!aliasValidation.IsValid)
        {
            return new CreateLinkResult.InvalidAlias(aliasValidation.Error!);
        }

        if (alias is not null)
        {
            var link = NewLink(alias, longUrl!);
            return await repository.AddAsync(link, cancellationToken) == AddLinkResult.Added
                ? new CreateLinkResult.Created(link)
                : new CreateLinkResult.AliasTaken();
        }

        // Insert-then-retry instead of check-then-insert: the unique index is the source of truth,
        // so two concurrent requests can never both claim the same code.
        for (var attempt = 0; attempt < MaxCodeGenerationAttempts; attempt++)
        {
            var link = NewLink(codeGenerator.Generate(), longUrl!);
            if (await repository.AddAsync(link, cancellationToken) == AddLinkResult.Added)
            {
                return new CreateLinkResult.Created(link);
            }
        }

        return new CreateLinkResult.CodeSpaceExhausted();
    }

    private Link NewLink(string code, string longUrl) =>
        new() { Code = code, LongUrl = longUrl, CreatedAt = timeProvider.GetUtcNow() };
}
