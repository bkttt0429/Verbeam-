namespace Verbeam.Core.Models;

public sealed record MortTranslateRequest
{
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? Target { get; init; }
    public string? Source { get; init; }
    public string? Mode { get; init; }
    public string? Glossary { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string>? ContextItems { get; init; }
}

public sealed record MortTranslateResponse(string Result, string ErrorCode, string ErrorMessage)
{
    public static MortTranslateResponse Success(string result) => new(result, "0", string.Empty);

    public static MortTranslateResponse Error(string fallbackResult, string errorMessage, string errorCode = "1")
        => new(fallbackResult, errorCode, errorMessage);
}
