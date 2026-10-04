using UrlShortener.Core.Links;

namespace UrlShortener.Tests.Core;

public class LinkValidatorTests
{
    private const string OwnHost = "sho.rt";
    private readonly LinkValidator _validator = new(OwnHost);

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?query=1#fragment")]
    [InlineData("HTTPS://Example.com")]
    [InlineData("https://sub.example.co.uk:8443/a/b")]
    public void ValidateUrl_accepts_absolute_http_and_https(string url)
    {
        var result = _validator.ValidateUrl(url);

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    [InlineData("example.com/no-scheme")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateUrl_rejects_unsafe_or_non_absolute_urls(string? url)
    {
        var result = _validator.ValidateUrl(url);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void ValidateUrl_accepts_url_of_exactly_max_length()
    {
        var url = "https://example.com/" + new string('a', LinkValidator.MaxUrlLength - "https://example.com/".Length);

        Assert.True(_validator.ValidateUrl(url).IsValid);
    }

    [Fact]
    public void ValidateUrl_rejects_url_over_max_length()
    {
        var url = "https://example.com/" + new string('a', LinkValidator.MaxUrlLength);

        Assert.False(_validator.ValidateUrl(url).IsValid);
    }

    [Theory]
    [InlineData("https://user@example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://paypal.com@evil.example")]
    public void ValidateUrl_rejects_urls_with_user_info(string url)
    {
        Assert.False(_validator.ValidateUrl(url).IsValid);
    }

    [Theory]
    [InlineData("https://sho.rt/abc1234")]
    [InlineData("http://SHO.RT/x")]
    public void ValidateUrl_rejects_links_to_own_host(string url)
    {
        Assert.False(_validator.ValidateUrl(url).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("my-link_2025")]
    [InlineData("ABCdef123")]
    [InlineData("a23456789012345678901234567890")]
    public void ValidateAlias_accepts_null_and_valid_aliases(string? alias)
    {
        var result = _validator.ValidateAlias(alias);

        Assert.True(result.IsValid, result.Error);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901")]
    [InlineData("has space")]
    [InlineData("bad!")]
    [InlineData("dot.ted")]
    [InlineData("slash/ed")]
    [InlineData("ünïcode")]
    [InlineData("")]
    public void ValidateAlias_rejects_wrong_length_or_characters(string alias)
    {
        var result = _validator.ValidateAlias(alias);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("health")]
    [InlineData("openapi")]
    [InlineData("swagger")]
    public void ValidateAlias_rejects_reserved_words_case_insensitively(string alias)
    {
        var result = _validator.ValidateAlias(alias);

        Assert.False(result.IsValid);
        Assert.Contains("reserved", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
