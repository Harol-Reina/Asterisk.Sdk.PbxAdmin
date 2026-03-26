using FluentAssertions;
using NSubstitute;
using PbxAdmin.LoadTests.Validation.Layer2;
using PbxAdmin.LoadTests.Validation.Layer2.Repositories;

namespace PbxAdmin.Tests.LoadTests;

public sealed class CdrReaderTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    // -------------------------------------------------------------------------
    // GetCallsForTest
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCallsForTest_ShouldFilterByTimeRange()
    {
        var repo = Substitute.For<ICdrReadRepository>();
        var expected = new List<CdrRecord>
        {
            new() { Id = 1, Src = "573101234567", CallDate = BaseTime.AddMinutes(5) },
            new() { Id = 2, Src = "573109876543", CallDate = BaseTime.AddMinutes(10) }
        };
        var from = BaseTime;
        var to = BaseTime.AddHours(1);
        repo.GetByTimeRangeAsync(from, to, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(expected));

        var reader = new CdrReader(repo);
        var result = await reader.GetCallsForTestAsync(from, to);

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expected);
        await repo.Received(1).GetByTimeRangeAsync(from, to, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // GetCallBySrc
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCallBySrc_ShouldReturnFirstMatch_WhenMultipleCdrs()
    {
        var repo = Substitute.For<ICdrReadRepository>();
        var first = new CdrRecord { Id = 10, Src = "573101234567", CallDate = BaseTime.AddMinutes(1) };
        var second = new CdrRecord { Id = 11, Src = "573101234567", CallDate = BaseTime.AddMinutes(3) };
        repo.GetBySrcAsync("573101234567", BaseTime, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CdrRecord> { first, second }));

        var reader = new CdrReader(repo);
        var result = await reader.GetCallBySrcAsync("573101234567", BaseTime);

        result.Should().NotBeNull();
        result!.Id.Should().Be(10);
    }

    [Fact]
    public async Task GetCallBySrc_ShouldReturnNull_WhenNoCdrs()
    {
        var repo = Substitute.For<ICdrReadRepository>();
        repo.GetBySrcAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CdrRecord>()));

        var reader = new CdrReader(repo);
        var result = await reader.GetCallBySrcAsync("573101234567", BaseTime);

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // GetTransferLegs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetTransferLegs_ShouldReturnMultipleRecords_WhenSameLinkedId()
    {
        var repo = Substitute.For<ICdrReadRepository>();
        const string linkedId = "1711447200.001";
        var legs = new List<CdrRecord>
        {
            new() { Id = 20, LinkedId = linkedId, Src = "573101234567" },
            new() { Id = 21, LinkedId = linkedId, Src = "1000" },
            new() { Id = 22, LinkedId = linkedId, Src = "1001" }
        };
        repo.GetByLinkedIdAsync(linkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(legs));

        var reader = new CdrReader(repo);
        var result = await reader.GetTransferLegsAsync(linkedId);

        result.Should().HaveCount(3);
        result.Should().AllSatisfy(r => r.LinkedId.Should().Be(linkedId));
    }

    [Fact]
    public async Task GetTransferLegs_ShouldReturnEmpty_WhenNoMatchingLinkedId()
    {
        var repo = Substitute.For<ICdrReadRepository>();
        repo.GetByLinkedIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CdrRecord>()));

        var reader = new CdrReader(repo);
        var result = await reader.GetTransferLegsAsync("nonexistent.001");

        result.Should().BeEmpty();
    }
}
