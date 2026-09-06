namespace VProxies;

public enum ProxyProtocol { HTTP, HTTPS, SOCKS4, SOCKS5 }
public enum RoutingMode { FullSystem, RulesOnly, SelectedApplications }

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
    public bool RemoteDns { get; init; } = true;
}

public sealed record StoredSettings
{
    public string ApiBase { get; init; } = "https://api.vproxies.app/api/v1/";
    public string Identity { get; init; } = "";
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.SOCKS5;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public string ProtectedPassword { get; init; } = "";
    public string Sni { get; init; } = "";
    public RoutingMode Mode { get; init; } = RoutingMode.FullSystem;
    public string Applications { get; init; } = "";
}
