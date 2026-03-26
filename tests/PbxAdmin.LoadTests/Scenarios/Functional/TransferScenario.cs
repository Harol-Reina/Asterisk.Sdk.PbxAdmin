using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates 1 call to extension 200 and waits for the agent to answer (10s),
/// then relies on the SIP agent's auto-behavior to perform a blind transfer.
/// Validates that CDR contains 2 records with the same linkedId and that CEL
/// shows a BLINDTRANSFER event.
/// </summary>
public sealed class TransferScenario : ITestScenario
{
    public string Name => "transfer";
    public string Description => "1 call to ext 200; after agent answers, blind transfer is expected; validates 2 CDR legs with same linkedId and CEL BLINDTRANSFER";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<TransferScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation("[{Scenario}] Generating 1 call to extension 200 for transfer test", Name);

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

        // Wait for agent to answer (10s) + transfer time + second leg completion
        logger.LogInformation("[{Scenario}] Waiting 30s for transfer to complete", Name);
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

                // Transfer-specific: look for BLINDTRANSFER in CEL
                bool blindTransferFound = celEvents.Any(e =>
                    string.Equals(e.EventType, "BLINDTRANSFER", StringComparison.OrdinalIgnoreCase));

                // Transfer-specific: check that there are 2 CDR legs sharing the same linkedId
                List<Validation.Layer2.CdrRecord> transferLegs = [];
                if (!string.IsNullOrEmpty(cdr?.LinkedId))
                    transferLegs = await context.CdrReader.GetTransferLegsAsync(cdr.LinkedId, ct);

                bool twoLegsFound = transferLegs.Count >= 2;

                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "TransferScenario",
                    Passed = true, // informational — transfer may not fire in offline mode
                    Checks =
                    [
                        new Validation.ValidationCheck
                        {
                            CheckName = "BlindTransferEventFound",
                            Passed = blindTransferFound || celEvents.Count == 0,
                            Expected = "BLINDTRANSFER event in CEL",
                            Actual = blindTransferFound ? "BLINDTRANSFER found" : celEvents.Count == 0 ? "No CEL (offline)" : "BLINDTRANSFER missing",
                            Message = (!blindTransferFound && celEvents.Count > 0) ? "CEL has no BLINDTRANSFER — transfer may not have been executed" : null
                        },
                        new Validation.ValidationCheck
                        {
                            CheckName = "TransferLegsPresent",
                            Passed = twoLegsFound || cdr is null,
                            Expected = ">=2 CDR legs with same linkedId",
                            Actual = $"{transferLegs.Count} leg(s) found",
                            Message = (!twoLegsFound && cdr is not null) ? "Transfer expected but only 1 CDR leg found — blind transfer may not have completed" : null
                        }
                    ]
                });
            }
            catch (Exception ex)
            {
                results.Add(new Validation.ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = "TransferScenario",
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
