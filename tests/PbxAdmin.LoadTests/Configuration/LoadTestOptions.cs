namespace PbxAdmin.LoadTests.Configuration;

public sealed class LoadTestOptions
{
    public const string SectionName = "LoadTest";

    public string TargetServer { get; init; } = "realtime";
    public AmiConnectionOptions PstnEmulatorAmi { get; init; } = new();
    public AmiConnectionOptions TargetPbxAmi { get; init; } = new();
    public string PostgresConnectionString { get; init; } = string.Empty;
}

public sealed class AmiConnectionOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5038;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
