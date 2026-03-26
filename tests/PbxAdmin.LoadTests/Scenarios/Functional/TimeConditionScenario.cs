using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200, which passes through time condition routing
/// before reaching the IVR or an alternative destination. Validates that a CDR
/// exists and CEL shows the call path through the time condition context.
/// </summary>
public sealed class TimeConditionScenario : ITestScenario
{
    public string Name => "time-condition";
    public string Description => "1 call to ext 200 routed through time condition; validates CDR exists and CEL shows time condition context in call path";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<TimeConditionScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to extension 200 (time condition routing)", Name);

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

        logger.LogInformation("[{Scenario}] Waiting 20s for time-conditioned call to complete", Name);
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

                // Time condition-specific: CEL should reference a context containing "tc" or "time"
                bool timeConditionContextFound = celEvents.Any(e =>
                    e.Context.Contains("tc", StringComparison.OrdinalIgnoreCase)
                    || e.Context.Contains("time", StringComparison.OrdinalIgnoreCase)
                    || e.AppName.Contains("GotoIfTime", StringComparison.OrdinalIgnoreCase));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "TimeConditionScenario",
                    Passed = timeConditionContextFound || celEvents.Count == 0,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "TimeConditionContextFound",
                            Passed = timeConditionContextFound || celEvents.Count == 0,
                            Expected = "CEL context containing 'tc' or 'time', or GotoIfTime app",
                            Actual = timeConditionContextFound
                                ? "Time condition context found in CEL"
                                : celEvents.Count == 0 ? "No CEL (offline)" : "Time condition context not found",
                            Message = (!timeConditionContextFound && celEvents.Count > 0)
                                ? "CEL has no time condition context — call may not have been routed through a time condition"
                                : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "TimeConditionScenario",
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
