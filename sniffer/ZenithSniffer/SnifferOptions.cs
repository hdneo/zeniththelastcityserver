namespace ZenithSniffer;

public sealed class SnifferOptions
{
    /// <summary>Local address for the HTTP(S) listeners and UDP bridges.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;

    /// <summary>Address the game client should use for the UDP bridge (written into the rewritten realm list).</summary>
    public string AdvertiseHost { get; set; } = "127.0.0.1";

    /// <summary>First local UDP port for bridges (one per upstream server).</summary>
    public int UdpPortBase { get; set; } = 27777;

    /// <summary>DNS servers used to resolve the REAL addresses (bypasses the hosts file).</summary>
    public List<string> UpstreamDns { get; set; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Optional fixed upstream IPs per host name (if DNS is not wanted).</summary>
    public Dictionary<string, string> HostOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional: redirect an upstream target completely, entries in the form "host:port=ip:port"
    /// (e.g. "nexus.zenithmmo.com:443=10.0.0.5:443", or for testing against a local ZenithServer).
    /// </summary>
    public List<string> UpstreamOverrides { get; set; } = [];

    public bool TryGetUpstreamOverride(string host, int port, out System.Net.IPEndPoint target)
    {
        foreach (var e in UpstreamOverrides)
        {
            var i = e.IndexOf('=');
            if (i > 0 && string.Equals(e[..i].Trim(), $"{host}:{port}", StringComparison.OrdinalIgnoreCase))
            {
                target = System.Net.IPEndPoint.Parse(e[(i + 1)..].Trim());
                return true;
            }
        }
        target = null!;
        return false;
    }

    /// <summary>Rewrite the realm list (api/realm/list) so the client connects through the UDP bridge.</summary>
    public bool RewriteRealmList { get; set; } = true;

    /// <summary>Output folder; each run creates a timestamped subfolder.</summary>
    public string OutputDirectory { get; set; } = "captures";

    /// <summary>Folder of the local CA (same format as the server, so only ONE root certificate is needed).</summary>
    public string CertificateDirectory { get; set; } = "certs";

    /// <summary>Text logs (*.log): max. bytes per packet shown as hex/ASCII (0 = everything).</summary>
    public int TextLogMaxBytes { get; set; } = 65536;

    /// <summary>Console: print every ZenFabric operation (except ping/pong and position spam).</summary>
    public bool VerboseConsole { get; set; } = true;

    /// <summary>Component IDs whose updates are hidden on the console (the logs still contain everything).</summary>
    public List<uint> QuietComponents { get; set; } = [54, 11000];
}
