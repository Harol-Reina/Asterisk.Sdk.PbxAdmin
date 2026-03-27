using Asterisk.Sdk.Ami.Actions;
using FluentAssertions;
using PbxAdmin.LoadTests.CallGeneration;

namespace PbxAdmin.Tests.LoadTests;

public sealed class CallGeneratorServiceTests
{
    private static readonly CallerProfile MobileCaller = new()
    {
        Number = "573101234567",
        DisplayName = "Juan Garcia",
        Operator = "Claro",
        Type = CallerType.Mobile
    };

    private static readonly CallerProfile LandlineCaller = new()
    {
        Number = "576014567890",
        DisplayName = "Maria Rodriguez",
        Operator = "Bogota",
        Type = CallerType.Landline
    };

    // --- BuildOriginateAction: context selection ---

    [Fact]
    public void BuildOriginateAction_ShouldUseRealtimeContext_WhenTargetIsRealtime()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", "test-001");

        action.Channel.Should().Be("Local/200@pstn-to-realtime-dynamic");
    }

    [Fact]
    public void BuildOriginateAction_ShouldUseRealtimeContext_WhenTargetIsUnrecognized()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "other", "test-001");

        action.Channel.Should().Be("Local/200@pstn-to-realtime-dynamic");
    }

    [Fact]
    public void BuildOriginateAction_ShouldUseFileContext_WhenTargetIsFile()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "file", "test-001");

        action.Channel.Should().Be("Local/200@pstn-to-file-dynamic");
    }

    [Fact]
    public void BuildOriginateAction_ShouldUseFileContext_WhenTargetIsFileCaseInsensitive()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "FILE", "test-001");

        action.Channel.Should().Be("Local/200@pstn-to-file-dynamic");
    }

    // --- BuildOriginateAction: channel variable encoding ---

    [Fact]
    public void BuildOriginateAction_ShouldSetCallerNumVariable()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", "test-002");

        AssertVariable(action, "CALLER_NUM", "573101234567");
    }

    [Fact]
    public void BuildOriginateAction_ShouldSetCallerNameVariable()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", "test-002");

        AssertVariable(action, "CALLER_NAME", "Juan Garcia");
    }

    [Fact]
    public void BuildOriginateAction_ShouldSetCallerIdVariables_ForLandlineCaller()
    {
        var action = CallGeneratorService.BuildOriginateAction("2001", LandlineCaller, "realtime", "test-003");

        AssertVariable(action, "CALLER_NUM", "576014567890");
        AssertVariable(action, "CALLER_NAME", "Maria Rodriguez");
    }

    // --- BuildOriginateAction: action properties ---

    [Fact]
    public void BuildOriginateAction_ShouldSetActionId()
    {
        const string callId = "loadtest-000042-20260326120000";

        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", callId);

        action.ActionId.Should().Be(callId);
    }

    [Fact]
    public void BuildOriginateAction_ShouldSetIsAsyncTrue()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", "test-004");

        action.IsAsync.Should().BeTrue();
    }

    [Fact]
    public void BuildOriginateAction_ShouldUseWaitApplication()
    {
        var action = CallGeneratorService.BuildOriginateAction("200", MobileCaller, "realtime", "test-005");

        action.Application.Should().Be("Wait");
        action.Data.Should().Be("300");
    }

    [Fact]
    public void BuildOriginateAction_ShouldEncodeDestinationInChannel()
    {
        var action = CallGeneratorService.BuildOriginateAction("2001", MobileCaller, "realtime", "test-006");

        action.Channel.Should().StartWith("Local/2001@");
    }

    // --- CreateDefaultConnection: structure ---

    [Fact]
    public void CreateDefaultConnection_ShouldExist_AsPrivateMethod()
    {
        var method = typeof(CallGeneratorService).GetMethod("CreateDefaultConnection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull("CreateDefaultConnection must exist on CallGeneratorService");
    }

    // --- Helper ---

    /// <summary>
    /// Reads back a variable set via SetVariable by serialising the extra fields.
    /// The SDK stores them under the "Variable" AMI header key as "KEY=VALUE".
    /// </summary>
    private static void AssertVariable(OriginateAction action, string key, string expectedValue)
    {
        // IHasExtraFields.GetExtraFields() returns the variable map
        var extraFields = ((Asterisk.Sdk.IHasExtraFields)action).GetExtraFields();
        extraFields.Should().NotBeNull();

        // Variables appear with the AMI header name "Variable" and value "KEY=VALUE"
        var match = extraFields!
            .Where(f => string.Equals(f.Key, "Variable", StringComparison.OrdinalIgnoreCase)
                        && f.Value.StartsWith($"{key}=", StringComparison.Ordinal))
            .Select(f => f.Value)
            .FirstOrDefault();

        match.Should().NotBeNull($"variable '{key}' was not found in extra fields");
        match.Should().Be($"{key}={expectedValue}");
    }
}
