namespace ZenithServer;

public sealed class ServerOptions
{
    /// <summary>IP/host name clients can reach for UDP (ZenFabric). Written into the realm list.</summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    /// <summary>Working directory for accounts, world snapshot, logs, certificates.</summary>
    public string DataDirectory { get; set; } = "data";

    public HttpOptions Http { get; set; } = new();
    public ZenFabricOptions ZenFabric { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public List<RealmOptions> Realms { get; set; } = [new()];

    /// <summary>PlayFab title ID of the client (production = 4BB7B).</summary>
    public string PlayFabTitleId { get; set; } = "4BB7B";

    /// <summary>Value for NoodleAuthResponse.projectId (from NoodleAuthConfig.ProjectID in the client).</summary>
    public string ProjectId { get; set; } = "beta_mexico_eight_889";
}

public sealed class HttpOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 80;
    /// <summary>0 = HTTPS off. Must be 443 for the hosts-file approach.</summary>
    public int HttpsPort { get; set; } = 443;
    /// <summary>Write request/response bodies to data/logs/http.jsonl.</summary>
    public bool LogBodies { get; set; } = true;
}

public sealed class ZenFabricOptions
{
    public int Port { get; set; } = 7777;
    public int DisconnectTimeoutMs { get; set; } = 30000;
    /// <summary>Write all ZenFabric payloads (decrypted) to data/logs/*.zfcap.</summary>
    public bool CapturePackets { get; set; } = true;
    /// <summary>Print every operation to the console (except ping/pong).</summary>
    public bool VerboseOps { get; set; } = true;
    /// <summary>Maximum number of remembered updates per component (for workers that join later).</summary>
    public int MaxUpdateLogPerComponent { get; set; } = 256;
    /// <summary>Worker flags sent via FlagUpdate after InitWorker.</summary>
    public Dictionary<string, string> WorkerFlags { get; set; } = new();
    /// <summary>Token an external UnityGameLogic worker can use to log in (empty = off).</summary>
    public string GameLogicWorkerToken { get; set; } = "";
    /// <summary>Spawn position for newly created player entities (placeholder).</summary>
    public double[] DefaultSpawn { get; set; } = [0, 0, 0];
}

public sealed class AuthOptions
{
    /// <summary>Create unknown platform users / logins automatically.</summary>
    public bool AutoRegister { get; set; } = true;
    /// <summary>Still allow ZenFabric connections with an unknown identity token (development).</summary>
    public bool AllowUnknownIdentityTokens { get; set; } = true;
}

public sealed class RealmOptions
{
    public string Id { get; set; } = "zenith-local-1";
    public string Name { get; set; } = "Zenith Local";
    public string Region { get; set; } = "EU";
    public string Status { get; set; } = "running";
    public float Load { get; set; } = 0.1f;
    /// <summary>Empty = use PublicHost / ZenFabric.Port.</summary>
    public string? WorkerHost { get; set; }
    public int? WorkerPort { get; set; }
}
