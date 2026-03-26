using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios;

public interface ITestScenario
{
    string Name { get; }
    string Description { get; }
    Task ExecuteAsync(TestContext context, CancellationToken ct);
    Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct);
}
