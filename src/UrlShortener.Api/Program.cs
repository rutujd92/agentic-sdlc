using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrlShortener.Api;
using UrlShortener.Api.Links;
using UrlShortener.Core.Links;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Shortener")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Shortener' is not configured. Set it with dotnet user-secrets (see README).");
builder.Services.AddInfrastructure(connectionString);

builder.Services.AddOptions<ShortenerOptions>()
    .BindConfiguration(ShortenerOptions.SectionName)
    .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Shortener:BaseUrl must be an absolute URL.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
builder.Services.AddSingleton(sp => new LinkValidator(sp.GetRequiredService<IOptions<ShortenerOptions>>().Value.BaseUrl.Host));
builder.Services.AddScoped<LinkService>();
builder.Services.AddProblemDetails();
builder.Services.AddCreateLinksRateLimit(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<ShortenerDbContext>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ShortenerDbContext>().Database.MigrateAsync();
}

app.UseStatusCodePages();
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapLinkEndpoints();

await app.RunAsync();
