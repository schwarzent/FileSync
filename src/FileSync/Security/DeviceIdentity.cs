using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FileSync.Core;

namespace FileSync.Security;

public sealed class DeviceIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Id => Fingerprint(Certificate);
    private sealed record StoredIdentity(byte[] Certificate, byte[] Key);

    public DeviceIdentity(StateStore store)
    {
        var path = Path.Combine(store.Root, "identity.dpapi");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=FileSync", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var privateKey = key.ExportPkcs8PrivateKey();
            var exported = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(certificate.RawData, privateKey));
            try { File.WriteAllBytes(path, WindowsSecret.Protect(exported)); }
            finally { CryptographicOperations.ZeroMemory(exported); CryptographicOperations.ZeroMemory(privateKey); }
        }
        var bytes = WindowsSecret.Unprotect(File.ReadAllBytes(path));
        try
        {
            var saved = JsonSerializer.Deserialize<StoredIdentity>(bytes) ?? throw new UserError("设备身份无效。");
            using var key = ECDsa.Create();
            try { key.ImportPkcs8PrivateKey(saved.Key, out _); }
            finally { CryptographicOperations.ZeroMemory(saved.Key); }
            using var certificate = X509CertificateLoader.LoadCertificate(saved.Certificate);
            Certificate = certificate.CopyWithPrivateKey(key);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (Certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new UserError("设备证书已过期，请保留数据并重新建立设备身份与配对。");
    }

    public static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);
    public void Dispose() => Certificate.Dispose();
}
