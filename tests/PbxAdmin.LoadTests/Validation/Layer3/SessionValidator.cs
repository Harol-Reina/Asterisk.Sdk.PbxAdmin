using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation.Layer1;
using PbxAdmin.LoadTests.Validation.Layer2;

namespace PbxAdmin.LoadTests.Validation.Layer3;

/// <summary>
/// Compares an SDK snapshot against a CDR record for a single call to detect
/// discrepancies that indicate SDK bugs (missed events, wrong disposition, etc.).
/// </summary>
public static class SessionValidator
{
    private const int DurationToleranceSecs = 2;

    public static ValidationResult ValidateCall(SdkSnapshot sdk, CdrRecord? cdr)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: CDR must exist — SDK saw a call so a CDR record must be written
        bool cdrExists = cdr is not null;
        checks.Add(new ValidationCheck
        {
            CheckName = "CdrExists",
            Passed = cdrExists,
            Expected = "CDR record present",
            Actual = cdrExists ? "CDR record present" : "CDR record missing",
            Message = cdrExists ? null : $"SDK observed call {sdk.CallId} but no CDR was written to the database"
        });

        if (cdr is not null)
        {
            // Check 2: Disposition must match
            bool dispositionMatch = sdk.Disposition is null
                || string.IsNullOrEmpty(cdr.Disposition)
                || string.Equals(sdk.Disposition, cdr.Disposition, StringComparison.OrdinalIgnoreCase);

            checks.Add(new ValidationCheck
            {
                CheckName = "DispositionMatch",
                Passed = dispositionMatch,
                Expected = sdk.Disposition,
                Actual = cdr.Disposition,
                Message = dispositionMatch ? null : $"SDK reported '{sdk.Disposition}' but CDR shows '{cdr.Disposition}'"
            });

            // Check 3: Duration within tolerance (2s)
            bool durationMatch = true;
            string? durationMessage = null;
            if (sdk.DurationSecs.HasValue)
            {
                int diff = Math.Abs(sdk.DurationSecs.Value - cdr.BillSec);
                durationMatch = diff <= DurationToleranceSecs;
                if (!durationMatch)
                    durationMessage = $"SDK duration {sdk.DurationSecs}s differs from CDR billsec {cdr.BillSec}s by {diff}s (tolerance {DurationToleranceSecs}s)";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "DurationMatch",
                Passed = durationMatch,
                Expected = sdk.DurationSecs?.ToString() ?? "(not set)",
                Actual = cdr.BillSec.ToString(),
                Message = durationMessage
            });

            // Check 4: Caller number must match CDR source
            bool callerMatch = string.Equals(sdk.CallerNumber, cdr.Src, StringComparison.Ordinal);
            checks.Add(new ValidationCheck
            {
                CheckName = "CallerMatch",
                Passed = callerMatch,
                Expected = sdk.CallerNumber,
                Actual = cdr.Src,
                Message = callerMatch ? null : $"SDK caller '{sdk.CallerNumber}' does not match CDR src '{cdr.Src}'"
            });

            // Check 5: Destination must match CDR destination
            bool destinationMatch = string.Equals(sdk.Destination, cdr.Dst, StringComparison.Ordinal);
            checks.Add(new ValidationCheck
            {
                CheckName = "DestinationMatch",
                Passed = destinationMatch,
                Expected = sdk.Destination,
                Actual = cdr.Dst,
                Message = destinationMatch ? null : $"SDK destination '{sdk.Destination}' does not match CDR dst '{cdr.Dst}'"
            });

