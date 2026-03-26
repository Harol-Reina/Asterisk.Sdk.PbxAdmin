namespace PbxAdmin.LoadTests.Validation;

public sealed record ValidationReport
{
    public DateTime TestStart { get; init; }
    public DateTime TestEnd { get; init; }
    public TimeSpan Duration { get; init; }
    public int TotalCalls { get; init; }
    public int TotalChecks { get; init; }
    public int PassedChecks { get; init; }
    public int FailedChecks { get; init; }
    public double PassRate => TotalChecks > 0 ? (double)PassedChecks / TotalChecks * 100 : 0;
    public List<ValidationResult> Results { get; init; } = [];
    public List<ValidationResult> Failures => Results.Where(r => !r.Passed).ToList();
    public List<string> SdkBugsFound { get; init; } = [];
}
