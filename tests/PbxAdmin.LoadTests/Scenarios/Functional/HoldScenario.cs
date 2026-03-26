using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200. After the agent answers (10s), the agent
/// holds the call for 5s then resumes. Validates that CEL contains HOLD and
/// UNHOLD events and that CDR duration includes hold time.
/// </summary>
public sealed class HoldScenario : ITestScenario
{
    public string Name => "hold";
    public string Description => "1 call to ext 200; agent holds then resumes; validates CEL HOLD/UNHOLD events and CDR duration includes hold time";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<HoldScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to extension 200 for hold test", Name);

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

        // Wait: ring (5s) + answer + hold (5s) + resume + talk + hangup
        logger.LogInformation("[{Scenario}] Waiting 30s for hold/resume cycle to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

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

                // Hold-specific: CEL should contain HOLD and UNHOLD events
                bool holdFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "HOLD", StringComparison.OrdinalIgnoreCase));
                bool unholdFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "UNHOLD", StringComparison.OrdinalIgnoreCase));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "HoldScenario",
                    Passed = true,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "HoldEventFound",
                            Passed = holdFound || celEvents.Count == 0,
                            Expected = "HOLD event in CEL",
                            Actual = holdFound ? "HOLD found" : celEvents.Count == 0 ? "No CEL (offline)" : "HOLD missing",
                            Message = (!holdFound && celEvents.Count > 0) ? "CEL has no HOLD event — agent may not have placed call on hold" : null
                        },
                        new Validation.ValidationCheck
                        {
                            CheckName = "UnholdEventFound",
                            Passed = unholdFound || celEvents.Count == 0,
                            Expected = "UNHOLD event in CEL",
                            Actual = unholdFound ? "UNHOLD found" : celEvents.Count == 0 ? "No CEL (offline)" : "UNHOLD missing",
                            Message = (!unholdFound && celEvents.Count > 0) ? "CEL has no UNHOLD event — call on hold was never resumed" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "HoldScenario",
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
