using UrlShortener.Core.Links;

namespace UrlShortener.Tests.Core;

public class ShortCodeGeneratorTests
{
    private readonly RandomShortCodeGenerator _generator = new();

    [Fact]
    public void Generate_returns_code_of_seven_characters()
    {
        Assert.Equal(7, _generator.Generate().Length);
    }

    [Fact]
    public void Generate_uses_only_alphanumeric_alphabet()
    {
        for (var i = 0; i < 1_000; i++)
        {
            Assert.All(_generator.Generate(), c => Assert.Contains(c, RandomShortCodeGenerator.Alphabet));
        }
    }

    [Fact]
    public void Generate_produces_no_duplicates_in_ten_thousand_codes()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => _generator.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(10_000, codes.Count);
    }

    [Fact]
    public void Alphabet_is_digits_then_upper_then_lower_case()
    {
        Assert.Equal("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", RandomShortCodeGenerator.Alphabet);
    }
}
