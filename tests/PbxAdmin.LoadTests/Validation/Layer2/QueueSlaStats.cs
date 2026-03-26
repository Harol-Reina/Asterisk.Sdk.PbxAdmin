namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed record QueueSlaStats
{
    public string QueueName { get; init; } = "";
    public int Offered { get; init; }
    public int Answered { get; init; }
    public int Abandoned { get; init; }
    public int WithinSla { get; init; }
    public double SlaPercent => Offered > 0 ? (double)WithinSla / Offered * 100 : 0;
    public double AvgWaitSecs { get; init; }
}
