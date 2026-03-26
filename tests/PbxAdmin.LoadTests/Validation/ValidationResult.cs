namespace PbxAdmin.LoadTests.Validation;

public sealed record ValidationResult
{
    public required string CallId { get; init; }
    public required string ValidatorName { get; init; }
    public required bool Passed { get; init; }
    public List<ValidationCheck> Checks { get; init; } = [];
}

public sealed record ValidationCheck
{
    public required string CheckName { get; init; }
    public required bool Passed { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Message { get; init; }
}
