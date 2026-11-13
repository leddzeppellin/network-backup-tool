using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Estação: liga este computador às pastas da empresa no servidor. No Windows vira a unidade Z:
/// (permanente, com a senha guardada) e os atalhos no Acesso rápido; no Mac/Linux abre no Finder
/// ou no gerenciador de arquivos.
/// </summary>
public partial class StationView : UserControl
{
    private readonly MainView _main;
    private StationConfig? _config;
    private DiscoveredReceiver? _picked;
    private bool _busy;

    public StationView() : this(null!) { } // designer

    public StationView(MainView main)
    {
        _main = main;
        InitializeComponent();
        if (main == null) return;

        if (Platform.IsAndroid)
        {
            ConnectText.Text = L.T("sta.connectTextAndroid");
            SubtitleText.Text = L.T("sta.subtitleAndroid");
            SetupBrowser();
        }
        else if (!Platform.IsWindows)
        {
            // Finder / gerenciador de arquivos pedem a senha e guardam no chaveiro do sistema.
            PasswordLabel.IsVisible = PasswordBox.IsVisible = false;
            ConnectButton.Content = L.T(Platform.IsMac ? "sta.openFinder" : "sta.openFiles");
            ConnectText.Text = L.T("sta.connectTextUnix");
        }
        ElevatedText.IsVisible = Platform.IsWindows && AgentClient.IsElevated;
        ConnectButton.IsEnabled = !ElevatedText.IsVisible;

        SearchButton.Click += async (_, _) => await Search();
        ConnectButton.Click += async (_, _) => await Connect();
        AdminServerButton.IsVisible = main.CanAdministerServer;
        AdminServerButton.Click += (_, _) => main.ShowServerPage();
        PasswordBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await Connect(); };
        ServerBox.TextChanged += (_, _) => { if (_picked != null && ServerBox.Text != _picked.Name) _picked = null; };
        OpenButton.Click += (_, _) => { if (_config != null) Platform.OpenFolder(_config.Drive + "\\"); };
        RefreshButton.Click += async (_, _) => await Refresh(sync: true);
        DisconnectButton.Click += async (_, _) => await Disconnect();

        _config = StationConfig.Load();
        if (_config != null)
        {
            ServerBox.Text = _config.Server;
            ShareBox.Text = _config.Share;
            LoginBox.Text = _config.Login;
        }
        if (Platform.IsAndroid) ResumeBrowser();
        Render();
        // Ao abrir o programa: religa a unidade e acerta os atalhos que o administrador mudou.
        if (_config is { Drive.Length: > 0 } && Platform.IsWindows) _ = Refresh(sync: true);

