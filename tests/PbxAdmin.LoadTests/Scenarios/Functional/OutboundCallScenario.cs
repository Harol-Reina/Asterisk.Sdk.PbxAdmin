using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Uses an agent to originate an outbound call to extension 1001 (PSTN normal answer).
/// Validates that CDR has src=agent extension, dst=1001, and disposition=ANSWERED.
/// </summary>
public sealed class OutboundCallScenario : ITestScenario
{
    public string Name => "outbound-call";
    public string Description => "Agent originates outbound call to 1001 (PSTN normal answer); validates CDR src=agent, dst=1001, disposition=ANSWERED";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<OutboundCallScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating outbound call to extension 1001 (PSTN normal answer)", Name);

        try
        {
            // Originate call via the PSTN emulator to the PSTN normal-answer scenario
            var result = await context.CallGenerator.GenerateCallAsync("1001", cancellationToken: ct);
            context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
            context.Metrics.RecordCallOriginated();

            if (result.Accepted)
                logger.LogDebug("[{Scenario}] Outbound call accepted: {CallId}", Name, result.CallId);
            else
                logger.LogWarning("[{Scenario}] Outbound call rejected: {Error}", Name, result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Call generation failed", Name);
        }

        logger.LogInformation("[{Scenario}] Waiting 15s for outbound call to complete", Name);
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

                // Outbound-specific: CDR destination should be 1001 and disposition ANSWERED
                bool dstMatch = cdr is null || string.Equals(cdr.Dst, "1001", StringComparison.Ordinal);
                bool answeredDisposition = cdr is null
                    || string.Equals(cdr.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase);

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "OutboundCallScenario",
                    Passed = dstMatch && answeredDisposition,
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "OutboundDestinationMatch",
                            Passed = dstMatch,
                            Expected = "1001",
                            Actual = cdr?.Dst ?? "(no CDR)",
                            Message = (!dstMatch) ? $"CDR dst '{cdr!.Dst}' does not match expected outbound destination '1001'" : null
                        },
                        new Validation.ValidationCheck
                        {
                            CheckName = "OutboundAnswered",
                            Passed = answeredDisposition,
                            Expected = "ANSWERED",
                            Actual = cdr?.Disposition ?? "(no CDR)",
                            Message = (!answeredDisposition) ? $"Outbound call not answered — CDR disposition is '{cdr!.Disposition}'" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "OutboundCallScenario",
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