            // Check 6: UniqueId must match (if both are set)
            bool uniqueIdMatch = true;
            string? uniqueIdMessage = null;
            if (!string.IsNullOrEmpty(sdk.UniqueId) && !string.IsNullOrEmpty(cdr.UniqueId))
            {
                uniqueIdMatch = string.Equals(sdk.UniqueId, cdr.UniqueId, StringComparison.Ordinal);
                if (!uniqueIdMatch)
                    uniqueIdMessage = $"SDK uniqueid '{sdk.UniqueId}' does not match CDR uniqueid '{cdr.UniqueId}'";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "UniqueIdMatch",
                Passed = uniqueIdMatch,
                Expected = sdk.UniqueId ?? "(not set)",
                Actual = cdr.UniqueId,
                Message = uniqueIdMessage
            });
        }

        // Check 7: SDK must have detected the hangup
        bool sdkDetectedHangup = sdk.EndTime.HasValue;
        checks.Add(new ValidationCheck
        {
            CheckName = "SdkDetectedHangup",
            Passed = sdkDetectedHangup,
            Expected = "EndTime set",
            Actual = sdkDetectedHangup ? "EndTime set" : "EndTime null",
            Message = sdkDetectedHangup ? null : "SDK did not receive the Hangup event for this call"
        });

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = sdk.CallId,
            ValidatorName = nameof(SessionValidator),
            Passed = allPassed,
            Checks = checks
        };
    }

    /// <summary>
    /// Compares a CallSessionSnapshot (SDK-processed session) against a CDR record
    /// to detect discrepancies between the SDK session tracker and the database.
    /// </summary>
    public static ValidationResult ValidateSession(CallSessionSnapshot session, CdrRecord? cdr)
    {
        var checks = new List<ValidationCheck>();

        // Check 1: CDR must exist
        bool cdrExists = cdr is not null;
        checks.Add(new ValidationCheck
        {
            CheckName = "CdrExists",
            Passed = cdrExists,
            Expected = "CDR record present",
            Actual = cdrExists ? "CDR record present" : "CDR record missing",
            Message = cdrExists ? null : $"SDK session {session.SessionId} completed but no CDR was written to the database"
        });

        if (cdr is not null)
        {
            // Check 8: State must be consistent with CDR disposition
            bool stateMatch = IsStateDispositionConsistent(session.FinalState, cdr.Disposition);
            checks.Add(new ValidationCheck
            {
                CheckName = "StateMatchesDisposition",
                Passed = stateMatch,
                Expected = session.FinalState,
                Actual = cdr.Disposition,
                Message = stateMatch ? null : $"SDK state '{session.FinalState}' is not consistent with CDR disposition '{cdr.Disposition}'"
            });

            // Check 9: Duration within tolerance (2s)
            bool durationMatch = true;
            string? durationMessage = null;
            if (session.Duration.HasValue)
            {
                int sessionDurationSecs = (int)session.Duration.Value.TotalSeconds;
                int diff = Math.Abs(sessionDurationSecs - cdr.BillSec);
                durationMatch = diff <= DurationToleranceSecs;
                if (!durationMatch)
                    durationMessage = $"SDK duration {sessionDurationSecs}s differs from CDR billsec {cdr.BillSec}s by {diff}s (tolerance {DurationToleranceSecs}s)";
            }

            checks.Add(new ValidationCheck
            {
                CheckName = "DurationMatch",
                Passed = durationMatch,
                Expected = session.Duration.HasValue ? ((int)session.Duration.Value.TotalSeconds).ToString() : "(not set)",
                Actual = cdr.BillSec.ToString(),
                Message = durationMessage
            });

            // Check 10: Caller number must match CDR source
            bool callerMatch = string.Equals(session.CallerNumber, cdr.Src, StringComparison.Ordinal);
            checks.Add(new ValidationCheck
            {
                CheckName = "CallerMatch",
                Passed = callerMatch,
                Expected = session.CallerNumber,
                Actual = cdr.Src,
                Message = callerMatch ? null : $"SDK caller '{session.CallerNumber}' does not match CDR src '{cdr.Src}'"
            });
        }

        bool allPassed = checks.All(c => c.Passed);

        return new ValidationResult
        {
            CallId = session.SessionId,
            ValidatorName = nameof(SessionValidator),
            Passed = allPassed,
            Checks = checks
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsStateDispositionConsistent(string? state, string? disposition)
    {
        if (state is null || string.IsNullOrEmpty(disposition))
            return true;

        // CDR ANSWERED can occur with SDK Failed/TimedOut when Queue Answer()s the
        // channel before distributing — no agent connects but CDR still shows ANSWERED.
        return disposition.ToUpperInvariant() switch
        {
            "ANSWERED" => state is "Completed" or "Connected" or "Failed" or "TimedOut",
            "NO ANSWER" => state is "TimedOut" or "Failed",
            "BUSY" => state is "Failed",
            "FAILED" => state is "Failed",
            _ => true
        };
    }
}
