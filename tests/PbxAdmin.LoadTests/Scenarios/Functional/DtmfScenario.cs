using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 1006 (PSTN DTMF test scenario).
/// Validates that the CDR shows ANSWERED and that CEL traces the call path
/// through the PSTN emulator.
/// </summary>
public sealed class DtmfScenario : ITestScenario
{
    public string Name => "dtmf";
    public string Description => "1 call to ext 1006 (PSTN DTMF test scenario); validates CDR=ANSWERED and CEL call path through PSTN emulator";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<DtmfScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to PSTN DTMF test extension 1006", Name);

        try
        {
            var result = await context.CallGenerator.GenerateCallAsync("1006", cancellationToken: ct);
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

        logger.LogInformation("[{Scenario}] Waiting 15s for DTMF test call to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

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

                // DTMF-specific: validate CDR disposition is ANSWERED
                bool answeredDisposition = cdr is null
                    || string.Equals(cdr.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase);

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "DtmfScenario",
                    Passed = answeredDisposition,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "DtmfCallAnswered",
                            Passed = answeredDisposition,
                            Expected = "ANSWERED",
                            Actual = cdr?.Disposition ?? "(no CDR)",
                            Message = (!answeredDisposition) ? $"DTMF test call not answered — CDR disposition is '{cdr!.Disposition}'" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "DtmfScenario",
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
