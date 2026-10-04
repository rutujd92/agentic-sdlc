using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Core.Links;

namespace UrlShortener.Infrastructure.Persistence;

public sealed class EfLinkRepository(ShortenerDbContext context) : ILinkRepository
{
    private const string PostgresUniqueViolation = "23505";
    private const string SqliteUniqueViolation = "UNIQUE constraint failed";

    public async Task<AddLinkResult> AddAsync(Link link, CancellationToken cancellationToken)
    {
        context.Links.Add(link);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return AddLinkResult.Added;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Detach so the failed insert is not retried by the next SaveChanges on this context.
            context.Entry(link).State = EntityState.Detached;
            return AddLinkResult.DuplicateCode;
        }
    }

    public Task<Link?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        context.Links.AsNoTracking().SingleOrDefaultAsync(l => l.Code == code, cancellationToken);

    public async Task<bool> IncrementClicksAsync(string code, CancellationToken cancellationToken)
    {
        // Single UPDATE ... SET click_count = click_count + 1, so concurrent redirects never lose counts.
        var updated = await context.Links
            .Where(l => l.Code == code)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ClickCount, l => l.ClickCount + 1), cancellationToken);

        return updated > 0;
    }

    // Provider-neutral check so Infrastructure does not depend on the SQLite test provider:
    // PostgreSQL reports SQLSTATE 23505; SQLite sets no SQLSTATE, and its unique-violation message is a fixed string.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is DbException db
        && (db.SqlState == PostgresUniqueViolation
            || db.Message.Contains(SqliteUniqueViolation, StringComparison.Ordinal));
}
