namespace PbxAdmin.LoadTests.Validation.Layer2.Repositories;

public interface ICelReadRepository
{
    Task<List<CelRecord>> GetByLinkedIdAsync(string linkedId, CancellationToken ct = default);
    Task<List<CelRecord>> GetByUniqueIdAsync(string uniqueId, CancellationToken ct = default);
    Task<List<CelRecord>> GetByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<int> GetCountByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
