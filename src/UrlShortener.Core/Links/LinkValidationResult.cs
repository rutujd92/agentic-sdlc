namespace UrlShortener.Core.Links;

public sealed record LinkValidationResult(bool IsValid, string? Error)
{
    public static LinkValidationResult Valid { get; } = new(true, null);

    public static LinkValidationResult Invalid(string error) => new(false, error);
}
