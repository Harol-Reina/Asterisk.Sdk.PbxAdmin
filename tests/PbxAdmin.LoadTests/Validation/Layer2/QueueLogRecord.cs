namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed record QueueLogRecord
{
    public long Id { get; init; }
    public DateTime Time { get; init; }
    public string CallId { get; init; } = "";
    public string QueueName { get; init; } = "";
    public string Agent { get; init; } = "";
    public string Event { get; init; } = "";
    public string Data1 { get; init; } = "";
    public string Data2 { get; init; } = "";
    public string Data3 { get; init; } = "";
    public string Data4 { get; init; } = "";
    public string Data5 { get; init; } = "";
}
