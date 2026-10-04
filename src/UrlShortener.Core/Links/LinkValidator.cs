using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace UrlShortener.Core.Links;

/// <summary>
/// Validates long URLs and custom aliases against the security rules in CLAUDE.md.
/// </summary>
public sealed partial class LinkValidator
{
    public const int MaxUrlLength = 2048;
    public const int MinAliasLength = 3;
    public const int MaxAliasLength = 30;

    // Aliases share the root path with API routes, so names used by routes are reserved.
    // Route matching is case-insensitive, so the check is too.
    private static readonly FrozenSet<string> ReservedAliases =
        new[] { "api", "health", "openapi", "swagger", "favicon.ico" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _ownHost;

    /// <param name="ownHost">Host the shortener is served from; links back to it are rejected to prevent redirect loops.</param>
    public LinkValidator(string ownHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownHost);
        _ownHost = ownHost;
    }

    public LinkValidationResult ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return LinkValidationResult.Invalid("URL is required.");
        }

        if (url.Length > MaxUrlLength)
        {
            return LinkValidationResult.Invalid($"URL must be at most {MaxUrlLength} characters.");
        }

        // On Unix, "/path" parses as an absolute file URI, so the scheme check below is what rejects it.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return LinkValidationResult.Invalid("URL must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return LinkValidationResult.Invalid("URL must not contain user information.");
        }

        if (string.Equals(uri.Host, _ownHost, StringComparison.OrdinalIgnoreCase))
        {
            return LinkValidationResult.Invalid("URL must not point to this shortener.");
        }

        return LinkValidationResult.Valid;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Part of the validator's instance API alongside ValidateUrl.")]
    public LinkValidationResult ValidateAlias(string? alias)
    {
        if (alias is null)
        {
            return LinkValidationResult.Valid;
        }

        if (!AliasPattern().IsMatch(alias))
        {
            return LinkValidationResult.Invalid(
                $"Alias must be {MinAliasLength}-{MaxAliasLength} characters of letters, digits, '-' or '_'.");
        }

        if (ReservedAliases.Contains(alias))
        {
            return LinkValidationResult.Invalid($"Alias '{alias}' is reserved.");
        }

        return LinkValidationResult.Valid;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{3,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
