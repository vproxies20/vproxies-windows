using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfButton = System.Windows.Controls.Button;

namespace VProxies;

public partial class MainWindow : Window
{
    private const string ApiBase = "https://api.vproxies.app/api/v1/";
    private readonly SettingsStore _store = new();
    private readonly VProxiesApiClient _api = new();
    private readonly SingBoxCore _core = new();
    private readonly UpdateService _updates = new();
    private readonly TrayIcon _trayIcon = new();
    private readonly DispatcherTimer _entitlementTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _sensitiveLogValues = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AssignedProxy> _loadedProxies = [];
    private bool _loadingGateways;
    private bool _checkingEntitlement;
    private bool _exitRequested;
    private bool _shutdownInProgress;
    private bool _checkingForUpdates;

    public MainWindow()
    {
        InitializeComponent();
        _core.Log += AppendLog;
        _entitlementTimer.Tick += EntitlementTimer_Tick;
        _trayIcon.ShowRequested += RestoreFromTray;
        _trayIcon.ExitRequested += async () => await ExitApplicationAsync();
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => { await Task.Delay(1800); await CheckForUpdatesAsync(silentWhenCurrent: true); };
        LoadSettings();
        AppendLog("VProxies 1.0.2 ready. Sign in to load direct proxy connections.");
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(silentWhenCurrent: false);

    private async Task CheckForUpdatesAsync(bool silentWhenCurrent)
    {
        if (_checkingForUpdates || _shutdownInProgress) return;
        _checkingForUpdates = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var update = await _updates.CheckAsync();
            if (update is null)
            {
                if (!silentWhenCurrent) System.Windows.MessageBox.Show(this, $"VProxies {UpdateService.CurrentVersion.ToString(3)} is up to date.", "VProxies Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var choice = System.Windows.MessageBox.Show(this, $"VProxies {update.Version.ToString(3)} is available.\n\nDownload, verify, and install it now?", "VProxies Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice != MessageBoxResult.Yes) return;
            AppendLog($"Downloading verified update {update.Version.ToString(3)} from GitHub...");
            UpdateButton.Content = "DOWNLOADING";
            var installerPath = await _updates.DownloadAndVerifyAsync(update);
            AppendLog("Update verified. Starting installer...");
            Process.Start(new ProcessStartInfo(installerPath, "/SILENT /CLOSEAPPLICATIONS /NORESTART") { UseShellExecute = true, Verb = "runas" });
            await ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            if (!silentWhenCurrent) ShowError(new InvalidOperationException("Update check failed: " + ex.Message, ex));
            else AppendLog("Update check delayed: " + ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
            if (!_shutdownInProgress)
            {
                UpdateButton.Content = UpdateService.CurrentVersion.ToString(3);
                UpdateButton.IsEnabled = true;
            }
        }
    }

    private void SidebarNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button) return;
        SetActiveSidebarButton(button);

        switch (button.Tag as string)
        {
            case "fleet":
                ProxyGrid.BringIntoView();
                ProxyGrid.Focus();
                break;
            case "account":
                IdentityBox.BringIntoView();
                IdentityBox.Focus();
                break;
            case "routing":
                ModeBox.BringIntoView();
                ModeBox.Focus();
                break;
            case "activity":
                LogBox.BringIntoView();
                LogBox.Focus();
                break;
            case "support":
                try { Process.Start(new ProcessStartInfo("https://vproxies.app") { UseShellExecute = true }); }
                catch (Exception ex) { ShowError(ex); }
                break;
        }
    }

