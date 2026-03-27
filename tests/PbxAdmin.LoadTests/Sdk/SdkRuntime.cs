using Asterisk.Sdk;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Sessions.Manager;

namespace PbxAdmin.LoadTests.Sdk;

/// <summary>
/// Holds the SDK infrastructure created during startup: the AMI connection to the
/// target PBX, the AsteriskServer live-state tracker, and the call session manager.
/// Disposing this record tears down the connection and server in the correct order.
/// </summary>
public sealed record SdkRuntime(
    IAmiConnection Connection,
    AsteriskServer Server,
    ICallSessionManager SessionManager) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
