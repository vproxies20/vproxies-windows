namespace VProxies;

public enum ProxyProtocol { HTTP, HTTPS, SOCKS4, SOCKS5 }
public enum RoutingMode { FullSystem, RulesOnly, SelectedApplications }

public sealed record GatewayInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Gateway";
    public string Region { get; init; } = "";
    public string SyncMode { get; init; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Region) ? Name : $"{Name} · {Region}";
}

public sealed record AssignedProxy
{
    public long Id { get; init; }
    public string GatewayId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string[] Protocols { get; init; } = [];
    public string GroupName { get; init; } = "";
    public string Status { get; init; } = "";
    public string ExitIp { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string CountryCode { get; init; } = "";
    public string Country { get; init; } = "";
    public string City { get; init; } = "";
    public bool ShowHostPort { get; init; }
    public long? LatencyMs { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Proxy #{Id}" : Name;
    public string LatencyText => LatencyMs is null ? "—" : $"{LatencyMs} ms";
    public string LatencyBand => LatencyMs switch { null => "Unknown", < 50 => "Fast", <= 150 => "Medium", _ => "Slow" };
    public bool IsOnline => Status.Equals("online", StringComparison.OrdinalIgnoreCase);
    public string ProtocolListText => string.Join(" · ", Protocols.Append(Protocol).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => x.ToUpperInvariant()));
    public string CountryFlag
    {
        get
        {
            var code = CountryCode.Trim().ToUpperInvariant();
            return code.Length == 2 && code.All(x => x is >= 'A' and <= 'Z')
                ? string.Concat(code.Select(x => char.ConvertFromUtf32(0x1F1E6 + x - 'A')))
                : "◉";
        }
    }
    public string LocationText
    {
        get
        {
            var value = string.Join(", ", new[] { City, Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(value) ? "Unknown location" : value;
        }
    }
    public string EndpointText => ShowHostPort && !string.IsNullOrWhiteSpace(Host) && Port > 0 ? $"{Host}:{Port}" : "Hidden";
}

public sealed record EntitlementInfo
{
    public bool Active { get; init; }
    public string Status { get; init; } = "unknown";
    public string EndsAt { get; init; } = "";
    public long RemainingDays { get; init; }
    public string PackageName { get; init; } = "";
}

public sealed record LoginResult
{
    public string UserName { get; init; } = "";
    public EntitlementInfo Entitlement { get; init; } = new();
}

public sealed record DirectConnectionInfo
{
    public string Mode { get; init; } = "";
    public string GatewayId { get; init; } = "";
    public long ProxyId { get; init; }
    public long ExpiresAt { get; init; }
    public bool ShowHostPort { get; init; }
    public string CountryCode { get; init; } = "";
    public string Country { get; init; } = "";
    public string Region { get; init; } = "";
    public string City { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string[] Protocols { get; init; } = [];
}

public sealed record ProxySettings
{
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.SOCKS5;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Sni { get; init; } = "";
}

public sealed record RoutingSettings
{
    public RoutingMode Mode { get; init; } = RoutingMode.FullSystem;
    public string[] Applications { get; init; } = [];
    public string[] Domains { get; init; } = [];
    public bool StrictRoute { get; init; } = true;
    public bool RemoteDns { get; init; }
}

public sealed record StoredSettings
{
    public string ApiBase { get; init; } = "https://api.vproxies.app/api/v1/";
    public string Identity { get; init; } = "";
    public bool RememberAccountPassword { get; init; }
    public string ProtectedAccountPassword { get; init; } = "";
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.SOCKS5;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string ProtectedPassword { get; init; } = "";
    public bool RememberProxyPassword { get; init; }
    public string Sni { get; init; } = "";
    public RoutingMode Mode { get; init; } = RoutingMode.FullSystem;
    public string Applications { get; init; } = "";
    public bool RemoteDns { get; init; }
    public bool StrictRoute { get; init; } = true;
}
