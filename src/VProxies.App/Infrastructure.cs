using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VProxies;

public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false)); }
        catch { return ""; }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, inBlob.Data, input.Length);
            Blob output;
            var ok = protect
                ? CryptProtectData(ref inBlob, "VProxies", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, output.Size); return bytes; }
            finally { LocalFree(output.Data); }
        }
        finally { CryptographicOperations.ZeroMemory(input); Marshal.FreeHGlobal(inBlob.Data); }
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VProxies", "settings.json");
    public StoredSettings Load()
    {
        try { return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }
    public void Save(StoredSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

public sealed class VProxiesApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public string AccessToken { get; private set; } = "";
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(AccessToken);

    public VProxiesApiClient() => _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    public async Task<LoginResult> LoginAsync(string apiBase, string identity, string password, CancellationToken cancellationToken = default)
    {
        _http.BaseAddress = new Uri(apiBase.EndsWith('/') ? apiBase : apiBase + "/");
        var body = new Dictionary<string, string>
        {
            ["login"] = identity,
            ["password"] = password,
            ["platform"] = "windows",
            ["client_name"] = "VProxies Windows 0.9.1"
        };
        using var document = await SendJsonAsync(HttpMethod.Post, "auth/login", body, cancellationToken, authorize: false);
        var root = document.RootElement;
        AccessToken = GetString(root, "access_token", "token");
        if (string.IsNullOrWhiteSpace(AccessToken)) throw new InvalidOperationException("Login response does not contain an access token.");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        var user = TryProperty(root, "user", out var userNode) ? GetString(userNode, "username", "name", "email") : identity;
        var entitlement = TryProperty(root, "entitlement", out var entitlementNode) ? ParseEntitlement(entitlementNode) : new EntitlementInfo();
        return new LoginResult { UserName = string.IsNullOrWhiteSpace(user) ? identity : user, Entitlement = entitlement };
    }

    public async Task<IReadOnlyList<GatewayInfo>> GetGatewaysAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, "gateways", null, cancellationToken);
        if (!TryProperty(document.RootElement, "gateways", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(x => new GatewayInfo
        {
            Id = GetScalar(x, "id"), Name = GetString(x, "name"), Region = GetString(x, "region"), SyncMode = GetString(x, "sync_mode")
        }).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToArray();
    }

    public async Task<IReadOnlyList<AssignedProxy>> GetProxiesAsync(string gatewayId, CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, $"proxies?gateway_id={Uri.EscapeDataString(gatewayId)}", null, cancellationToken);
        var root = document.RootElement;
        if (!TryProperty(root, "proxies", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var deliveryVisible = TryProperty(root, "delivery", out var delivery) && GetBool(delivery, "show_host_port");
        return items.EnumerateArray().Select(x =>
        {
            var visible = TryProperty(x, "visibility", out var visibility) ? GetBool(visibility, "show_host_port") : deliveryVisible;
            return new AssignedProxy
            {
                Id = GetInt64(x, "id"), GatewayId = GetScalar(x, "gateway_id") is var id && !string.IsNullOrWhiteSpace(id) ? id : gatewayId,
                Name = GetString(x, "name"), Protocol = GetString(x, "protocol"), Protocols = GetStrings(x, "protocols"), GroupName = GetString(x, "group_name", "group"),
                Status = GetString(x, "status"), CountryCode = GetString(x, "country_code"), Country = GetString(x, "country"), City = GetString(x, "city"),
                Host = visible ? GetString(x, "host") : "", Port = visible ? (int)GetInt64(x, "port") : 0,
                ExitIp = visible ? GetString(x, "exit_ip") : "", ShowHostPort = visible, LatencyMs = GetNullableInt64(x, "latency_ms")
            };
        }).Where(x => x.Id > 0).ToArray();
    }

    public async Task<EntitlementInfo> GetEntitlementAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get, "entitlement", null, cancellationToken);
        var root = document.RootElement;
        return TryProperty(root, "data", out var data) ? ParseEntitlement(data) : ParseEntitlement(root);
    }

    public async Task<DirectConnectionInfo> CreateConnectionAsync(string gatewayId, long proxyId, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object> { ["gateway_id"] = gatewayId, ["proxy_id"] = proxyId };
        using var document = await SendJsonAsync(HttpMethod.Post, "connections", body, cancellationToken);
        var root = document.RootElement;
        if (!TryProperty(root, "connection", out var envelope)) throw new InvalidOperationException("Connection response is missing its envelope.");
        if (!TryProperty(envelope, "connection", out var source)) throw new InvalidOperationException("Connection response is missing source proxy data.");
        TryProperty(envelope, "visibility", out var visibility); TryProperty(envelope, "location", out var location);
        return new DirectConnectionInfo
        {
            Mode = GetString(envelope, "mode"), GatewayId = GetScalar(envelope, "gateway_id") is var id && !string.IsNullOrWhiteSpace(id) ? id : gatewayId,
            ProxyId = GetInt64(envelope, "proxy_id"), ExpiresAt = GetInt64(envelope, "expires_at"), ShowHostPort = GetBool(visibility, "show_host_port"),
            CountryCode = GetString(location, "country_code"), Country = GetString(location, "country"), Region = GetString(location, "region"), City = GetString(location, "city"),
            Host = GetString(source, "host"), Port = (int)GetInt64(source, "port"), Username = GetString(source, "username"), Password = GetString(source, "password"),
            Protocol = GetString(source, "protocol"), Protocols = GetStrings(source, "protocols")
        };
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken, bool authorize = true)
    {
        if (authorize && !IsSignedIn) throw new InvalidOperationException("Sign in to your VProxies account first.");
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API {(int)response.StatusCode}: {ReadMessage(json)}");
        return JsonDocument.Parse(json);
    }

    private static EntitlementInfo ParseEntitlement(JsonElement node) => new()
    {
        Active = GetBool(node, "active"), Status = GetString(node, "status"), EndsAt = GetString(node, "ends_at"),
        RemainingDays = GetInt64(node, "remaining_days"), PackageName = GetString(node, "package_name")
    };

    private static string ReadMessage(string json)
    {
        try { return FindString(JsonDocument.Parse(json).RootElement, "message", "error") ?? "Unknown API error"; }
        catch { return json.Length > 180 ? json[..180] : json; }
    }
    private static string? FindString(JsonElement node, params string[] names)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var p in node.EnumerateObject())
            {
                if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                var nested = FindString(p.Value, names); if (nested is not null) return nested;
            }
        if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) { var nested = FindString(item, names); if (nested is not null) return nested; }
        return null;
    }

    private static bool TryProperty(JsonElement node, string name, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static string GetString(JsonElement node, params string[] names)
    {
        foreach (var name in names) if (TryProperty(node, name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return "";
    }
    private static string GetScalar(JsonElement node, string name)
    {
        if (!TryProperty(node, name, out var value)) return "";
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" };
    }
    private static string[] GetStrings(JsonElement node, string name)
    {
        if (!TryProperty(node, name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
    }
    private static long GetInt64(JsonElement node, string name)
    {
        if (!TryProperty(node, name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }
    private static long? GetNullableInt64(JsonElement node, string name) => TryProperty(node, name, out var value) && value.ValueKind != JsonValueKind.Null ? GetInt64(node, name) : null;
    private static bool GetBool(JsonElement node, string name)
    {
        if (!TryProperty(node, name, out var value)) return false;
        return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
    }
}

public static class ProxyTester
{
    public static async Task<TimeSpan> TestAsync(ProxySettings proxy, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(proxy.Host, proxy.Port, cancellationToken);
        await using var networkStream = tcp.GetStream();
        Stream stream = networkStream;
        SslStream? tlsStream = null;
        if (proxy.Protocol == ProxyProtocol.HTTPS)
        {
            tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
            await tlsStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(proxy.Sni) ? proxy.Host : proxy.Sni
            }, cancellationToken);
            stream = tlsStream;
        }
        await using var ownedTlsStream = tlsStream;
        if (proxy.Protocol is ProxyProtocol.HTTP or ProxyProtocol.HTTPS)
        {
            var auth = string.IsNullOrEmpty(proxy.Username) ? "" : $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.Username + ":" + proxy.Password))}\r\n";
            var request = Encoding.ASCII.GetBytes($"CONNECT www.cloudflare.com:443 HTTP/1.1\r\nHost: www.cloudflare.com:443\r\n{auth}Connection: close\r\n\r\n");
            await stream.WriteAsync(request, cancellationToken);
            var buffer = new byte[256]; var count = await stream.ReadAsync(buffer, cancellationToken);
            var status = Encoding.ASCII.GetString(buffer, 0, count);
            if (!status.Contains(" 200 ")) throw new IOException("HTTP CONNECT was rejected: " + status.Split('\n')[0].Trim());
        }
        else if (proxy.Protocol == ProxyProtocol.SOCKS5)
        {
            var auth = string.IsNullOrEmpty(proxy.Username);
            await stream.WriteAsync(auth ? new byte[] { 5, 1, 0 } : new byte[] { 5, 1, 2 }, cancellationToken);
            var hello = new byte[2]; await ReadExact(stream, hello, cancellationToken);
            if (hello[1] == 2)
            {
                var u = Encoding.UTF8.GetBytes(proxy.Username); var p = Encoding.UTF8.GetBytes(proxy.Password);
                var packet = new byte[3 + u.Length + p.Length]; packet[0] = 1; packet[1] = (byte)u.Length; u.CopyTo(packet, 2); packet[2 + u.Length] = (byte)p.Length; p.CopyTo(packet, 3 + u.Length);
                await stream.WriteAsync(packet, cancellationToken); var reply = new byte[2]; await ReadExact(stream, reply, cancellationToken); if (reply[1] != 0) throw new IOException("SOCKS5 authentication failed.");
            }
            else if (hello[1] != 0) throw new IOException("SOCKS5 authentication method was rejected.");
            var host = Encoding.ASCII.GetBytes("www.cloudflare.com"); var connect = new byte[7 + host.Length]; connect[0] = 5; connect[1] = 1; connect[3] = 3; connect[4] = (byte)host.Length; host.CopyTo(connect, 5); connect[^2] = 1; connect[^1] = 187;
            await stream.WriteAsync(connect, cancellationToken); var response = new byte[4]; await ReadExact(stream, response, cancellationToken); if (response[1] != 0) throw new IOException($"SOCKS5 connect failed ({response[1]}).");
        }
        else
        {
            var host = Encoding.ASCII.GetBytes("www.cloudflare.com"); var user = Encoding.UTF8.GetBytes(proxy.Username);
            var packet = new byte[9 + user.Length + host.Length + 1]; packet[0] = 4; packet[1] = 1; packet[2] = 1; packet[3] = 187; packet[4] = packet[5] = packet[6] = 0; packet[7] = 1; user.CopyTo(packet, 8); packet[8 + user.Length] = 0; host.CopyTo(packet, 9 + user.Length);
            await stream.WriteAsync(packet, cancellationToken); var response = new byte[8]; await ReadExact(stream, response, cancellationToken); if (response[1] != 90) throw new IOException($"SOCKS4A connect failed ({response[1]}).");
        }
        watch.Stop(); return watch.Elapsed;
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0; while (offset < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(offset), ct); if (read == 0) throw new EndOfStreamException(); offset += read; }
    }
}
