namespace PbxAdmin.LoadTests.Validation.Layer1;

public sealed record SdkSnapshot
{
    public required string CallId { get; init; }
    public required string CallerNumber { get; init; }
    public required string Destination { get; init; }
    public required DateTime StartTime { get; init; }
    public DateTime? AnswerTime { get; init; }
    public DateTime? EndTime { get; init; }
    public string? Disposition { get; init; }
    public int? DurationSecs { get; init; }
    public string? UniqueId { get; init; }
    public string? LinkedId { get; init; }
    public string? QueueName { get; init; }
    public string? AgentChannel { get; init; }
    public int EventCount { get; init; }
    public List<CapturedEvent> Events { get; init; } = [];
}
