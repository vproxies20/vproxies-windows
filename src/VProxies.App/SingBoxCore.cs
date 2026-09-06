using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace VProxies;

public static class SingBoxConfigBuilder
{
    public static string Build(ProxySettings proxy, RoutingSettings routing)
    {
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535) throw new ArgumentException("A valid proxy host and port are required.");
        var proxyOutbound = BuildProxy(proxy);
        var direct = new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" };
        var block = new Dictionary<string, object?> { ["type"] = "block", ["tag"] = "block" };
        var rules = new List<object>
        {
            new Dictionary<string, object?> { ["process_name"] = new[] { "VProxies.exe", "sing-box.exe" }, ["action"] = "route", ["outbound"] = "direct" },
            new Dictionary<string, object?> { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" }
        };
        if (IPAddress.TryParse(proxy.Host, out _)) rules.Add(new Dictionary<string, object?> { ["ip_cidr"] = new[] { proxy.Host + (proxy.Host.Contains(':') ? "/128" : "/32") }, ["action"] = "route", ["outbound"] = "direct" });
        var selected = routing.Applications.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var domains = routing.Domains.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (routing.Mode != RoutingMode.FullSystem && selected.Length > 0) rules.Add(new Dictionary<string, object?> { ["process_name"] = selected, ["action"] = "route", ["outbound"] = "proxy" });
        if (routing.Mode == RoutingMode.RulesOnly && domains.Length > 0) rules.Add(new Dictionary<string, object?> { ["domain_suffix"] = domains, ["action"] = "route", ["outbound"] = "proxy" });

        var root = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = BuildDns(routing.RemoteDns),
            ["inbounds"] = new object[] { new Dictionary<string, object?> { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "VProxies", ["address"] = new[] { "172.19.0.1/30" }, ["mtu"] = 9000, ["auto_route"] = true, ["strict_route"] = routing.StrictRoute, ["stack"] = "mixed", ["dns_mode"] = "hijack" } },
            ["outbounds"] = new object[] { proxyOutbound, direct, block },
            ["route"] = new Dictionary<string, object?> { ["rules"] = rules, ["final"] = routing.Mode == RoutingMode.FullSystem ? "proxy" : "direct", ["auto_detect_interface"] = true, ["find_process"] = true, ["default_domain_resolver"] = "dns-local" }
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object?> BuildProxy(ProxySettings p)
    {
        Dictionary<string, object?> result;
        if (p.Protocol is ProxyProtocol.SOCKS4 or ProxyProtocol.SOCKS5)
            result = new() { ["type"] = "socks", ["tag"] = "proxy", ["server"] = p.Host, ["server_port"] = p.Port, ["version"] = p.Protocol == ProxyProtocol.SOCKS4 ? "4a" : "5" };
        else
        {
            result = new() { ["type"] = "http", ["tag"] = "proxy", ["server"] = p.Host, ["server_port"] = p.Port };
            if (p.Protocol == ProxyProtocol.HTTPS) result["tls"] = new Dictionary<string, object?> { ["enabled"] = true, ["server_name"] = string.IsNullOrWhiteSpace(p.Sni) ? p.Host : p.Sni };
        }
        if (!string.IsNullOrEmpty(p.Username)) result["username"] = p.Username;
        if (!string.IsNullOrEmpty(p.Password) && p.Protocol != ProxyProtocol.SOCKS4) result["password"] = p.Password;
        if (!IPAddress.TryParse(p.Host, out _)) result["domain_resolver"] = "dns-local";
        return result;
    }

    private static Dictionary<string, object?> BuildDns(bool remote)
    {
        var servers = new List<object> { new Dictionary<string, object?> { ["type"] = "local", ["tag"] = "dns-local" } };
        if (remote) servers.Add(new Dictionary<string, object?> { ["type"] = "https", ["tag"] = "dns-proxy", ["server"] = "1.1.1.1", ["server_port"] = 443, ["path"] = "/dns-query", ["tls"] = new Dictionary<string, object?> { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" }, ["detour"] = "proxy" });
        return new Dictionary<string, object?> { ["servers"] = servers, ["final"] = remote ? "dns-proxy" : "dns-local" };
    }
}

public sealed class SingBoxCore : IDisposable
{
    private Process? _process;
    public event Action<string>? Log;
    public bool IsRunning => _process is { HasExited: false };
    private string RuntimeDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VProxies", "runtime");
    private string Executable => Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");

    public async Task StartAsync(string config)
    {
        if (IsRunning) return;
        if (!File.Exists(Executable)) throw new FileNotFoundException("Place sing-box.exe in the runtime folder.", Executable);
        Directory.CreateDirectory(RuntimeDir); var configPath = Path.Combine(RuntimeDir, "config.json"); await File.WriteAllTextAsync(configPath, config);
        await RunCheck(configPath);
        _process = new Process { StartInfo = CreateStartInfo($"run -c \"{configPath}\"") , EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
        _process.Exited += (_, _) => Log?.Invoke($"Core exited with code {_process?.ExitCode}.");
        _process.Start(); _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        await Task.Delay(1200); if (_process.HasExited) throw new InvalidOperationException($"sing-box stopped during startup (exit {_process.ExitCode}).");
        FlushDns(); Log?.Invoke("Core started and DNS cache flushed.");
    }
    public void Stop()
    {
        if (_process is { HasExited: false }) { try { _process.Kill(true); _process.WaitForExit(5000); } catch { } }
        _process?.Dispose(); _process = null; FlushDns(); Log?.Invoke("Core stopped and network state released.");
    }
    private async Task RunCheck(string path)
    {
        using var check = new Process { StartInfo = CreateStartInfo($"check -c \"{path}\"") };
        check.Start(); var stdout = await check.StandardOutput.ReadToEndAsync(); var stderr = await check.StandardError.ReadToEndAsync(); await check.WaitForExitAsync();
        if (check.ExitCode != 0) throw new InvalidOperationException("Invalid sing-box config: " + (stderr + stdout).Trim());
    }
    private ProcessStartInfo CreateStartInfo(string args) => new(Executable, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RuntimeDir };
    private static void FlushDns() { try { Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(3000); } catch { } }
    public void Dispose() => Stop();
}
