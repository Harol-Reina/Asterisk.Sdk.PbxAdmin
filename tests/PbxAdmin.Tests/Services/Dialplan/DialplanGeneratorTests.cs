using PbxAdmin.Models;
using PbxAdmin.Services.Dialplan;
using FluentAssertions;

namespace PbxAdmin.Tests.Services.Dialplan;

public class DialplanGeneratorTests
{
    // --- Inbound ---

    [Fact]
    public void Generate_InboundRoute_Extension_ShouldCreateGoto()
    {
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551000", DestinationType = "extension", Destination = "1001", Priority = 1, Enabled = true }],
            [], []);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().ContainSingle(l => l.Context == "from-trunk" && l.Exten == "5551000")
            .Which.Should().Match<DialplanLine>(l => l.App == "Goto" && l.AppData == "default,1001,1");
    }

    [Fact]
    public void Generate_InboundRoute_Queue_ShouldCreateGoto()
    {
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551001", DestinationType = "queue", Destination = "sales", Priority = 1, Enabled = true }],
            [], []);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().ContainSingle(l => l.Context == "from-trunk" && l.Exten == "5551001")
            .Which.Should().Match<DialplanLine>(l => l.AppData == "queues,sales,1");
    }

    [Fact]
    public void Generate_InboundRoute_TimeCondition_ShouldCreateGoto()
    {
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551000", DestinationType = "time_condition", Destination = "horario-oficina", Priority = 1, Enabled = true }],
            [], []);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().ContainSingle(l => l.Context == "from-trunk" && l.Exten == "5551000")
            .Which.Should().Match<DialplanLine>(l => l.AppData == "tc-horario-oficina,s,1");
    }

    [Fact]
    public void Generate_InboundRoute_Disabled_ShouldSkip()
    {
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551000", DestinationType = "extension", Destination = "1001", Enabled = false }],
            [], []);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().NotContain(l => l.Context == "from-trunk");
    }

    [Fact]
    public void Generate_InboundRoutes_ShouldOrderByPriority()
    {
        var data = new DialplanData(
            [
                new InboundRouteConfig { DidPattern = "5551002", DestinationType = "extension", Destination = "1002", Priority = 2, Enabled = true },
                new InboundRouteConfig { DidPattern = "5551001", DestinationType = "extension", Destination = "1001", Priority = 1, Enabled = true },
            ], [], []);
        var fromTrunk = DialplanGenerator.Generate(data).Where(l => l.Context == "from-trunk").ToList();
        fromTrunk[0].Exten.Should().Be("5551001");
        fromTrunk[1].Exten.Should().Be("5551002");
    }

    // --- Outbound ---

    [Fact]
    public void Generate_OutboundRoute_SingleTrunk_ShouldCreateDial()
    {
        var data = new DialplanData([], [
            new OutboundRouteConfig
            {
                DialPattern = "_NXXNXXXXXX", Priority = 1, Enabled = true,
                Trunks = [new RouteTrunk { TrunkName = "trunk-primary", TrunkTechnology = "PjSip", Sequence = 1 }]
            }
        ], []);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "outbound-routes").ToList();
        lines.Should().Contain(l => l.App == "Dial" && l.AppData.Contains("trunk-primary"));
    }

    [Fact]
    public void Generate_OutboundRoute_MultipleTrunks_ShouldChainFailover()
    {
        var data = new DialplanData([], [
            new OutboundRouteConfig
            {
                DialPattern = "_NXXNXXXXXX", Priority = 1, Enabled = true,
                Trunks = [
                    new RouteTrunk { TrunkName = "trunk-primary", TrunkTechnology = "PjSip", Sequence = 1 },
                    new RouteTrunk { TrunkName = "trunk-backup", TrunkTechnology = "PjSip", Sequence = 2 },
                ]
            }
        ], []);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "outbound-routes").ToList();
        lines.Should().Contain(l => l.App == "ExecIf" && l.AppData.Contains("trunk-backup"));
    }

    [Fact]
    public void Generate_OutboundRoute_WithPrepend_ShouldSetOutnum()
    {
        var data = new DialplanData([], [
            new OutboundRouteConfig
            {
                DialPattern = "_00X.", Prepend = "011", Prefix = "00", Priority = 1, Enabled = true,
                Trunks = [new RouteTrunk { TrunkName = "trunk-intl", TrunkTechnology = "PjSip", Sequence = 1 }]
            }
        ], []);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "outbound-routes").ToList();
        lines.Should().Contain(l => l.App == "Set" && l.AppData.Contains("OUTNUM=011${EXTEN:2}"));
    }

    // --- Time Conditions ---

    [Fact]
    public void Generate_TimeCondition_ShouldCreateOverrideCheck()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "extension", NoMatchDest = "1099", Enabled = true,
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }],
        };
        var data = new DialplanData([], [], [tc]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "tc-horario").ToList();
        lines[0].App.Should().Be("Set");
        lines[0].AppData.Should().Contain("DB(TC_OVERRIDE/horario)");
        lines[1].App.Should().Be("GotoIf");
        lines[1].AppData.Should().Contain("OPEN");
    }

    [Fact]
    public void Generate_TimeCondition_ShouldCreateHolidayChecks()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "extension", NoMatchDest = "1099", Enabled = true,
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }],
            Holidays = [new HolidayEntry { Name = "Xmas", Month = 12, Day = 25 }],
        };
        var data = new DialplanData([], [], [tc]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "tc-horario").ToList();
        lines.Should().Contain(l => l.App == "GotoIfTime" && l.AppData.Contains(",25,dec"));
    }

    [Fact]
    public void Generate_TimeCondition_ShouldCreateTimeRanges()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "extension", NoMatchDest = "1099", Enabled = true,
            Ranges = [
                new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) },
                new TimeRangeEntry { DayOfWeek = DayOfWeek.Saturday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(13, 0) },
            ],
        };
        var data = new DialplanData([], [], [tc]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "tc-horario" && l.App == "GotoIfTime").ToList();
        lines.Should().Contain(l => l.AppData.Contains("09:00-18:00,mon"));
        lines.Should().Contain(l => l.AppData.Contains("09:00-13:00,sat"));
    }

    [Fact]
    public void Generate_TimeCondition_ShouldDefaultToClosed()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "extension", NoMatchDest = "1099", Enabled = true,
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }],
        };
        var data = new DialplanData([], [], [tc]);
        var tcLines = DialplanGenerator.Generate(data).Where(l => l.Context == "tc-horario").ToList();
        tcLines.Last().App.Should().Be("Goto");
        tcLines.Last().AppData.Should().Be("tc-horario-closed,s,1");
    }

    [Fact]
    public void Generate_TimeCondition_ShouldCreateOpenAndClosedContexts()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "extension", NoMatchDest = "1099", Enabled = true,
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }],
        };
        var data = new DialplanData([], [], [tc]);
        var lines = DialplanGenerator.Generate(data);
        lines.Should().ContainSingle(l => l.Context == "tc-horario-open")
            .Which.AppData.Should().Be("queues,sales,1");
        lines.Should().ContainSingle(l => l.Context == "tc-horario-closed")
            .Which.AppData.Should().Be("default,1099,1");
    }

    // --- Queue Context ---

    [Fact]
    public void Generate_ShouldCreateQueuesContext_WhenInboundRouteTargetsQueue()
    {
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551000", DestinationType = "queue", Destination = "sales", Priority = 1, Enabled = true }],
            [], []);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "queues" && l.Exten == "sales").ToList();
        lines.Should().HaveCount(3);
        lines.Should().Contain(l => l.Priority == 1 && l.App == "Answer");
        lines.Should().Contain(l => l.Priority == 2 && l.App == "Queue" && l.AppData == "sales,,,,300");
        lines.Should().Contain(l => l.Priority == 3 && l.App == "Hangup");
    }

    [Fact]
    public void Generate_ShouldCreateQueuesContext_WhenIvrMenuItemTargetsQueue()
    {
        var menu = new IvrMenuConfig
        {
            Id = 1, ServerId = "srv1", Name = "main", Label = "Main Menu",
            Greeting = "welcome", Timeout = 5, MaxRetries = 3, Enabled = true,
            Items = [new IvrMenuItemConfig { Digit = "1", DestType = "queue", DestTarget = "support" }]
        };
        var data = new DialplanData([], [], [], [menu]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "queues" && l.Exten == "support").ToList();
        lines.Should().HaveCount(3);
        lines.Should().Contain(l => l.Priority == 1 && l.App == "Answer");
        lines.Should().Contain(l => l.Priority == 2 && l.App == "Queue" && l.AppData == "support,,,,300");
        lines.Should().Contain(l => l.Priority == 3 && l.App == "Hangup");
    }

    [Fact]
    public void Generate_ShouldCreateQueuesContext_WhenTimeConditionTargetsQueue()
    {
        var tc = new TimeConditionConfig
        {
            Name = "horario", MatchDestType = "queue", MatchDest = "sales",
            NoMatchDestType = "queue", NoMatchDest = "after-hours", Enabled = true,
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }],
        };
        var data = new DialplanData([], [], [tc]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "queues").ToList();
        lines.Select(l => l.Exten).Distinct().Should().BeEquivalentTo("after-hours", "sales");
        lines.Where(l => l.Exten == "sales").Should().HaveCount(3);
        lines.Where(l => l.Exten == "after-hours").Should().HaveCount(3);
    }

    [Fact]
    public void Generate_ShouldNotDuplicateQueues_WhenMultipleSourcesTargetSameQueue()
    {
        var menu = new IvrMenuConfig
        {
            Id = 1, ServerId = "srv1", Name = "main", Label = "Main Menu",
            Greeting = "welcome", Timeout = 5, MaxRetries = 3, Enabled = true,
            Items = [new IvrMenuItemConfig { Digit = "1", DestType = "queue", DestTarget = "sales" }]
        };
        var data = new DialplanData(
            [new InboundRouteConfig { DidPattern = "5551000", DestinationType = "queue", Destination = "sales", Priority = 1, Enabled = true }],
            [], [], [menu]);
        var lines = DialplanGenerator.Generate(data).Where(l => l.Context == "queues" && l.Exten == "sales").ToList();
        lines.Should().HaveCount(3);
    }
}
