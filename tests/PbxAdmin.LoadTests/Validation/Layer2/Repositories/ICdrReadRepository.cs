namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public interface ICdrReadRepository
{
    Task<List<CdrRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<CdrRecord?> GetByUniqueIdAsync(string uniqueId, CancellationToken ct = default);
    Task<List<CdrRecord>> GetBySrcAsync(string src, DateTime after, CancellationToken ct = default);
    Task<List<CdrRecord>> GetByLinkedIdAsync(string linkedId, CancellationToken ct = default);
    Task<int> GetCountByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
