using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.LoadTests.Validation.Layer2;

public sealed class CdrReader
{
    private readonly ICdrReadRepository _repository;

    public CdrReader(ICdrReadRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Returns all CDR records within the test time window.
    /// </summary>
    public Task<List<CdrRecord>> GetCallsForTestAsync(
        DateTime testStart, DateTime testEnd, CancellationToken ct = default)
        => _repository.GetByTimeRangeAsync(testStart, testEnd, ct);

    /// <summary>
    /// Returns the first CDR matching the given source number after the specified time.
    /// </summary>
    public async Task<CdrRecord?> GetCallBySrcAsync(
        string callerNumber, DateTime after, CancellationToken ct = default)
    {
        var records = await _repository.GetBySrcAsync(callerNumber, after, ct);
        return records.Count > 0 ? records[0] : null;
    }

    /// <summary>
    /// Returns all CDR legs that share the same linkedId (e.g. transfer scenarios).
    /// </summary>
    public Task<List<CdrRecord>> GetTransferLegsAsync(
        string linkedId, CancellationToken ct = default)
        => _repository.GetByLinkedIdAsync(linkedId, ct);
}
