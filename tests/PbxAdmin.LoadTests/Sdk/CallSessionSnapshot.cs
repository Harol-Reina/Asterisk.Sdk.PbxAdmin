namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Immutable snapshot of a CallSession captured at completion time.
/// </summary>
public sealed record CallSessionSnapshot
{
    public required string SessionId { get; init; }
    public string? CallerNumber { get; init; }
    public string? LinkedId { get; init; }
    public string? QueueName { get; init; }
    public string? AgentInterface { get; init; }
    public string? FinalState { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? AnswerTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? TalkTime { get; init; }
    public int ParticipantCount { get; init; }
}
