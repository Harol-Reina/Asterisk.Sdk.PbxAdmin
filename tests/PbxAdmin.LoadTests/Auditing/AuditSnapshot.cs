using System.Text.Json.Serialization;

namespace PbxAdmin.LoadTests.Auditing;

/// <summary>One point-in-time snapshot of the entire Docker stack infrastructure.</summary>
public sealed record AuditSnapshot
{
    public required DateTime Timestamp { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required int SequenceNumber { get; init; }
    public required ContainerSnapshot[] Containers { get; init; }
    public required AsteriskSnapshot Realtime { get; init; }
    public required AsteriskBasicSnapshot Pstn { get; init; }
    public required AsteriskBasicSnapshot File { get; init; }
    public required ErrorEntry[] Errors { get; init; }
}

public sealed record ContainerSnapshot
{
    public required string Name { get; init; }
    public double CpuPercent { get; init; }
    public double MemUsageMB { get; init; }
    public double MemLimitMB { get; init; }
    public double NetInputMB { get; init; }
    public double NetOutputMB { get; init; }
}

public sealed record AsteriskSnapshot
{
    public int ActiveChannels { get; init; }
    public int ActiveCalls { get; init; }
    public int CallsProcessed { get; init; }
    public int OdbcActiveConnections { get; init; }
    public int OdbcMaxConnections { get; init; }
    public int EndpointCount { get; init; }
    public QueueSnapshot? Queue { get; init; }
    public string? RtpRawOutput { get; init; }
}

public sealed record QueueSnapshot
{
    public int CallsWaiting { get; init; }
    public int Completed { get; init; }
    public int Abandoned { get; init; }
    public int Holdtime { get; init; }
    public int Talktime { get; init; }
    public int MembersIdle { get; init; }
    public int MembersInUse { get; init; }
    public int MembersRinging { get; init; }
    public int MembersUnavailable { get; init; }
}

public sealed record AsteriskBasicSnapshot
{
    public int ActiveChannels { get; init; }
    public int ActiveCalls { get; init; }
    public int CallsProcessed { get; init; }
}

public sealed record ErrorEntry
{
    public required string Container { get; init; }
    public required string Message { get; init; }
}

/// <summary>Wraps all snapshots for the consolidated JSON output.</summary>
public sealed record AuditReport
{
    public required DateTime TestStarted { get; init; }
    public required DateTime TestEnded { get; init; }
    public required int IntervalSeconds { get; init; }
    public required int SnapshotCount { get; init; }
    public required AuditSnapshot[] Snapshots { get; init; }
}
