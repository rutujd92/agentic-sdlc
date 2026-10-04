using Microsoft.EntityFrameworkCore;
using UrlShortener.Core.Links;

namespace UrlShortener.Infrastructure.Persistence;

public sealed class ShortenerDbContext(DbContextOptions<ShortenerDbContext> options) : DbContext(options)
{
    public DbSet<Link> Links => Set<Link>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<Link>();
        link.ToTable("links");
        link.HasKey(l => l.Id);
        link.Property(l => l.Code).HasMaxLength(LinkValidator.MaxAliasLength).IsRequired();
        link.HasIndex(l => l.Code).IsUnique();
        link.Property(l => l.LongUrl).HasMaxLength(LinkValidator.MaxUrlLength).IsRequired();
        link.Property(l => l.CreatedAt).IsRequired();
        link.Property(l => l.ClickCount).IsRequired();
    }
}
