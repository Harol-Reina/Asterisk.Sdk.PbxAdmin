namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed record CdrRecord
{
    public long Id { get; init; }
    public DateTime CallDate { get; init; }
    public string Clid { get; init; } = "";
    public string Src { get; init; } = "";
    public string Dst { get; init; } = "";
    public string DContext { get; init; } = "";
    public string Channel { get; init; } = "";
    public string DstChannel { get; init; } = "";
    public string LastApp { get; init; } = "";
    public string LastData { get; init; } = "";
    public int Duration { get; init; }
    public int BillSec { get; init; }
    public string Disposition { get; init; } = "";
    public string UniqueId { get; init; } = "";
    public string LinkedId { get; init; } = "";
}
