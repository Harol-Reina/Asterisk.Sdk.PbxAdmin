namespace PbxAdmin.LoadTests.AgentEmulation;

public enum AgentState
{
    Offline,        // Not registered
    Registering,    // Registration in progress
    Idle,           // Registered, waiting for calls
    Ringing,        // Incoming INVITE received, not yet answered
    InCall,         // Call active, RTP flowing
    OnHold,         // Call on hold
    Wrapup,         // Post-call wrapup period
    Error           // Registration or call failure
}
