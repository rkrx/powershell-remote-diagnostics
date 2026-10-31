using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;

namespace WindowsPowerShellRc.Transport;

public sealed class ConnectionAddress
{
    public string Host { get; }
    public int Port { get; }
    private readonly string secret;
    private readonly string fingerprint;
    public ConnectionAddress(string host, int port, string secret, string fingerprint)
    {
        Host = host; Port = port; this.secret = secret; this.fingerprint = fingerprint;
    }
    public static ConnectionAddress Parse(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "grpc" ||
            uri.Port is < 1 or > 65535 || string.IsNullOrEmpty(uri.Host) || uri.Host is "0.0.0.0" or "[::]" ||
            uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.UserInfo, "\\Av1\\.[a-f0-9]{64}\\.[a-f0-9]{64}\\z", RegexOptions.CultureInvariant))
            throw new FormatException("Invalid connection URL. Paste the complete grpc:// connection string printed by the server.");
        var pieces = uri.UserInfo.Split('.');
        return new(uri.Host.Trim('[', ']'), uri.Port, pieces[1], pieces[2]);
    }
    public string ToConnectionString() => $"grpc://v1.{secret}.{fingerprint}@{(Host.Contains(':') ? "[" + Host + "]" : Host)}:{Port}";
    public override string ToString() => "[redacted connection]";
    public Metadata Headers => new() { { "authorization", "Bearer " + secret } };
    public GrpcChannel CreateChannel() => GrpcChannel.ForAddress(new UriBuilder("https", Host, Port).Uri,
        new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 4 * 1024 * 1024,
            HttpHandler = new SocketsHttpHandler
            {
                SslOptions = new()
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        // The copied fingerprint is the trust anchor, including for IP/alias connections.
                        // We intentionally do not use system CA/name trust in place of this pin.
                        if (certificate is not X509Certificate2 cert) return false;
                        return cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                            CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(fingerprint));
                    }
                }
            }
        });
}
