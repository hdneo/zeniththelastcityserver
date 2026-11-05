using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zenith.Protocol.Net;

/// <summary>
/// Small local CA: creates a root certificate (once) and issues matching server certificates
/// per host name (SNI). Needed when the client is pointed at the original domains
/// (nexus.zenithmmo.com, ...) via the hosts file and expects HTTPS.
///
/// The user must import the root certificate (.cer) THEMSELVES into
/// "Trusted Root Certification Authorities" – see docs.
/// </summary>
public sealed class LocalCertificateAuthority
{
    private readonly X509Certificate2 _root;
    private readonly ConcurrentDictionary<string, X509Certificate2> _leafs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;

    public X509Certificate2 Root => _root;
    public string RootCerPath => Path.Combine(_dir, "zenith-local-ca.cer");

    private LocalCertificateAuthority(string dir, X509Certificate2 root)
    {
        _dir = dir;
        _root = root;
    }

    public static LocalCertificateAuthority LoadOrCreate(string dir, string commonName = "Zenith Preservation Local CA")
    {
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "zenith-local-ca.pfx");
        X509Certificate2 root;
        if (File.Exists(pfx))
        {
            root = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        }
        else
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest($"CN={commonName}, O=Zenith Preservation", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx));
            root = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        }
        var ca = new LocalCertificateAuthority(dir, root);
        File.WriteAllBytes(ca.RootCerPath, root.Export(X509ContentType.Cert));
        File.WriteAllText(Path.Combine(dir, "zenith-local-ca.pem"), root.ExportCertificatePem());
        return ca;
    }

    /// <summary>Returns (and caches) a server certificate for the host name or IP.</summary>
    public X509Certificate2 GetServerCertificate(string? host)
    {
        host = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim().TrimEnd('.');
        return _leafs.GetOrAdd(host, CreateLeaf);
    }

    private X509Certificate2 CreateLeaf(string host)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = DateTimeOffset.UtcNow.AddDays(397);
        if (notAfter > _root.NotAfter) notAfter = _root.NotAfter.AddMinutes(-1);
        using var signed = req.Create(_root, DateTimeOffset.UtcNow.AddDays(-1), notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Re-import as PFX so SChannel on Windows can use the private key.
        return new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
