using Microsoft.EntityFrameworkCore;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Shortener")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Shortener' is not configured. Set it with dotnet user-secrets (see README).");
builder.Services.AddInfrastructure(connectionString);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ShortenerDbContext>().Database.MigrateAsync();
}

await app.RunAsync();