        AttachedToVisualTree += async (_, _) =>
        {
            if (Platform.IsAndroid) { if (_browser == null) await Search(); else await LoadFolder(_path); }
            else if (_config is not { Drive.Length: > 0 }) await Search();
            else await Refresh(sync: false);
        };
    }

    private bool Connected => Platform.IsAndroid ? _browser != null : _config is { Drive.Length: > 0 };

    private void Render()
    {
        ConnectCard.IsVisible = !Connected;
        ConnectedPanel.IsVisible = Connected && !Platform.IsAndroid;
        BrowserPanel.IsVisible = Connected && Platform.IsAndroid;
        if (Platform.IsAndroid) return;
        if (!Connected || _config == null) return;
        ConnectedTitle.Text = L.T("sta.connectedTitle", _config.Drive, _config.Share);
        ConnectedText.Text = L.T("sta.connectedText", _config.Unc, _config.Login);
    }

    // ---------------------------------------------------------------- procurar servidores

    private async Task Search()
    {
        SearchButton.IsEnabled = false;
        SearchingText.Text = L.T("sta.searching");
        SearchingText.IsVisible = true;
        ServerChips.Children.Clear();
        List<DiscoveredReceiver> found;
        // Android: o Wi-Fi só entrega as respostas de broadcast com o "multicast lock".
        using (Platform.AcquireMulticast?.Invoke())
        {
            try { found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5)); }
            catch { found = new(); }
        }
        var servers = found.Where(r => r.Share != null && !r.Name.Equals(Platform.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
        SearchButton.IsEnabled = true;
        SearchingText.IsVisible = servers.Count == 0;
        SearchingText.Text = L.T("sta.noneFound");
        foreach (var s in servers)
        {
            var chip = new ToggleButton { Classes = { "chip" }, Margin = new(0, 0, 8, 8) };
            chip.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new PathIcon { Data = (Geometry)this.FindResource("IconComputer")!, Width = 14, Height = 14 },
                    new TextBlock { Text = $@"\\{s.Name}\{s.Share}", VerticalAlignment = VerticalAlignment.Center },
                },
            };
            chip.Click += (_, _) =>
            {
                foreach (var c in ServerChips.Children.OfType<ToggleButton>()) c.IsChecked = c == chip;
                _picked = s;
                ServerBox.Text = s.Name;
                ShareBox.Text = s.Share;
                LoginBox.Focus();
            };
            ServerChips.Children.Add(chip);
        }
        // Um servidor só: já deixa escolhido.
        if (servers.Count == 1 && string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            _picked = servers[0];
            ServerBox.Text = servers[0].Name;
            ShareBox.Text = servers[0].Share;
            ((ToggleButton)ServerChips.Children[0]).IsChecked = true;
        }
    }

    // ---------------------------------------------------------------- conectar

    private async Task Connect()
    {
        if (_busy) return;
        var server = (ServerBox.Text ?? "").Trim();
        var share = (ShareBox.Text ?? "").Trim();
        var login = (LoginBox.Text ?? "").Trim();
        var password = PasswordBox.Text ?? "";
        if (server.Length == 0 || share.Length == 0 || login.Length == 0 || ((Platform.IsWindows || Platform.IsAndroid) && password.Length == 0))
        {
            Status(L.T("sta.fillAll"), error: true);
            return;
        }

        if (Platform.IsAndroid)
        {
            await ConnectBrowser(server, _picked?.Address, share, login, password);
            return;
        }
        if (!Platform.IsWindows)
        {
            new StationConfig { Server = server, Share = share, Login = login }.Save();
            Platform.OpenUrl($"smb://{Uri.EscapeDataString(login)}@{server}/{Uri.EscapeDataString(share)}");
            Status(L.T("sta.unixOpened"), error: false);
            return;
        }

        _busy = true;
        ConnectButton.IsEnabled = false;
        Status(L.T("sta.connecting"), error: false);
        var fallback = _picked?.Address;
        var (error, cfg) = await Task.Run(() =>
        {
            var e = StationLink.Connect(server, share, login, password, out var c);
            // O nome não resolveu na rede: tenta pelo endereço que a busca achou.
            if (e != null && fallback != null && e == L.T("sta.noServer", server))
                e = StationLink.Connect(fallback, share, login, password, out c);
            return (e, c);
        });
        _busy = false;
        ConnectButton.IsEnabled = true;
        if (error != null)
        {
            Status(error, error: true);
            return;
        }
        PasswordBox.Text = "";
        Status("", error: false);
        _config = cfg;
        Render();
        await Refresh(sync: true);
    }

    private void Status(string text, bool error)
    {
        StatusText.Text = text;
        if (error) StatusText.Foreground = Brushes.IndianRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ---------------------------------------------------------------- conectado

    private async Task Refresh(bool sync)
    {
        if (_config is not { Drive.Length: > 0 } cfg || !Platform.IsWindows) return;
        RefreshButton.IsEnabled = false;
        var (online, folders, pinned) = await Task.Run(() =>
        {
            StationLink.Reconnect(cfg);
            bool on = StationLink.IsMapped(cfg);
            var pins = on && sync ? StationLink.SyncPins(cfg) : cfg.Pinned.ToList();
            return (on, on ? StationLink.Folders(cfg) : new List<string>(), pins);
        });
        RefreshButton.IsEnabled = true;
        OfflineText.IsVisible = !online;
        OpenButton.IsEnabled = online;
        RenderFolders(folders, pinned, online);
    }

    private void RenderFolders(List<string> folders, List<string> pinned, bool online)
    {
        FolderList.Children.Clear();
        NoFoldersText.IsVisible = online && folders.Count == 0;
        if (_config == null) return;
        foreach (var name in folders)
        {
            var path = Path.Combine(_config.Drive + "\\", name);
            bool isPinned = pinned.Contains(path, StringComparer.OrdinalIgnoreCase);
            var open = new Button { Content = L.T("sta.openOne"), Padding = new(10, 4), FontSize = 12 };
            open.Click += (_, _) => Platform.OpenFolder(path);
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new PathIcon { Data = (Geometry)this.FindResource("IconFolder")!, Width = 16, Height = 16 });
            left.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            if (isPinned) left.Children.Add(new TextBlock { Text = L.T("sta.pinned"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(left);
            FolderList.Children.Add(new Border { Padding = new(0, 4), Child = row });
        }
    }

    private async Task Disconnect()
    {
        if (_config == null) return;
        if (!await _main.Ask(L.T("sta.disconnectAsk", _config.Drive), yesNo: true)) return;
        var cfg = _config;
        await Task.Run(() => StationLink.Disconnect(cfg));
        _config = null;
        PasswordBox.Text = "";
        Render();
        await Search();
    }
}
