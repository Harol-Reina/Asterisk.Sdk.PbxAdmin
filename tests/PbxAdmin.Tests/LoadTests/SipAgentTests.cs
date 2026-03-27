using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.Configuration;
using SIPSorcery.SIP;

namespace PbxAdmin.Tests.LoadTests;

/// <summary>
/// Unit tests for SipAgent state machine logic.
///
/// SIPSorcery operations (REGISTER, INVITE, RTP) require real network I/O and are
/// covered by integration/E2E tests against the Docker stack.  These tests focus
/// exclusively on state transitions and behavior selection that can be exercised
/// without a live SIP transport.
/// </summary>
public sealed class SipAgentTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AgentBehaviorOptions DefaultBehavior(
        bool autoAnswer = true,
        int ringDelaySecs = 0,
        int talkTimeSecs = 60,
        int wrapupTimeSecs = 1) =>
        new()
        {
            AgentCount = 1,
            MinAgents = 1,
            MaxAgents = 10,
            AutoAnswer = autoAnswer,
            RingDelaySecs = ringDelaySecs,
            TalkTimeSecs = talkTimeSecs,
            WrapupTimeSecs = wrapupTimeSecs
        };

    private static SipAgent CreateAgent(
        AgentBehaviorOptions? behavior = null,
        string extensionId = "1001")
    {
        var transport = new SIPTransport();
        var logger = Substitute.For<ILogger>();
        return new SipAgent(
            extensionId,
            password: "secret",
            serverHost: "127.0.0.1",
            serverPort: 5060,
            sharedTransport: transport,
            behavior: behavior ?? DefaultBehavior(),
            logger: logger);
    }

    // -------------------------------------------------------------------------
    // Initial state
    // -------------------------------------------------------------------------

    [Fact]
    public void SipAgent_ShouldStartInOfflineState()
    {
        var agent = CreateAgent();

        agent.State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public void SipAgent_ShouldHaveZeroCallsHandled_Initially()
    {
        var agent = CreateAgent();

        agent.CallsHandled.Should().Be(0);
    }

    [Fact]
    public void SipAgent_ShouldHaveNullLastCallTime_Initially()
    {
        var agent = CreateAgent();

        agent.LastCallTime.Should().BeNull();
    }

    [Fact]
    public void SipAgent_ShouldExposeExtensionId()
    {
        var agent = CreateAgent(extensionId: "2050");

        agent.ExtensionId.Should().Be("2050");
    }

    // -------------------------------------------------------------------------
    // RegisterAsync → Registering
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_ShouldTransitionToRegistering_WhenOffline()
    {
        var agent = CreateAgent();
        var states = new List<AgentState>();
        agent.StateChanged += (_, _, s) => states.Add(s);

        await agent.RegisterAsync(CancellationToken.None);

        // The first transition fired synchronously is Registering.
        // (Idle fires asynchronously once registration succeeds against a real server.)
        states.Should().Contain(AgentState.Registering);
    }

    [Fact]
    public async Task RegisterAsync_ShouldBeIdempotent_WhenAlreadyRegistering()
    {
        var agent = CreateAgent();
        await agent.RegisterAsync(CancellationToken.None);
        var statesAfterSecondCall = new List<AgentState>();
        agent.StateChanged += (_, _, s) => statesAfterSecondCall.Add(s);

        // Second call while already registering should be a no-op (no state change).
        await agent.RegisterAsync(CancellationToken.None);

        statesAfterSecondCall.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // HandleIncomingInviteAsync — Idle → Ringing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleIncomingInvite_ShouldTransitionToRinging_WhenIdle()
    {
        var agent = CreateAgent(behavior: DefaultBehavior(autoAnswer: false));

        // RegisterAsync initialises _userAgent (no real network needed for this — registration
        // will time out in the background, but _userAgent is assigned synchronously).
        await agent.RegisterAsync(CancellationToken.None);

        // Override the state field directly so we can test the INVITE guard in isolation.
        ForceIdleState(agent);
        agent.State.Should().Be(AgentState.Idle);

        var request = BuildFakeInvite("1001");
        await agent.HandleIncomingInviteAsync(request);

        agent.State.Should().Be(AgentState.Ringing);
    }

    [Fact]
    public async Task HandleIncomingInvite_ShouldIgnore_WhenNotIdle()
    {
        var agent = CreateAgent(behavior: DefaultBehavior(autoAnswer: false));
        // Agent is Offline — INVITE should be silently ignored.

        var request = BuildFakeInvite("1001");
        await agent.HandleIncomingInviteAsync(request);

        agent.State.Should().Be(AgentState.Offline);
    }

    [Fact]
    public async Task HandleIncomingInvite_ShouldFireStateChanged_ToRinging()
    {
        var agent = CreateAgent(behavior: DefaultBehavior(autoAnswer: false));
        await agent.RegisterAsync(CancellationToken.None);
        ForceIdleState(agent);

        AgentState? captured = null;
        agent.StateChanged += (_, _, s) => captured = s;

        await agent.HandleIncomingInviteAsync(BuildFakeInvite("1001"));

        captured.Should().Be(AgentState.Ringing);
    }

    // -------------------------------------------------------------------------
    // HangupAsync — guards
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HangupAsync_ShouldDoNothing_WhenIdle()
    {
        var agent = CreateAgent();
        ForceIdleState(agent);

        var statesBefore = agent.State;
        await agent.HangupAsync();

        agent.State.Should().Be(statesBefore);
    }

    [Fact]
    public async Task HangupAsync_ShouldDoNothing_WhenOffline()
    {
        var agent = CreateAgent();

        await agent.HangupAsync();

        agent.State.Should().Be(AgentState.Offline);
    }

    // -------------------------------------------------------------------------
    // Wrapup → Idle transition (time-based)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task State_ShouldReturnToIdle_AfterWrapupTimeout()
    {
        // Use a 1-second wrapup to keep the test fast.
        var agent = CreateAgent(behavior: DefaultBehavior(wrapupTimeSecs: 1));
        ForceIdleState(agent);

        // Simulate arriving in Wrapup by calling the internal method via reflection.
        await InvokeBeginWrapupAsync(agent);

        // Give the wrapup timer a little room to fire.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        agent.State.Should().Be(AgentState.Idle);
    }

    // -------------------------------------------------------------------------
    // StateChanged event
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_ShouldFireStateChanged_WithRegistering()
    {
        var agent = CreateAgent();
        AgentState? observed = null;
        agent.StateChanged += (_, _, s) => observed = s;

        await agent.RegisterAsync(CancellationToken.None);

        observed.Should().Be(AgentState.Registering);
    }

    [Fact]
    public async Task StateChanged_ShouldReceiveAgentReference()
    {
        var agent = CreateAgent();
        SipAgent? receivedAgent = null;
        agent.StateChanged += (a, _, _) => receivedAgent = a;

        await agent.RegisterAsync(CancellationToken.None);

        receivedAgent.Should().BeSameAs(agent);
    }

    // -------------------------------------------------------------------------
    // AgentBehaviorType enum coverage
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentBehaviorType.AutoAnswer)]
    [InlineData(AgentBehaviorType.DelayedAnswer)]
    [InlineData(AgentBehaviorType.RejectCall)]
    [InlineData(AgentBehaviorType.AnswerAndTransfer)]
    [InlineData(AgentBehaviorType.AnswerAndHold)]
    [InlineData(AgentBehaviorType.NoAnswer)]
    public void AgentBehaviorType_AllValuesShouldBeDefined(AgentBehaviorType value)
    {
        Enum.IsDefined(value).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // AgentState enum coverage
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentState.Offline)]
    [InlineData(AgentState.Registering)]
    [InlineData(AgentState.Idle)]
    [InlineData(AgentState.Ringing)]
    [InlineData(AgentState.InCall)]
    [InlineData(AgentState.OnHold)]
    [InlineData(AgentState.Wrapup)]
    [InlineData(AgentState.Error)]
    public void AgentState_AllValuesShouldBeDefined(AgentState value)
    {
        Enum.IsDefined(value).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // CalculateStaggeredExpiry — thundering-herd prevention
    // -------------------------------------------------------------------------

    [Fact]
    public void RegistrationExpiry_ShouldBeStaggered_AcrossMultipleAgents()
    {
        // When creating many agents, their registration expiry should vary
        // to avoid thundering-herd re-registration storms.
        var expiries = new HashSet<int>();

        for (int i = 0; i < 50; i++)
        {
            int expiry = SipAgent.CalculateStaggeredExpiry(i);
            expiry.Should().BeInRange(90, 150,
                "expiry should be staggered between 90-150 seconds");
            expiries.Add(expiry);
        }

        // With 50 agents spread over a 60-second range, we should see at least 5 distinct values
        expiries.Count.Should().BeGreaterOrEqualTo(5,
            "expiry values should be distributed, not all the same");
    }

    // -------------------------------------------------------------------------
    // DisposeAsync — does not throw
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNeverRegistered()
    {
        var agent = CreateAgent();

        Func<Task> act = async () => await agent.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_AfterRegisterAsync()
    {
        var agent = CreateAgent();
        await agent.RegisterAsync(CancellationToken.None);

        Func<Task> act = async () => await agent.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    // -------------------------------------------------------------------------
    // Test helpers — reflection-based state forcing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Forces the agent's private <c>_state</c> field to <see cref="AgentState.Idle"/>
    /// without touching any network resources, allowing state-transition tests
    /// to operate independently of SIP registration.
    /// </summary>
    private static void ForceIdleState(SipAgent agent)
    {
        var field = typeof(SipAgent).GetField("_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.Should().NotBeNull("_state field must exist on SipAgent");
        field!.SetValue(agent, AgentState.Idle);
    }

    /// <summary>
    /// Invokes the private <c>BeginWrapupAsync</c> method via reflection so that
    /// the Wrapup→Idle timer can be tested without full call flow.
    /// </summary>
    private static Task InvokeBeginWrapupAsync(SipAgent agent)
    {
        var method = typeof(SipAgent).GetMethod("BeginWrapupAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull("BeginWrapupAsync method must exist on SipAgent");
        return (Task)method!.Invoke(agent, null)!;
    }

    /// <summary>
    /// Builds a minimal SIPRequest that looks like an INVITE, sufficient for
    /// state-machine testing (no real SIP parsing or network needed).
    /// </summary>
    private static SIPRequest BuildFakeInvite(string toExtension)
    {
        var uri = SIPURI.ParseSIPURI($"sip:{toExtension}@127.0.0.1");
        var request = new SIPRequest(SIPMethodsEnum.INVITE, uri);
        request.Header = new SIPHeader(
            new SIPFromHeader("Caller", SIPURI.ParseSIPURI("sip:caller@127.0.0.1"), "tag-abc"),
            new SIPToHeader(null, uri, null),
            cseq: 1,
            callId: Guid.NewGuid().ToString());
        return request;
    }
}
