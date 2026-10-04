using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace UrlShortener.Api;

/// <summary>
/// Fixed-window limit on link creation, partitioned per client IP so one caller cannot exhaust
/// the budget for everyone. Redirects are deliberately not limited.
/// </summary>
public static class CreateLinksRateLimit
{
    public const string PolicyName = "create-links";
    public const string SectionName = "RateLimiting:CreateLinks";

    public static IServiceCollection AddCreateLinksRateLimit(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var permitLimit = section.GetValue("PermitLimit", 20);
        var window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", 60));

        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests", Detail = "Link creation rate limit exceeded. Retry later." },
                });
            };
        });
    }
}
