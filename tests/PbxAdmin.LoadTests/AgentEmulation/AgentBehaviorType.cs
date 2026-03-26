namespace PbxAdmin.LoadTests.AgentEmulation;

public enum AgentBehaviorType
{
    AutoAnswer,         // Answer after ring delay, talk for configured time, hangup
    DelayedAnswer,      // Answer after longer delay (simulates slow agent)
    RejectCall,         // Send busy/decline
    AnswerAndTransfer,  // Answer, talk briefly, then blind transfer to another extension
    AnswerAndHold,      // Answer, talk, put on hold, resume, hangup
    NoAnswer            // Never answer (simulates away agent)
}
