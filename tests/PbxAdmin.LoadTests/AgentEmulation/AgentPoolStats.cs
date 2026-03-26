namespace PbxAdmin.LoadTests.AgentEmulation;

public sealed record AgentPoolStats
{
    public int Total { get; init; }
    public int Idle { get; init; }
    public int Ringing { get; init; }
    public int InCall { get; init; }
    public int OnHold { get; init; }
    public int Wrapup { get; init; }
    public int Error { get; init; }
    public int TotalCallsHandled { get; init; }
    public DateTime Timestamp { get; init; }
}
