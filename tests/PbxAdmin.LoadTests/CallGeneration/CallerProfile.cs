namespace PbxAdmin.LoadTests.CallGeneration;

public sealed record CallerProfile
{
    public required string Number { get; init; }
    public required string DisplayName { get; init; }
    public required string Operator { get; init; }
    public required CallerType Type { get; init; }
}

public enum CallerType { Mobile, Landline }
