namespace PbxAdmin.LoadTests.Validation.Layer1;

public sealed record CapturedEvent
{
    public required string EventType { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Channel { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new();
}
