using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FileSync.Core;

namespace FileSync.Security;

public sealed class DeviceIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Id => Fingerprint(Certificate);

    public DeviceIdentity(StateStore store)
    {
        var path = Path.Combine(store.Root, "identity.dpapi");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=FileSync", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var exported = certificate.Export(X509ContentType.Pfx);
            try { File.WriteAllBytes(path, WindowsSecret.Protect(exported)); }
            finally { CryptographicOperations.ZeroMemory(exported); }
        }
        var bytes = WindowsSecret.Unprotect(File.ReadAllBytes(path));
        try { Certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (Certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new UserError("设备证书已过期，请保留数据并重新建立设备身份与配对。");
    }

    public static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);
    public static bool Valid(X509Certificate2? cert) => cert is not null &&
        cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow;
    public void Dispose() => Certificate.Dispose();
}
