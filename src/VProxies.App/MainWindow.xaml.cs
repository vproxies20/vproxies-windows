using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VProxies;

public partial class MainWindow : Window
{
    private const string ApiBase = "https://api.vproxies.app/api/v1/";
    private readonly SettingsStore _store = new();
    private readonly VProxiesApiClient _api = new();
    private readonly SingBoxCore _core = new();
    private bool _loadingGateways;

    public MainWindow()
    {
        InitializeComponent();
        _core.Log += AppendLog;
        LoadSettings();
        Closed += (_, _) => _core.Dispose();
        AppendLog("VProxies 0.9.0 ready. Sign in to load Gateway proxies.");
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
            AccountStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(result.Entitlement.Active ? "#42E3A4" : "#FFC35A"));
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
            GatewayConnectButton.IsEnabled = proxies.Count > 0 && !_core.IsRunning;
            AppendLog($"Loaded {proxies.Count} authorized proxy/proxies from {gateway.Name}.");
        }
        catch (Exception ex)
        {
            ProxyGrid.ItemsSource = null;
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

    private async void GatewayConnect_Click(object sender, RoutedEventArgs e)
    {
        if (GatewayBox.SelectedItem is not GatewayInfo gateway || ProxyGrid.SelectedItem is not AssignedProxy selected)
        {
            ShowError(new InvalidOperationException("Select a Gateway and proxy first."));
            return;
        }

        GatewayConnectButton.IsEnabled = false;
        try
        {
            var entitlement = await _api.GetEntitlementAsync();
            if (!entitlement.Active) throw new InvalidOperationException($"Your VProxies access is {entitlement.Status}. Renew or activate the account before connecting.");

            AppendLog($"Requesting a short-lived route for {selected.DisplayName}...");
            var route = await _api.CreateRouteAsync(gateway.Id, selected.Id);
            var useSocks = GatewayProtocolBox.SelectedIndex == 1;
            var host = useSocks ? route.Socks5Host : route.HttpHost;
            var port = useSocks ? route.Socks5Port : route.HttpPort;
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
                throw new InvalidOperationException($"The Gateway did not return a valid {(useSocks ? "SOCKS5" : "HTTP")} route.");

            var proxy = new ProxySettings
            {
                Protocol = useSocks ? ProxyProtocol.SOCKS5 : ProxyProtocol.HTTP,
                Host = host,
                Port = port,
                Username = route.Username,
                Password = route.Password
            };
            await ConnectProxyAsync(proxy, $"Gateway {gateway.Name} · {selected.DisplayName}", saveManualSettings: false);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(route.ExpiresAt).ToLocalTime();
            AppendLog($"Route active until {expiry:yyyy-MM-dd HH:mm:ss zzz}. Credentials were not saved.");
        }
        catch (Exception ex)
        {
            SetConnected(false);
            ShowError(ex);
        }
        finally
        {
            if (!_core.IsRunning) GatewayConnectButton.IsEnabled = ProxyGrid.SelectedItem is AssignedProxy;
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
            await ConnectProxyAsync(proxy, $"{proxy.Protocol} {proxy.Host}:{proxy.Port}", saveManualSettings: true);
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

    private async Task ConnectProxyAsync(ProxySettings proxy, string description, bool saveManualSettings)
    {
        var routing = ReadRouting();
        if (saveManualSettings) SaveSettings(proxy, routing);
        var config = SingBoxConfigBuilder.Build(proxy, routing);
        await _core.StartAsync(config);
        SetConnected(true);
        AppendLog($"Connected: {description} · {routing.Mode}.");
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _core.Stop();
        SetConnected(false);
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
        StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#62E6A6" : "#FF9BA8"));
        StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#163D38" : "#2B3449"));
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        GatewayConnectButton.IsEnabled = !connected && ProxyGrid.SelectedItem is AssignedProxy;
    }

    private static string FormatAccountStatus(string userName, EntitlementInfo entitlement)
    {
        var plan = string.IsNullOrWhiteSpace(entitlement.PackageName) ? entitlement.Status : entitlement.PackageName;
        if (entitlement.Active) return $"{userName}\n{plan} · {entitlement.RemainingDays} day(s) remaining";
        return $"{userName}\nAccess: {entitlement.Status}";
    }

    private void AppendLog(string message) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");
        LogBox.ScrollToEnd();
    });

    private void ShowError(Exception ex)
    {
        AppendLog("ERROR: " + ex.Message);
        MessageBox.Show(this, ex.Message, "VProxies", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
