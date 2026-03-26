using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;

namespace PbxAdmin.LoadTests.Validation.Layer3;

/// <summary>
/// Compares SDK captured events against CEL records to detect event sequencing
/// discrepancies that indicate SDK bugs (missed events, out-of-order delivery, etc.).
/// </summary>
public static class EventSequenceValidator
{
    // SDK may see more or fewer events than CEL due to AMI vs CEL differences
    private const double EventCountTolerancePercent = 0.20;

    public static ValidationResult ValidateEventSequence(SdkSnapshot sdk, List<CelRecord> celRecords)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: CEL records must exist for this call
        bool celRecordsExist = celRecords.Count > 0;
        checks.Add(new ValidationCheck
        {
            CheckName = "CelRecordsExist",
            Passed = celRecordsExist,
            Expected = "At least 1 CEL record",
            Actual = $"{celRecords.Count} CEL record(s)",
            Message = celRecordsExist ? null : $"No CEL records found for call {sdk.CallId}"
        });

        if (celRecords.Count > 0)
        {
            // Check 2: Event counts roughly match (within ±20%)
            int sdkCount = sdk.EventCount;
            int celCount = celRecords.Count;
            bool eventCountMatch = true;
            string? eventCountMessage = null;

            if (sdkCount > 0 && celCount > 0)
            {
                double ratio = (double)Math.Abs(sdkCount - celCount) / celCount;
                eventCountMatch = ratio <= EventCountTolerancePercent;
                if (!eventCountMatch)
                    eventCountMessage = $"SDK event count {sdkCount} differs from CEL count {celCount} by {ratio:P0} (tolerance {EventCountTolerancePercent:P0})";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "EventCountMatch",
                Passed = eventCountMatch,
                Expected = $"{celCount} (±{EventCountTolerancePercent:P0})",
                Actual = sdkCount.ToString(),
                Message = eventCountMessage
            });

            // Check 3: CEL must have CHAN_START
            bool chanStartPresent = celRecords.Any(r =>
                string.Equals(r.EventType, "CHAN_START", StringComparison.OrdinalIgnoreCase));
            checks.Add(new ValidationCheck
            {
                CheckName = "ChanStartPresent",
                Passed = chanStartPresent,
                Expected = "CHAN_START event in CEL",
                Actual = chanStartPresent ? "CHAN_START found" : "CHAN_START missing",
                Message = chanStartPresent ? null : "CEL records lack a CHAN_START event — call may not have been tracked"
            });

            // Check 4: CEL must have HANGUP
            bool hangupPresent = celRecords.Any(r =>
                string.Equals(r.EventType, "HANGUP", StringComparison.OrdinalIgnoreCase));
            checks.Add(new ValidationCheck
            {
                CheckName = "HangupPresent",
                Passed = hangupPresent,
                Expected = "HANGUP event in CEL",
                Actual = hangupPresent ? "HANGUP found" : "HANGUP missing",
                Message = hangupPresent ? null : "CEL records lack a HANGUP event — call may not have terminated cleanly"
            });

            // Check 5: BRIDGE_ENTER and BRIDGE_EXIT counts must balance
            int bridgeEnterCount = celRecords.Count(r =>
                string.Equals(r.EventType, "BRIDGE_ENTER", StringComparison.OrdinalIgnoreCase));
            int bridgeExitCount = celRecords.Count(r =>
                string.Equals(r.EventType, "BRIDGE_EXIT", StringComparison.OrdinalIgnoreCase));
            bool bridgeConsistency = bridgeEnterCount == bridgeExitCount;
            checks.Add(new ValidationCheck
            {
                CheckName = "BridgeConsistency",
                Passed = bridgeConsistency,
                Expected = $"BRIDGE_ENTER ({bridgeEnterCount}) == BRIDGE_EXIT",
                Actual = bridgeExitCount.ToString(),
                Message = bridgeConsistency ? null : $"Orphaned bridge: {bridgeEnterCount} BRIDGE_ENTER vs {bridgeExitCount} BRIDGE_EXIT"
            });

            // Check 6: CEL events must be chronologically ordered
            bool eventOrder = true;
            string? orderMessage = null;
            for (int i = 1; i < celRecords.Count; i++)
            {
                if (celRecords[i].EventTime < celRecords[i - 1].EventTime)
                {
                    eventOrder = false;
                    orderMessage = $"CEL event at index {i} ({celRecords[i].EventType} @ {celRecords[i].EventTime:HH:mm:ss.fff}) precedes event at index {i - 1} ({celRecords[i - 1].EventType} @ {celRecords[i - 1].EventTime:HH:mm:ss.fff})";
                    break;
                }
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "EventOrder",
                Passed = eventOrder,
                Expected = "Events chronologically ordered",
                Actual = eventOrder ? "Ordered" : "Out of order",
                Message = orderMessage
            });

            // Check 7: If CEL has ANSWER, SDK must have AnswerTime set
            bool celHasAnswer = celRecords.Any(r =>
                string.Equals(r.EventType, "ANSWER", StringComparison.OrdinalIgnoreCase));
            bool sdkSawAnswer = !celHasAnswer || sdk.AnswerTime.HasValue;
            checks.Add(new ValidationCheck
            {
                CheckName = "SdkSawAnswer",
                Passed = sdkSawAnswer,
                Expected = celHasAnswer ? "AnswerTime set (CEL has ANSWER)" : "N/A (no ANSWER in CEL)",
                Actual = sdk.AnswerTime.HasValue ? "AnswerTime set" : "AnswerTime null",
                Message = sdkSawAnswer ? null : "CEL shows ANSWER event but SDK snapshot has no AnswerTime — SDK missed the answer"
            });
        }

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = sdk.CallId,
            ValidatorName = nameof(EventSequenceValidator),
            Passed = allPassed,
            Checks = checks
        };
    }
}
