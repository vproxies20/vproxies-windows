using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VProxies;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly VProxiesApiClient _api = new();
    private readonly SingBoxCore _core = new();
    public MainWindow()
    {
        InitializeComponent(); _core.Log += AppendLog; LoadSettings(); Closed += (_, _) => _core.Dispose();
        AppendLog("VProxies 0.8.3 prototype ready. No proxy is active.");
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try { await _api.LoginAsync("https://api.vproxies.app/api/v1/", IdentityBox.Text.Trim(), LoginPasswordBox.Password); AppendLog("Signed in successfully. Access token is kept in memory only."); }
        catch (Exception ex) { ShowError(ex); }
        finally { LoginButton.IsEnabled = true; }
    }
    private async void CheckProxy_Click(object sender, RoutedEventArgs e)
    {
        try { var elapsed = await ProxyTester.TestAsync(ReadProxy()); AppendLog($"Proxy handshake succeeded in {elapsed.TotalMilliseconds:0} ms."); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var proxy = ReadProxy(); var routing = ReadRouting(); SaveSettings(proxy, routing);
            var config = SingBoxConfigBuilder.Build(proxy, routing); await _core.StartAsync(config);
            SetConnected(true); AppendLog($"Connected: {proxy.Protocol} {proxy.Host}:{proxy.Port} — {routing.Mode}.");
        }
        catch (Exception ex) { SetConnected(false); ShowError(ex); }
        finally { if (!_core.IsRunning) ConnectButton.IsEnabled = true; }
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e) { _core.Stop(); SetConnected(false); }

    private ProxySettings ReadProxy()
    {
        if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("Proxy port is invalid.");
        return new ProxySettings { Protocol = (ProxyProtocol)ProtocolBox.SelectedIndex, Host = HostBox.Text.Trim(), Port = port, Username = ProxyUsernameBox.Text.Trim(), Password = ProxyPasswordBox.Password, Sni = SniBox.Text.Trim() };
    }
    private RoutingSettings ReadRouting()
    {
        var mode = (RoutingMode)ModeBox.SelectedIndex;
        var applications = ApplicationsBox.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mode != RoutingMode.FullSystem && applications.Length == 0) throw new ArgumentException("Add at least one application name for this routing mode.");
        return new RoutingSettings { Mode = mode, Applications = applications, StrictRoute = StrictRouteBox.IsChecked == true, RemoteDns = RemoteDnsBox.IsChecked == true };
    }
    private void LoadSettings()
    {
        var s = _store.Load(); IdentityBox.Text = s.Identity; ProtocolBox.SelectedIndex = (int)s.Protocol; HostBox.Text = s.Host; PortBox.Text = s.Port > 0 ? s.Port.ToString() : "1080"; ProxyUsernameBox.Text = s.Username; ProxyPasswordBox.Password = SecretStore.Unprotect(s.ProtectedPassword); SniBox.Text = s.Sni; ModeBox.SelectedIndex = (int)s.Mode; ApplicationsBox.Text = s.Applications;
    }
    private void SaveSettings(ProxySettings p, RoutingSettings r) => _store.Save(new StoredSettings { Identity = IdentityBox.Text.Trim(), Protocol = p.Protocol, Host = p.Host, Port = p.Port, Username = p.Username, ProtectedPassword = string.IsNullOrEmpty(p.Password) ? "" : SecretStore.Protect(p.Password), Sni = p.Sni, Mode = r.Mode, Applications = ApplicationsBox.Text });
    private void SetConnected(bool connected)
    {
        StatusText.Text = connected ? "Connected" : "Disconnected"; StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#62E6A6" : "#FF9BA8")); ConnectButton.IsEnabled = !connected; DisconnectButton.IsEnabled = connected;
    }
    private void AppendLog(string message) => Dispatcher.Invoke(() => { LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n"); LogBox.ScrollToEnd(); });
    private void ShowError(Exception ex) { AppendLog("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "VProxies", MessageBoxButton.OK, MessageBoxImage.Error); }
}
