using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>Administração remota: o mesmo painel, falando com o serviço de outro PC pela rede.</summary>
public partial class ServerView
{
    /// <summary>Este PC é o servidor (serviço instalado aqui).</summary>
    private static bool LocalServer => AgentConfig.ServerInstalled;
    private bool _connectForm;
    private DiscoveredReceiver? _remotePicked;

    /// <summary>Há um servidor para o painel falar: o deste PC ou um remoto conectado.</summary>
    private static bool HasServer => AgentClient.Remote != null || LocalServer;

    private static bool IsRemote => AgentClient.Remote != null;

    /// <summary>Nome do computador do servidor administrado (vai no endereço \\NOME\Compartilhamento).</summary>
    private static string ServerName => AgentClient.Remote?.MachineName ?? Platform.DeviceName;

    private void SetupRemote()
    {
        OtherServerButton.Click += async (_, _) => { _connectForm = true; UpdateRemoteUi(); await SearchServers(); };
        RemoteCancelButton.Click += (_, _) => { _connectForm = false; UpdateRemoteUi(); };
        RemoteSearchButton.Click += async (_, _) => await SearchServers();
        RemoteConnectButton.Click += async (_, _) => await ConnectRemote();
        RemotePasswordBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await ConnectRemote(); };
        RemoteDisconnectButton.Click += async (_, _) => await DisconnectRemote();
        RootPathBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await UseRemoteRoot(); };
        _connectForm = !LocalServer;
        UpdateRemoteUi();
        // Abriu a página sem servidor neste PC: já procura os da rede.
        AttachedToVisualTree += async (_, _) => { if (RemoteCard.IsVisible && RemoteChips.Children.Count == 0) await SearchServers(); };
    }

    private void UpdateRemoteUi()
    {
        bool form = _connectForm && !IsRemote;
        RemoteCard.IsVisible = form;
        RemoteCancelButton.IsVisible = LocalServer;
        ServerContent.IsVisible = !form && HasServer;
        if (form) ProblemText.IsVisible = false;
        ApplyBar.IsVisible = !form && HasServer && Dirty;

        RemoteBar.IsVisible = !form;
        RemoteText.Text = IsRemote
            ? L.T("rem.connectedTo", AgentClient.Remote!.MachineName, AgentClient.Remote.UserName)
            : L.T("rem.thisPc", Platform.DeviceName);
        RemoteDisconnectButton.IsVisible = IsRemote;
        OtherServerButton.IsVisible = !IsRemote;

        // A pasta da empresa de outro PC é digitada: o seletor de pastas só vê este computador.
        RootText.IsVisible = !IsRemote;
        RootPathBox.IsVisible = IsRemote;
        ChooseRootButton.Content = L.T(IsRemote ? "rem.useRoot" : "srv.chooseRoot");
    }

    private async Task SearchServers()
    {
        RemoteSearchButton.IsEnabled = false;
        RemoteSearchingText.Text = L.T("sta.searching");
        RemoteSearchingText.IsVisible = true;
        RemoteChips.Children.Clear();
        List<DiscoveredReceiver> found;
        try { found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5)); }
        catch { found = new(); }
        // Primeiro os que já compartilham a pasta da empresa (servidores de verdade).
        var servers = found.Where(r => !r.Name.Equals(Platform.DeviceName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Share == null).ThenBy(r => r.Name).ToList();
        RemoteSearchButton.IsEnabled = true;
        RemoteSearchingText.IsVisible = servers.Count == 0;
        RemoteSearchingText.Text = L.T("sta.noneFound");
        foreach (var s in servers)
        {
            var chip = new ToggleButton { Classes = { "chip" }, Margin = new(0, 0, 8, 8) };
            chip.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new PathIcon { Data = Res.Get<Geometry>("IconComputer"), Width = 14, Height = 14 },
                    new TextBlock { Text = $"{s.Name}  ({s.Address})", VerticalAlignment = VerticalAlignment.Center },
                },
            };
            chip.Click += (_, _) =>
            {
                foreach (var c in RemoteChips.Children.OfType<ToggleButton>()) c.IsChecked = c == chip;
                _remotePicked = s;
                RemoteHostBox.Text = s.Name;
                RemoteUserBox.Focus();
            };
            RemoteChips.Children.Add(chip);
        }
        if (servers.Count(s => s.Share != null) == 1 && string.IsNullOrWhiteSpace(RemoteHostBox.Text))
        {
            _remotePicked = servers[0];
            RemoteHostBox.Text = servers[0].Name;
            ((ToggleButton)RemoteChips.Children[0]).IsChecked = true;
        }
    }

    private async Task ConnectRemote()
    {
        var typed = (RemoteHostBox.Text ?? "").Trim().TrimStart('\\').Split('\\')[0];
        if (typed.Length == 0)
        {
            RemoteStatus.Text = L.T("rem.noHost");
            return;
        }
        // Escolhido na lista: usa o endereço que respondeu (o nome pode não resolver em toda rede).
        var host = _remotePicked != null && _remotePicked.Name.Equals(typed, StringComparison.OrdinalIgnoreCase) ? _remotePicked.Address : typed;
        var user = (RemoteUserBox.Text ?? "").Trim();
        // Conta sem "PC\": é do servidor (fora de domínio é o caso comum).
        if (user.Length > 0 && !user.Contains('\\') && !user.Contains('@')) user = $@"{typed}\{user}";

        RemoteConnectButton.IsEnabled = false;
        RemoteStatus.Text = L.T("rem.connecting", typed);
        var (server, error) = await RemoteServer.ConnectAsync(host, user, RemotePasswordBox.Text ?? "");
        RemoteConnectButton.IsEnabled = true;
        if (server == null)
        {
            RemoteStatus.Text = error ?? "?";
            return;
        }
        RemoteStatus.Text = "";
        RemotePasswordBox.Text = "";
        await SwitchServer(server);
    }

    private async Task DisconnectRemote()
    {
        await SwitchServer(null);
        _connectForm = !LocalServer;
        UpdateRemoteUi();
        if (_connectForm) await SearchServers();
    }

    /// <summary>Troca o servidor administrado; o que estava sendo editado no outro é descartado.</summary>
    private async Task SwitchServer(RemoteServer? server)
    {
        var old = AgentClient.Remote;
        AgentClient.Remote = server;
        old?.Dispose();
        _saved = new ShareConfig();
        _draft = new ShareConfig();
        _passwords.Clear();
        _selected = null;
        _activity.Clear();
        SessionsText.Text = OpenCountText.Text = PeopleCountText.Text = "—";
        OpenFilesList.Children.Clear();
        _connectForm = false;
        UpdateRemoteUi();
        RenderAll();
        if (HasServer) await Reload();
    }

    /// <summary>Pasta da empresa digitada (servidor remoto). As pastas que já existem nela entram na lista.</summary>
    private async Task UseRemoteRoot()
    {
        var path = (RootPathBox.Text ?? "").Trim().TrimEnd('\\');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:\\"))
        {
            await _main.Ask(L.T("rem.rootInvalid"), error: true);
            return;
        }
        _draft.Root = path;
        await ImportDirs(path);
        // Pasta que ainda não existe: é criada no Aplicar.
        Changed();
    }
}
