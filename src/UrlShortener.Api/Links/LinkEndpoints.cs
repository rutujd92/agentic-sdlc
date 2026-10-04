using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Links;

namespace UrlShortener.Api.Links;

public static class LinkEndpoints
{
    // Only paths shaped like a code reach the redirect handler; everything else falls through to 404.
    private const string CodeRoute = "/{code:regex(^[A-Za-z0-9_-]{{3,30}}$)}";

    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/links", CreateAsync).WithName("CreateLink");
        app.MapGet(CodeRoute, RedirectAsync).WithName("RedirectLink");
        return app;
    }

    private static async Task<Results<Created<CreateLinkResponse>, ProblemHttpResult>> CreateAsync(
        CreateLinkRequest request,
        LinkService service,
        IOptions<ShortenerOptions> options,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request.Url, request.Alias, cancellationToken);

        return result switch
        {
            CreateLinkResult.Created created => Created(created.Link, options.Value.BaseUrl),
            CreateLinkResult.InvalidUrl invalid => Problem(StatusCodes.Status400BadRequest, "Invalid URL", invalid.Message),
            CreateLinkResult.InvalidAlias invalid => Problem(StatusCodes.Status400BadRequest, "Invalid alias", invalid.Message),
            CreateLinkResult.AliasTaken => Problem(StatusCodes.Status409Conflict, "Alias already taken", $"The alias '{request.Alias}' is already in use."),
            CreateLinkResult.CodeSpaceExhausted => Problem(StatusCodes.Status503ServiceUnavailable, "Could not allocate a code", "Please retry the request."),
            _ => throw new InvalidOperationException($"Unhandled result {result.GetType().Name}."),
        };
    }

    private static async Task<Results<RedirectHttpResult, ProblemHttpResult>> RedirectAsync(
        string code,
        ILinkRepository repository,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var link = await repository.GetByCodeAsync(code, cancellationToken);
        if (link is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Link not found", $"No link exists for '{code}'.");
        }

        await repository.IncrementClicksAsync(code, cancellationToken);

        // no-store so browsers and proxies do not cache the redirect and skip the click count.
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Redirect(link.LongUrl);
    }

    private static Created<CreateLinkResponse> Created(Link link, Uri baseUrl)
    {
        var shortUrl = $"{baseUrl.ToString().TrimEnd('/')}/{link.Code}";
        return TypedResults.Created(shortUrl, new CreateLinkResponse(link.Code, shortUrl, link.LongUrl));
    }

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail);
}

public sealed record CreateLinkRequest(string? Url, string? Alias);

public sealed record CreateLinkResponse(string Code, string ShortUrl, string LongUrl);
