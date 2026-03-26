namespace PbxAdmin.LoadTests.CallGeneration;

public sealed record CallGenerationResult
{
    public required string CallId { get; init; }
    public required CallerProfile Caller { get; init; }
    public required string Destination { get; init; }
    public required DateTime Timestamp { get; init; }
    public required bool Accepted { get; init; }
    public string? ErrorMessage { get; init; }
}
