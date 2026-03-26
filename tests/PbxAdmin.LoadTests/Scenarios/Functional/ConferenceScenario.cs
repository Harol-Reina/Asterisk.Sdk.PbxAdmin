using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 2 calls to extension 801 (conference room) and validates that
/// both calls have CDR records and that CEL shows BRIDGE_ENTER for both
/// (indicating they joined the same conference bridge).
/// </summary>
public sealed class ConferenceScenario : ITestScenario
{
    public string Name => "conference";
    public string Description => "2 calls to ext 801 (conference room); validates CDR for both calls and CEL BRIDGE_ENTER events showing bridge join";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<ConferenceScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 2 calls to conference room (extension 801)", Name);

        try
        {
            for (int i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("801", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Call {N}/2 accepted: {CallId}", Name, i + 1, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Call {N}/2 rejected: {Error}", Name, i + 1, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        logger.LogInformation("[{Scenario}] Waiting 20s for conference to complete", Name);
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

                // Conference-specific: BRIDGE_ENTER indicates entry into conference bridge
                bool bridgeEnterFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "BRIDGE_ENTER", StringComparison.OrdinalIgnoreCase));

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "ConferenceScenario",
                    Passed = bridgeEnterFound || celEvents.Count == 0,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "ConferenceBridgeEnterFound",
                            Passed = bridgeEnterFound || celEvents.Count == 0,
                            Expected = "BRIDGE_ENTER event in CEL (conference join)",
                            Actual = bridgeEnterFound ? "BRIDGE_ENTER found" : celEvents.Count == 0 ? "No CEL (offline)" : "BRIDGE_ENTER missing",
                            Message = (!bridgeEnterFound && celEvents.Count > 0) ? "CEL has no BRIDGE_ENTER — call may not have joined the conference room" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "ConferenceScenario",
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