    private void SetActiveSidebarButton(WpfButton active)
    {
        foreach (var button in new[] { SidebarFleetButton, SidebarAccountButton, SidebarRoutingButton, SidebarActivityButton, SidebarSupportButton })
        {
            button.Background = Brush(button == active ? "#15324A" : "#00000000");
            button.BorderBrush = Brush(button == active ? "#13E5FF" : "#00000000");
            button.Foreground = Brush(button == active ? "#EAFBFF" : "#8FA3BE");
        }
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
            SaveCurrentSettings();
            if (RememberAccountPasswordBox.IsChecked != true) LoginPasswordBox.Clear();
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
            _loadedProxies = [];
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
            _loadedProxies = proxies;
            ApplyProxyFilter();
            if (proxies.Count > 0) ProxyGrid.SelectedIndex = 0;
            ProxyCountText.Text = proxies.Count == 0 ? "No proxy is assigned on this Gateway." : $"{proxies.Count} authorized proxy/proxies";
            GatewayConnectButton.IsEnabled = ProxyGrid.SelectedItem is AssignedProxy { IsOnline: true } && GatewayProtocolBox.SelectedItem is string && !_core.IsRunning;
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
        GatewayConnectButton.IsEnabled = proxy.IsOnline && protocols.Length > 0 && !_core.IsRunning;
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
            var description = $"{selected.DisplayName} · {(string.IsNullOrWhiteSpace(location) ? "Unknown location" : location)} · {selectedProtocol.ToUpperInvariant()}";
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

    private void ProxyRowConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!_core.IsRunning && sender is WpfButton { CommandParameter: AssignedProxy { IsOnline: true } proxy })
        {
            ProxyGrid.SelectedItem = proxy;
            ProxyGrid.ScrollIntoView(proxy);
            GatewayConnect_Click(sender, e);
        }
    }

    private void ProxySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyProxyFilter();

    private void ApplyProxyFilter()
    {
        var query = ProxySearchBox.Text.Trim();
        IReadOnlyList<AssignedProxy> filtered = string.IsNullOrWhiteSpace(query) ? _loadedProxies : _loadedProxies.Where(proxy =>
            new[] { proxy.DisplayName, proxy.Country, proxy.City, proxy.Protocol, proxy.ProtocolListText, proxy.EndpointText }
                .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        ProxyGrid.ItemsSource = filtered;
        ProxyCountText.Text = _loadedProxies.Count == 0 ? "No authorized proxies" : $"{filtered.Count} shown · {_loadedProxies.Count} authorized";
    }

    private async Task ConnectProxyAsync(ProxySettings proxy, string description, bool saveManualSettings, bool concealEndpoint)
    {
        var routing = ReadRouting();
        if (saveManualSettings) SaveCurrentSettings(proxy, routing);
        _sensitiveLogValues.Clear();
        if (concealEndpoint)
        {
            foreach (var value in new[] { proxy.Host, $"{proxy.Host}:{proxy.Port}", proxy.Username, proxy.Password })
                if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3) _sensitiveLogValues.Add(value);
        }
        var config = SingBoxConfigBuilder.Build(proxy, routing);
        await _core.StartAsync(config);
        SetConnected(true);
        ConnectionDetailText.Text = description;
        AppendLog($"Connected: {description} · {routing.Mode}.");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        DisconnectButton.IsEnabled = false;
        GatewayDisconnectButton.IsEnabled = false;
        HeaderDisconnectButton.IsEnabled = false;
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
        RememberAccountPasswordBox.IsChecked = s.RememberAccountPassword;
        LoginPasswordBox.Password = s.RememberAccountPassword ? SecretStore.Unprotect(s.ProtectedAccountPassword) : "";
        ProtocolBox.SelectedIndex = (int)s.Protocol;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port > 0 ? s.Port.ToString() : "1080";
        ProxyUsernameBox.Text = s.Username;
        RememberProxyPasswordBox.IsChecked = s.RememberProxyPassword;
        ProxyPasswordBox.Password = s.RememberProxyPassword ? SecretStore.Unprotect(s.ProtectedPassword) : "";
        SniBox.Text = s.Sni;
        ModeBox.SelectedIndex = (int)s.Mode;
        ApplicationsBox.Text = s.Applications;
        RemoteDnsBox.IsChecked = s.RemoteDns;
        StrictRouteBox.IsChecked = s.StrictRoute;
    }

    private void SaveCurrentSettings(ProxySettings? proxy = null, RoutingSettings? routing = null)
    {
        proxy ??= TryReadProxy();
        routing ??= new RoutingSettings
        {
            Mode = ModeBox.SelectedIndex >= 0 ? (RoutingMode)ModeBox.SelectedIndex : RoutingMode.FullSystem,
            Applications = ApplicationsBox.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StrictRoute = StrictRouteBox.IsChecked == true,
            RemoteDns = RemoteDnsBox.IsChecked == true
        };
        var rememberAccount = RememberAccountPasswordBox.IsChecked == true;
        var rememberProxy = RememberProxyPasswordBox.IsChecked == true;
        _store.Save(new StoredSettings
        {
            Identity = IdentityBox.Text.Trim(),
            RememberAccountPassword = rememberAccount,
            ProtectedAccountPassword = rememberAccount && !string.IsNullOrEmpty(LoginPasswordBox.Password) ? SecretStore.Protect(LoginPasswordBox.Password) : "",
            Protocol = proxy.Protocol,
            Host = proxy.Host,
            Port = proxy.Port,
            Username = proxy.Username,
            RememberProxyPassword = rememberProxy,
            ProtectedPassword = rememberProxy && !string.IsNullOrEmpty(proxy.Password) ? SecretStore.Protect(proxy.Password) : "",
            Sni = proxy.Sni,
            Mode = routing.Mode,
            Applications = ApplicationsBox.Text,
            RemoteDns = routing.RemoteDns,
            StrictRoute = routing.StrictRoute
        });
    }

    private ProxySettings TryReadProxy()
    {
        _ = int.TryParse(PortBox.Text, out var port);
        return new ProxySettings
        {
            Protocol = ProtocolBox.SelectedIndex >= 0 ? (ProxyProtocol)ProtocolBox.SelectedIndex : ProxyProtocol.SOCKS5,
            Host = HostBox.Text.Trim(), Port = port, Username = ProxyUsernameBox.Text.Trim(),
            Password = ProxyPasswordBox.Password, Sni = SniBox.Text.Trim()
        };
    }

    private void SetConnected(bool connected)
    {
        StatusText.Text = connected ? "Connected" : "Disconnected";
        StatusText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(connected ? "#62E6A6" : "#FF9BA8"));
        StatusBadge.Background = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(connected ? "#163D38" : "#2B3449"));
        StatusDot.Fill = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(connected ? "#3BE3A0" : "#FF718B"));
        if (!connected) ConnectionDetailText.Text = "No proxy is active";
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        GatewayDisconnectButton.IsEnabled = connected;
        HeaderDisconnectButton.IsEnabled = connected;
        GatewayConnectButton.IsEnabled = !connected && ProxyGrid.SelectedItem is AssignedProxy { IsOnline: true } && GatewayProtocolBox.SelectedItem is string;
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

    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        message = AnsiPattern.Replace(SanitizeMessage(message), "").Trim();
        var level = message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || message.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "ERROR"
            : message.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "WARN" : "INFO";
        var color = level == "ERROR" ? "#FF6689" : level == "WARN" ? "#FFC857" : "#45DFA2";
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
        paragraph.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss")) { Foreground = Brush("#7186A3") });
        paragraph.Inlines.Add(new Run($"  {level,-5}  ") { Foreground = Brush(color), FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(message) { Foreground = Brush("#D8E5F5") });
        LogBox.Document.Blocks.Add(paragraph);
        while (LogBox.Document.Blocks.Count > 400 && LogBox.Document.Blocks.FirstBlock is Block first) LogBox.Document.Blocks.Remove(first);
        LogSummaryText.Text = $"  ·  {level}";
        if (AutoScrollBox.IsChecked == true) LogBox.ScrollToEnd();
    }

    private static SolidColorBrush Brush(string color) => new((MediaColor)MediaColorConverter.ConvertFromString(color));

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text.Trim();
        if (!string.IsNullOrWhiteSpace(text)) System.Windows.Clipboard.SetText(text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        LogSummaryText.Text = "  ·  Cleared";
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
        try { SaveCurrentSettings(); } catch { }
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
