using FluentAssertions;
using PbxAdmin.LoadTests.Auditing;

namespace PbxAdmin.Tests.LoadTests;

public sealed class ContainerLogCollectorTests
{
    [Fact]
    public void FilterErrors_ShouldExtractErrorAndFatalLines()
    {
        string output = """
            [Mar 28 13:12:56] -- Remote UNIX connection
            [Mar 28 13:12:56] -- Remote UNIX connection disconnected
            [Mar 28 13:13:02] ERROR: res_pjsip_outbound_authenticator_digest.c:504 no auth ids
            [Mar 28 13:13:05] -- Accepting connection
            [Mar 28 13:13:07] FATAL: something terrible
            [Mar 28 13:13:08] WARNING: something concerning
            """;

        var errors = ContainerLogCollector.FilterErrors(output, "demo-pbx-realtime");

        errors.Should().HaveCount(3);
        errors[0].Container.Should().Be("demo-pbx-realtime");
        errors[0].Message.Should().Contain("ERROR");
        errors[1].Message.Should().Contain("FATAL");
        errors[2].Message.Should().Contain("WARNING");
    }

    [Fact]
    public void FilterErrors_ShouldReturnEmpty_WhenNoErrors()
    {
        string output = """
            [Mar 28 13:12:56] -- Remote UNIX connection
            [Mar 28 13:12:56] -- Remote UNIX connection disconnected
            """;

        var errors = ContainerLogCollector.FilterErrors(output, "demo-pstn");

        errors.Should().BeEmpty();
    }

    [Fact]
    public void FilterErrors_ShouldReturnEmpty_WhenOutputIsEmpty()
    {
        ContainerLogCollector.FilterErrors("", "demo-pstn").Should().BeEmpty();
    }
}
