using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WindowsPowerShellRc.Transport;

namespace WindowsPowerShellRc;

public sealed class ServerIdentity : IDisposable
{
    public BearerKey Key { get; } = new();
    public X509Certificate2 Certificate { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString();
    public ServerIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PowerShell Remote Diagnostics", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }
    public ConnectionAddress Address(string host, int port) => new(host, port, Key.Value, Certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());
    public void Dispose() => Certificate.Dispose();
}
