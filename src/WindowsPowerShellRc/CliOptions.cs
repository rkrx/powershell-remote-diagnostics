using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace WindowsPowerShellRc;

public sealed record CliOptions(bool Auto = false, bool Test = false, string? DeclarationFile = null,
    int Port = 8080, string Bind = "All", bool Help = false, bool Version = false, string? Relay = null)
{
    public const string DefaultDeclaration = "Only read-only diagnostic operations are allowed. Do not change files, data, configuration, services or permissions. Do not install software or restart the system.";
    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            options = args[i] switch
            {
                "-a" or "--auto" => options with { Auto = true },
                "-t" or "--test" => options with { Test = true },
                "-d" or "--declaration" => options with { DeclarationFile = Value() },
                "-p" or "--port" => options with { Port = int.TryParse(Value(), out int port) && port is > 0 and <= 65535 ? port : throw new ArgumentException("Port must be 1–65535.") },
                "-b" or "--bind" => options with { Bind = Value() },
                "-h" or "--help" => options with { Help = true },
                "--version" => options with { Version = true },
                "--relay" => options with { Relay = Value() },
                _ => throw new ArgumentException($"Unknown option: {args[i]}")
            };
        }
        if (options.Relay is not null)
        {
            try { _ = WindowsPowerShellRc.Transport.ConnectionAddress.Parse(options.Relay); }
            catch (FormatException) { throw new ArgumentException("Invalid relay registration URL."); }
        }
        return options;
    }

    public IReadOnlyList<IPAddress> ResolveAddresses()
    {
        if (Bind.Equals("All", StringComparison.OrdinalIgnoreCase))
            return System.Net.Sockets.Socket.OSSupportsIPv6 ? [IPAddress.IPv6Any] : [IPAddress.Any];
        if (IPAddress.TryParse(Bind, out var address)) return [address];
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Name == Bind)
            ?? throw new ArgumentException($"Unknown local interface: {Bind}");
        var addresses = adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address).Distinct().ToArray();
        if (addresses.Length == 0) throw new ArgumentException($"Interface has no addresses: {Bind}");
        return addresses;
    }

    public string ReadDeclaration() => DeclarationFile is null ? DefaultDeclaration :
        File.ReadAllText(DeclarationFile, new UTF8Encoding(false, true));

    public const string Usage = """
        Windows-Powershell-RC — embedded PowerShell 7 remote diagnostics
        Usage: powershell-rc [-a] [-t] [-d file] [-p port] [-b IP|interface|All]
          -a, --auto          Skip per-script console approval (default: approval required)
          -t, --test          Declare a test environment (default: production)
          -d, --declaration   UTF-8 policy file (default: read-only diagnostics)
          -p, --port          TLS gRPC port (default: 8080)
          -b, --bind          Local IP or exact interface name (default: All, IPv4/IPv6)
              --relay URL    Connect outbound to a pinned relay; diagnostics bind loopback only
          -h, --help          Show this help without starting the server
              --version       Show version without starting the server
        Includes its own PowerShell 7 engine; self-contained packages also include .NET.
        """;
}
