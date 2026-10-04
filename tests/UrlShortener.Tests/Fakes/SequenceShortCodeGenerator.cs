using UrlShortener.Core.Links;

namespace UrlShortener.Tests.Fakes;

/// <summary>Returns the given codes in order, so tests can force collisions deterministically.</summary>
public sealed class SequenceShortCodeGenerator(params string[] codes) : IShortCodeGenerator
{
    private readonly Queue<string> _codes = new(codes);

    public string Generate() => _codes.Dequeue();
}
