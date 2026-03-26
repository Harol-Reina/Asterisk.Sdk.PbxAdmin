namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed record CelRecord
{
    public long Id { get; init; }
    public string EventType { get; init; } = "";
    public DateTime EventTime { get; init; }
    public string CidName { get; init; } = "";
    public string CidNum { get; init; } = "";
    public string Exten { get; init; } = "";
    public string Context { get; init; } = "";
    public string ChanName { get; init; } = "";
    public string AppName { get; init; } = "";
    public string AppData { get; init; } = "";
    public string UniqueId { get; init; } = "";
    public string LinkedId { get; init; } = "";
    public string Peer { get; init; } = "";
    public string Extra { get; init; } = "";
}
