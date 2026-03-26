using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using PbxAdmin.Models;

namespace PbxAdmin.Services;

/// <summary>
/// Blazor ↔ JavaScript bridge for the embedded WebRTC softphone.
/// Scoped per Blazor circuit — one instance per browser tab.
/// Supports single active connection with credential caching for fast server switching.
/// </summary>
public sealed class SoftphoneService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IToastService _toast;
    private readonly WebRtcProviderResolver _providerResolver;
    private readonly NavigationManager _navigation;
    private readonly Dictionary<string, WebRtcCredentials> _credentialCache = new(StringComparer.OrdinalIgnoreCase);
    private DotNetObjectReference<SoftphoneService>? _dotNetRef;
    private bool _switching;

    public SoftphoneState State { get; private set; } = SoftphoneState.Unregistered;
    public string? ConnectedServerId { get; private set; }
    public string? Extension { get; private set; }
    public string? RemoteNumber { get; private set; }
    public string? RemoteName { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsOnHold { get; private set; }
    public DateTimeOffset? CallStartedAt { get; private set; }

    /// <summary>Raised on any state or property change so UI components can re-render.</summary>
    public event Action? OnStateChanged;

    public SoftphoneService(
        IJSRuntime js,
        IToastService toast,
        WebRtcProviderResolver providerResolver,
        NavigationManager navigation)
    {
        _js = js;
        _toast = toast;
        _providerResolver = providerResolver;
        _navigation = navigation;
    }

    // -----------------------------------------------------------------------
    // Connection lifecycle
    // -----------------------------------------------------------------------

    /// <summary>Provisions a WebRTC extension and registers the SIP UA in the browser.</summary>
    public async Task ConnectAsync(string serverId)
    {
        SetState(SoftphoneState.Registering);
        try
        {
            WebRtcCredentials creds;
            if (_credentialCache.TryGetValue(serverId, out var cached))
            {
                creds = cached;
            }
            else
            {
                var browserHost = new Uri(_navigation.Uri).Host;
                var provider = _providerResolver.GetProvider(serverId);
                creds = await provider.ProvisionAsync(serverId, browserHost);
                _credentialCache[serverId] = creds;
            }

            Extension = creds.Extension;
            ConnectedServerId = serverId;
            _dotNetRef = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("Softphone.register",
                creds.WssUrl, creds.Extension, creds.Password, creds.Extension, _dotNetRef,
                creds.TurnUrl, creds.TurnUsername, creds.TurnPassword);
        }
        catch (Exception ex)
        {
            ConnectedServerId = null;
            SetState(SoftphoneState.Unregistered);
            _toast.Show("Registration failed: " + ex.Message, ToastLevel.Error);
        }
    }

    /// <summary>Unregisters the SIP UA and releases resources.</summary>
    public async Task DisconnectAsync()
    {
        await _js.InvokeVoidAsync("Softphone.unregister");
        SetState(SoftphoneState.Unregistered);
        ConnectedServerId = null;
        Extension = null;
    }

    /// <summary>
    /// Disconnects from the current server and connects to the new one.
    /// Uses cached credentials when available for instant reconnection.
    /// </summary>
    public async Task SwitchServerAsync(string newServerId)
    {
        if (_switching) return;
        if (State is SoftphoneState.Unregistered or SoftphoneState.Registering) return;

        _switching = true;
        try
        {
            await DisconnectAsync();
            await ConnectAsync(newServerId);
        }
        finally
        {
            _switching = false;
        }
    }

    // -----------------------------------------------------------------------
    // Call control
    // -----------------------------------------------------------------------

    /// <summary>Places an outbound call to the given SIP destination or extension number.</summary>
    public async Task CallAsync(string destination)
    {
        if (State != SoftphoneState.Idle) return;
        RemoteNumber = destination;
        RemoteName = null;
        SetState(SoftphoneState.RingingOut);
        await _js.InvokeVoidAsync("Softphone.call", destination);
    }

    /// <summary>Answers an incoming call.</summary>
    public async Task AnswerAsync()
    {
        if (State != SoftphoneState.RingingIn) return;
        await _js.InvokeVoidAsync("Softphone.answer");
    }

    /// <summary>Rejects an incoming call without answering.</summary>
    public async Task RejectAsync()
    {
        if (State != SoftphoneState.RingingIn) return;
        await _js.InvokeVoidAsync("Softphone.hangup");
        SetState(SoftphoneState.Idle);
    }

    /// <summary>Hangs up the current call.</summary>
    public async Task HangupAsync() =>
        await _js.InvokeVoidAsync("Softphone.hangup");

    /// <summary>Toggles hold/unhold on the active call.</summary>
    public async Task ToggleHoldAsync()
    {
        if (IsOnHold) await _js.InvokeVoidAsync("Softphone.unhold");
        else await _js.InvokeVoidAsync("Softphone.hold");
    }

    /// <summary>Toggles microphone mute on the active call.</summary>
    public async Task ToggleMuteAsync()
    {
        if (IsMuted) await _js.InvokeVoidAsync("Softphone.unmute");
        else await _js.InvokeVoidAsync("Softphone.mute");
    }

    /// <summary>Sends a DTMF tone via the active call.</summary>
    public async Task SendDtmfAsync(string digit) =>
        await _js.InvokeVoidAsync("Softphone.sendDtmf", digit);

    // -----------------------------------------------------------------------
    // JS → .NET callbacks
    // -----------------------------------------------------------------------

    [JSInvokable]
    public void OnRegistered()
    {
        SetState(SoftphoneState.Idle);
        _toast.Show("Softphone registered", ToastLevel.Success, Extension);
    }

    [JSInvokable]
    public void OnRegistrationFailed(string reason)
    {
        if (ConnectedServerId is not null)
            _credentialCache.Remove(ConnectedServerId);
        ConnectedServerId = null;
        SetState(SoftphoneState.Unregistered);
        _toast.Show("Registration failed", ToastLevel.Error, reason);
    }

    [JSInvokable]
    public void OnRingingOut() => SetState(SoftphoneState.RingingOut);

    [JSInvokable]
    public void OnIncomingCall(string name, string number)
    {
        RemoteName = string.IsNullOrEmpty(name) ? null : name;
        RemoteNumber = number;
        SetState(SoftphoneState.RingingIn);
    }

    [JSInvokable]
    public void OnCallAnswered()
    {
        CallStartedAt = DateTimeOffset.UtcNow;
        IsMuted = false;
        IsOnHold = false;
        SetState(SoftphoneState.InCall);
    }

    [JSInvokable]
    public void OnCallEnded()
    {
        CallStartedAt = null;
        RemoteNumber = null;
        RemoteName = null;
        IsMuted = false;
        IsOnHold = false;
        SetState(SoftphoneState.Idle);
        _toast.Show("Call ended", ToastLevel.Info);
    }

    [JSInvokable]
    public void OnCallFailed(string reason)
    {
        SetState(SoftphoneState.Idle);
        _toast.Show("Call failed", ToastLevel.Error, reason);
    }

    [JSInvokable]
    public void OnHoldChanged(bool held)
    {
        IsOnHold = held;
        SetState(held ? SoftphoneState.OnHold : SoftphoneState.InCall);
    }

    [JSInvokable]
    public void OnMuteChanged(bool muted)
    {
        IsMuted = muted;
        OnStateChanged?.Invoke();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void SetState(SoftphoneState state)
    {
        State = state;
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (State != SoftphoneState.Unregistered)
        {
            try { await DisconnectAsync(); }
            catch { /* best effort */ }
        }

        _dotNetRef?.Dispose();
    }
}
