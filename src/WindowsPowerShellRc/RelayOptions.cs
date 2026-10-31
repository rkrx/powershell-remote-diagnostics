using System.Net;
namespace WindowsPowerShellRc;

public sealed record RelayOptions(int Port = 8080, string Bind = "All", string? AdvertiseHost = null,
    int RangeStart = 20000, int RangeEnd = 25000, bool Help = false, bool Version = false)
{
    public string PublicHost => AdvertiseHost ?? Dns.GetHostName();
    public static RelayOptions Parse(string[] args)
    {
        var result = new RelayOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing relay option value.");
            int Number() => int.TryParse(Value(), out var n) && n is > 0 and <= 65535 ? n : throw new ArgumentException("Ports must be 1–65535.");
            result = args[i] switch
            {
                "-p" or "--port" => result with { Port = Number() },
                "-b" or "--bind" => result with { Bind = Value() },
                "--advertise-host" => result with { AdvertiseHost = Value() },
                "--port-range-start" => result with { RangeStart = Number() },
                "--port-range-end" => result with { RangeEnd = Number() },
                "-h" or "--help" => result with { Help = true },
                "--version" => result with { Version = true },
                _ => throw new ArgumentException("Unknown relay option.")
            };
        }
        if (result.RangeStart > result.RangeEnd || result.Port >= result.RangeStart && result.Port <= result.RangeEnd)
            throw new ArgumentException("Relay range must be ordered and exclude the control port.");
        if (Uri.CheckHostName(result.PublicHost) == UriHostNameType.Unknown ||
            IPAddress.TryParse(result.PublicHost.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
            throw new ArgumentException("Advertised relay host must be a concrete hostname or IP address.");
        _ = new CliOptions(Bind: result.Bind).ResolveAddresses();
        return result;
    }
    public const string Usage = """
        Usage: powershell-rc relay [-p port] [-b IP|interface|All]
          --port-range-start   First public tunnel port (default: 20000)
          --port-range-end     Last public tunnel port, inclusive (default: 25000)
          --advertise-host     Hostname/IP clients use (default: machine hostname)
          -p, --port           TLS relay registration port (default: 8080)
          -b, --bind           Listening IP/interface (default: All)
          -h, --help           Show help
              --version        Show version
        Relay mode does not initialize PowerShell. Allow inbound TCP to the control port and public range.
        """;
}
