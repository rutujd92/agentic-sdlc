using System.Security.Cryptography;

namespace UrlShortener.Core.Links;

/// <summary>
/// Generates 7-character codes from a 62-character alphabet (62^7 ≈ 3.5 trillion combinations)
/// using a cryptographically secure source. <see cref="RandomNumberGenerator.GetString"/> uses
/// rejection sampling, so every character is equally likely (no modulo bias).
/// </summary>
public sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int CodeLength = 7;

    public string Generate() => RandomNumberGenerator.GetString(Alphabet, CodeLength);
}
