using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace VProxies;

public partial class MainWindow : Window
{
    private const string ApiBase = "https://api.vproxies.app/api/v1/";
    private readonly SettingsStore _store = new();
    private readonly VProxiesApiClient _api = new();
    private readonly SingBoxCore _core = new();
    private readonly TrayIcon _trayIcon = new();
    private readonly DispatcherTimer _entitlementTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _sensitiveLogValues = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingGateways;
    private bool _checkingEntitlement;
    private bool _exitRequested;
    private bool _shutdownInProgress;

    public MainWindow()
    {
        InitializeComponent();
        _core.Log += AppendLog;
        _entitlementTimer.Tick += EntitlementTimer_Tick;
        _trayIcon.ShowRequested += RestoreFromTray;
        _trayIcon.ExitRequested += async () => await ExitApplicationAsync();
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        LoadSettings();
        AppendLog("VProxies 0.9.3 ready. Sign in to load direct proxy connections.");
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdentityBox.Text) || string.IsNullOrWhiteSpace(LoginPasswordBox.Password))
        {
            ShowError(new ArgumentException("Enter your username/email and password."));
            return;
        }

        LoginButton.IsEnabled = false;
        try
        {
            var result = await _api.LoginAsync(ApiBase, IdentityBox.Text.Trim(), LoginPasswordBox.Password);
            LoginPasswordBox.Clear();
            AccountStatusText.Text = FormatAccountStatus(result.UserName, result.Entitlement);
            AccountStatusText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(result.Entitlement.Active ? "#42E3A4" : "#FFC35A"));
            AppendLog($"Signed in as {result.UserName}. Loading authorized Gateways...");
            await LoadGatewaysAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { LoginButton.IsEnabled = true; }
    }

    private async Task LoadGatewaysAsync()
    {
        if (!_api.IsSignedIn) throw new InvalidOperationException("Sign in to your VProxies account first.");
        _loadingGateways = true;
        RefreshGatewaysButton.IsEnabled = false;
        GatewayConnectButton.IsEnabled = false;
        try
        {
            var gateways = await _api.GetGatewaysAsync();
            GatewayBox.ItemsSource = gateways;
            ProxyGrid.ItemsSource = null;
            GatewayProtocolBox.ItemsSource = null;
            ProxyCountText.Text = gateways.Count == 0 ? "No active Gateway is assigned to this account." : $"{gateways.Count} Gateway(s) available";
            AppendLog($"Loaded {gateways.Count} Gateway(s).");
            if (gateways.Count > 0) GatewayBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingGateways = false;
            RefreshGatewaysButton.IsEnabled = true;
        }
        if (GatewayBox.SelectedItem is GatewayInfo gateway) await LoadProxiesAsync(gateway);
    }

    private async Task LoadProxiesAsync(GatewayInfo gateway)
    {
        GatewayConnectButton.IsEnabled = false;
        ProxyCountText.Text = $"Loading proxies from {gateway.DisplayName}...";
        try
        {
            var proxies = await _api.GetProxiesAsync(gateway.Id);
            ProxyGrid.ItemsSource = proxies;
            if (proxies.Count > 0) ProxyGrid.SelectedIndex = 0;
            ProxyCountText.Text = proxies.Count == 0 ? "No proxy is assigned on this Gateway." : $"{proxies.Count} authorized proxy/proxies";
            GatewayConnectButton.IsEnabled = proxies.Count > 0 && GatewayProtocolBox.SelectedItem is string && !_core.IsRunning;
            AppendLog($"Loaded {proxies.Count} authorized proxy/proxies from {gateway.Name}.");
        }
        catch (Exception ex)
        {
            ProxyGrid.ItemsSource = null;
            GatewayProtocolBox.ItemsSource = null;
            ProxyCountText.Text = "Could not load proxies.";
            ShowError(ex);
        }
    }

    private async void RefreshGateways_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadGatewaysAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void GatewayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGateways || GatewayBox.SelectedItem is not GatewayInfo gateway) return;
        await LoadProxiesAsync(gateway);
    }

    private void ProxyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyGrid.SelectedItem is not AssignedProxy proxy)
        {
            GatewayProtocolBox.ItemsSource = null;
            GatewayConnectButton.IsEnabled = false;
            return;
        }

        var protocols = proxy.Protocols
            .Append(proxy.Protocol)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(IsSupportedDirectProtocol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToUpperInvariant())
            .ToArray();
        GatewayProtocolBox.ItemsSource = protocols;
        var preferred = Array.FindIndex(protocols, x => x.Equals(proxy.Protocol, StringComparison.OrdinalIgnoreCase));
        GatewayProtocolBox.SelectedIndex = preferred >= 0 ? preferred : protocols.Length > 0 ? 0 : -1;
        GatewayConnectButton.IsEnabled = protocols.Length > 0 && !_core.IsRunning;
    }

    private async void GatewayConnect_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayBox.SelectedItem is not GatewayInfo gateway || ProxyGrid.SelectedItem is not AssignedProxy selected)
        {
            ShowError(new InvalidOperationException("Select a Gateway and proxy first."));
            return;
        }
        if (GatewayProtocolBox.SelectedItem is not string selectedProtocol)
        {
            ShowError(new InvalidOperationException("This proxy does not advertise a supported connection protocol."));
            return;
        }

        GatewayConnectButton.IsEnabled = false;
        try
        {
            var entitlement = await _api.GetEntitlementAsync();
            if (!entitlement.Active) throw new InvalidOperationException($"Your VProxies access is {entitlement.Status}. Renew or activate the account before connecting.");

            AppendLog($"Requesting a direct configuration for {selected.DisplayName}...");
            var connection = await _api.CreateConnectionAsync(gateway.Id, selected.Id);
            if (!connection.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsupported delivery mode: {connection.Mode}. This client accepts direct mode only.");
            if (string.IsNullOrWhiteSpace(connection.Host) || connection.Port is < 1 or > 65535)
                throw new InvalidOperationException("The API did not return a valid direct proxy endpoint.");

            var protocol = ResolveDirectProtocol(connection, selectedProtocol);
            if (selectedProtocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                AppendLog("HTTPS source label is using HTTP CONNECT transport; upstream TLS metadata is not advertised by the API.");

            var proxy = new ProxySettings
            {
                Protocol = protocol,
                Host = connection.Host,
                Port = connection.Port,
                Username = connection.Username,
                Password = connection.Password
            };
            var location = string.Join(", ", new[] { connection.City, connection.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var description = $"{selected.DisplayName} · {(string.IsNullOrWhiteSpace(location) ? "Chưa xác định" : location)} · {selectedProtocol.ToUpperInvariant()}";
            await ConnectProxyAsync(proxy, description, saveManualSettings: false, concealEndpoint: !connection.ShowHostPort);
            if (connection.ExpiresAt > 0)
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(connection.ExpiresAt).ToLocalTime();
                AppendLog($"Direct configuration valid until {expiry:yyyy-MM-dd HH:mm:ss zzz}. Credentials were not saved.");
            }
            else AppendLog("Direct configuration active. Credentials were not saved.");
        }
        catch (Exception ex)
        {
            SetConnected(false);
            ShowError(ex);
            _sensitiveLogValues.Clear();
        }
        finally
        {
            if (!_core.IsRunning) GatewayConnectButton.IsEnabled = ProxyGrid.SelectedItem is AssignedProxy && GatewayProtocolBox.SelectedItem is string;
        }
    }

    private async void CheckProxy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var elapsed = await ProxyTester.TestAsync(ReadProxy());
            AppendLog($"Proxy handshake succeeded in {elapsed.TotalMilliseconds:0} ms.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var proxy = ReadProxy();
            await ConnectProxyAsync(proxy, $"{proxy.Protocol} {proxy.Host}:{proxy.Port}", saveManualSettings: true, concealEndpoint: false);
        }
        catch (Exception ex)
        {
            SetConnected(false);
            ShowError(ex);
        }
        finally
        {
            if (!_core.IsRunning) ConnectButton.IsEnabled = true;
        }
    }

    private async Task ConnectProxyAsync(ProxySettings proxy, string description, bool saveManualSettings, bool concealEndpoint)
    {
        var routing = ReadRouting();
        if (saveManualSettings) SaveSettings(proxy, routing);
        _sensitiveLogValues.Clear();
        if (concealEndpoint)
        {
            foreach (var value in new[] { proxy.Host, $"{proxy.Host}:{proxy.Port}", proxy.Username, proxy.Password })
                if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3) _sensitiveLogValues.Add(value);
        }
        var config = SingBoxConfigBuilder.Build(proxy, routing);
        await _core.StartAsync(config);
        SetConnected(true);
        AppendLog($"Connected: {description} · {routing.Mode}.");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        try
        {
            await _core.StopAsync();
            SetConnected(false);
            _sensitiveLogValues.Clear();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private ProxySettings ReadProxy()
    {
        if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("Proxy port is invalid.");
        return new ProxySettings
        {
            Protocol = (ProxyProtocol)ProtocolBox.SelectedIndex,
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = ProxyUsernameBox.Text.Trim(),
            Password = ProxyPasswordBox.Password,
            Sni = SniBox.Text.Trim()
        };
    }

    private RoutingSettings ReadRouting()
    {
        var mode = (RoutingMode)ModeBox.SelectedIndex;
        var applications = ApplicationsBox.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mode != RoutingMode.FullSystem && applications.Length == 0) throw new ArgumentException("Add at least one application name for this routing mode.");
        return new RoutingSettings
        {
            Mode = mode,
            Applications = applications,
            StrictRoute = StrictRouteBox.IsChecked == true,
            RemoteDns = RemoteDnsBox.IsChecked == true
        };
    }

    private void LoadSettings()
    {
        var s = _store.Load();
        IdentityBox.Text = s.Identity;
        ProtocolBox.SelectedIndex = (int)s.Protocol;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port > 0 ? s.Port.ToString() : "1080";
        ProxyUsernameBox.Text = s.Username;
        ProxyPasswordBox.Password = SecretStore.Unprotect(s.ProtectedPassword);
        SniBox.Text = s.Sni;
        ModeBox.SelectedIndex = (int)s.Mode;
        ApplicationsBox.Text = s.Applications;
    }

    private void SaveSettings(ProxySettings proxy, RoutingSettings routing) => _store.Save(new StoredSettings
    {
        Identity = IdentityBox.Text.Trim(),
        Protocol = proxy.Protocol,
        Host = proxy.Host,
        Port = proxy.Port,
        Username = proxy.Username,
        ProtectedPassword = string.IsNullOrEmpty(proxy.Password) ? "" : SecretStore.Protect(proxy.Password),
        Sni = proxy.Sni,
        Mode = routing.Mode,
        Applications = ApplicationsBox.Text
    });

    private void SetConnected(bool connected)
    {
        StatusText.Text = connected ? "Connected" : "Disconnected";
        StatusText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(connected ? "#62E6A6" : "#FF9BA8"));
        StatusBadge.Background = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(connected ? "#163D38" : "#2B3449"));
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        GatewayConnectButton.IsEnabled = !connected && ProxyGrid.SelectedItem is AssignedProxy && GatewayProtocolBox.SelectedItem is string;
        if (connected && _api.IsSignedIn) _entitlementTimer.Start(); else _entitlementTimer.Stop();
    }

    private static ProxyProtocol ResolveDirectProtocol(DirectConnectionInfo connection, string requestedProtocol)
    {
        var requested = requestedProtocol.Trim().ToLowerInvariant();
        var allowed = connection.Protocols
            .Append(connection.Protocol)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!IsSupportedDirectProtocol(requested)) throw new InvalidOperationException($"Unsupported direct proxy protocol: {requestedProtocol}");
        if (allowed.Length > 0 && !allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The selected protocol {requestedProtocol.ToUpperInvariant()} is no longer available. Refresh the proxy list.");
        return requested switch
        {
            "http" => ProxyProtocol.HTTP,
            "https" => ProxyProtocol.HTTP,
            "socks4" => ProxyProtocol.SOCKS4,
            "socks5" => ProxyProtocol.SOCKS5,
            _ => throw new InvalidOperationException($"Unsupported direct proxy protocol: {requestedProtocol}")
        };
    }

    private static bool IsSupportedDirectProtocol(string protocol) => protocol is "http" or "https" or "socks4" or "socks5";

    private async void EntitlementTimer_Tick(object? sender, EventArgs e)
    {
        if (_checkingEntitlement || !_core.IsRunning) return;
        _checkingEntitlement = true;
        try
        {
            var entitlement = await _api.GetEntitlementAsync();
            if (!entitlement.Active)
            {
                AppendLog($"Access is {entitlement.Status}; disconnecting.");
                await _core.StopAsync(); SetConnected(false); _sensitiveLogValues.Clear();
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("API 401", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("API 403", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("Account session or entitlement was revoked; disconnecting.");
                await _core.StopAsync(); SetConnected(false); _sensitiveLogValues.Clear();
            }
            else AppendLog("Entitlement check delayed: " + ex.Message);
        }
        finally { _checkingEntitlement = false; }
    }

    private static string FormatAccountStatus(string userName, EntitlementInfo entitlement)
    {
        var plan = string.IsNullOrWhiteSpace(entitlement.PackageName) ? entitlement.Status : entitlement.PackageName;
        if (entitlement.Active) return $"{userName}\n{plan} · {entitlement.RemainingDays} day(s) remaining";
        return $"{userName}\nAccess: {entitlement.Status}";
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        message = SanitizeMessage(message);
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");
        LogBox.ScrollToEnd();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        Hide();
        _trayIcon.ShowMinimizedNotice();
    }

    private void RestoreFromTray()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        await ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        _entitlementTimer.Stop();
        try { await _core.StopAsync(); }
        catch (Exception ex) { AppendLog("Shutdown cleanup warning: " + ex.Message); }
        _sensitiveLogValues.Clear();
        _core.Dispose();
        _trayIcon.Dispose();
        _exitRequested = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowError(Exception ex)
    {
        var message = SanitizeMessage(ex.Message);
        AppendLog("ERROR: " + message);
        System.Windows.MessageBox.Show(this, message, "VProxies", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private string SanitizeMessage(string message)
    {
        foreach (var value in _sensitiveLogValues) message = message.Replace(value, "[hidden]", StringComparison.OrdinalIgnoreCase);
        return message;
    }
}
