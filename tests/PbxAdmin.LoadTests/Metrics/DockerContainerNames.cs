namespace PbxAdmin.LoadTests.Metrics;

/// <summary>
/// Container names from the Docker Compose stack (docker-compose.pbxadmin.yml).
/// </summary>
public static class DockerContainerNames
{
    public const string Postgres = "demo-postgres";
    public const string PbxRealtime = "demo-pbx-realtime";
    public const string PbxFile = "demo-pbx-file";
    public const string Pstn = "demo-pstn";
    public const string PbxAdmin = "asterisk-pbx-admin";

    public static readonly string[] All = [Postgres, PbxRealtime, PbxFile, Pstn, PbxAdmin];
    public const string PrimaryTarget = PbxRealtime;
}
