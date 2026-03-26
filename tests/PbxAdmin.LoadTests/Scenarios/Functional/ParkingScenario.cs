using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200. After the agent answers, the agent parks
/// the call. Validates that CEL contains a PARK_START event and a CDR record exists.
/// </summary>
public sealed class ParkingScenario : ITestScenario
{
    public string Name => "parking";
    public string Description => "1 call to ext 200; agent answers then parks the call; validates CEL PARK_START event and CDR existence";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<ParkingScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to extension 200 for parking test", Name);

        try
        {
            var result = await context.CallGenerator.GenerateCallAsync("200", cancellationToken: ct);
            context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
            context.Metrics.RecordCallOriginated();

            if (result.Accepted)
                logger.LogDebug("[{Scenario}] Call accepted: {CallId}", Name, result.CallId);
            else
                logger.LogWarning("[{Scenario}] Call rejected: {Error}", Name, result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        // Wait for ring + answer + park action + parked call timeout
        logger.LogInformation("[{Scenario}] Waiting 20s for parking sequence to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var results = new List<Validation.ValidationResult>();

        foreach (var snapshot in context.EventCapture.GetAllSnapshots())
        {
            try
            {
                var cdr = await context.CdrReader.GetCallBySrcAsync(snapshot.CallerNumber, context.TestStartTime, ct);
                var celEvents = snapshot.LinkedId is not null
                    ? await context.CelReader.GetEventSequenceAsync(snapshot.LinkedId, ct)
                    : [];

                results.Add(SessionValidator.ValidateCall(snapshot, cdr));
                results.Add(EventSequenceValidator.ValidateEventSequence(snapshot, celEvents));

                // Parking-specific: CEL should have PARK_START
                bool parkStartFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "PARK_START", StringComparison.OrdinalIgnoreCase));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "ParkingScenario",
                    Passed = parkStartFound || celEvents.Count == 0,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "ParkStartEventFound",
                            Passed = parkStartFound || celEvents.Count == 0,
                            Expected = "PARK_START event in CEL",
                            Actual = parkStartFound ? "PARK_START found" : celEvents.Count == 0 ? "No CEL (offline)" : "PARK_START missing",
                            Message = (!parkStartFound && celEvents.Count > 0) ? "CEL has no PARK_START — call may not have been parked" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "ParkingScenario",
                    Passed = false,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "ValidationException",
                            Passed = false,
                            Message = ex.Message
                        }
                    ]
                });
            }
        }

        return new ValidationReport
        {
            TestStart = context.TestStartTime,
            TestEnd = context.TestEndTime,
            Duration = context.TestEndTime - context.TestStartTime,
            TotalCalls = results.Count(r => r.ValidatorName == nameof(SessionValidator)),
            TotalChecks = results.SelectMany(r => r.Checks).Count(),
            PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
            FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
            Results = results
        };
    }
}
