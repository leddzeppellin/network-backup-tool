using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

public partial class ReceiveView : UserControl
{
    private readonly MainView _main;
    private readonly IReceiveEngine _engine;
    private IDisposable? _multicast;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, SessionRow> _rows = new();
    private readonly List<SessionRow> _finished = new();
    private const int KeepFinished = 5;

    /// <summary>Uma cópia na lista: quem envia, estado, arquivo atual, barra e números.</summary>
    private sealed class SessionRow
    {
        public readonly SpeedMeter Meter = new();
        public readonly TextBlock State = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
        public readonly Border Pill;
        public readonly TextBlock File = new() { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly ProgressBar Bar = new() { Maximum = 1000 };
        public readonly TextBlock Text = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        public readonly Control Root;

        public SessionRow(string sender)
        {
            State.Text = L.T("recv.on");
            Pill = new Border { Classes = { "pill" }, Child = State, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var header = new DockPanel();
            DockPanel.SetDock(Pill, Dock.Right);
            header.Children.Add(Pill);
            header.Children.Add(new TextBlock { Text = sender, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            Root = new StackPanel { Spacing = 6, Children = { header, File, Bar, Text } };
        }
    }

    public event Action<bool>? ReceivingChanged;

    /// <summary>Recebendo por este app (no modo serviço, fechar o app não interrompe nada).</summary>
    public bool IsReceiving => !_engine.IsService && _engine.IsRunning;
    public bool Busy => !_engine.IsService && _engine.Active.Count > 0;
    public bool IsServiceMode => _engine.IsService;
    public string CurrentCode => _engine.Code;

    public ReceiveView() : this(null!) { } // designer

    public ReceiveView(MainView main)
    {
        _main = main;
        InitializeComponent();
        _engine = main != null && main.Host.IsDesktop && AgentConfig.ServerInstalled ? new ServiceEngine() : new LocalEngine();
        if (main == null) return;
        if (Platform.IsAndroid) SubtitleText.Text = L.T("mobile.recvSubtitle");

        // O serviço manda a hora em que aconteceu (o histórico aparece ao abrir o app).
        _engine.Log += line => ProgressText.Append(LogBox, line, stamp: !_engine.IsService);
        _engine.StateChanged += () =>
        {
            ShowCode();
            ShowDestination();
            if (_engine.IsService) ShowServiceState();
        };
        _engine.SessionStarted += s =>
        {
            RowFor(s);
            _main.Host.Notify(L.T("recv.notifyStartTitle"), L.T("recv.notifyStart", s.Sender));
        };
        _engine.SessionEnded += SessionEnded;

        StartStopButton.Click += (_, _) => Toggle();
        NewCodeButton.Click += async (_, _) =>
        {
            if (_engine.IsService)
            {
                await Report(await _engine.NewCodeAsync());
                return;
            }
            await _engine.NewCodeAsync();
            if (main.Settings.FixedCode.Length == 6 && _engine.Code.Length == 6)
            {
                main.Settings.FixedCode = _engine.Code;
                main.Settings.Save();
            }
            ShowCode();
        };
        ChooseButton.Click += async (_, _) => await ChooseDestination();
        ClearButton.Click += (_, _) =>
        {
            foreach (var row in _finished) Sessions.Children.Remove(row.Root);
            _finished.Clear();
            UpdateEmptyState();
        };
        OpenButton.Click += (_, _) => Platform.OpenFolder(DestinationPath);
        if (Platform.IsWindows && main.Host.IsDesktop && !_engine.IsService)
        {
            FirewallButton.IsVisible = true;
            FirewallButton.Click += (_, _) =>
            {
                var ok = Platform.AllowFirewall();
                Log(ok == null ? L.T("recv.fwCanceled") : ok.Value ? L.T("recv.fwCreated") : L.T("recv.fwNotConfirmed"));
            };
        }
        if (_engine is ServiceEngine svc)
        {
            ServiceBanner.IsVisible = true;
            RestartServiceButton.Click += async (_, _) => await Report(await svc.RestartServiceAsync());
            _refresh.Start(); // o serviço pode estar recebendo antes do app abrir
            ShowServiceState();
        }
        _refresh.Tick += (_, _) => RefreshSessions();
        ShowMachine();
        ShowCode();
        ShowDestination();
    }

    private string DestinationPath => _engine is ServiceEngine svc ? svc.Destination : _main.Settings.DestinationRoot;

    private async Task Report(string? error)
    {
        if (error != null) await _main.Ask(error, error: true);
    }

    private void ShowServiceState()
    {
        var svc = (ServiceEngine)_engine;
        ServiceText.Text = L.T(!svc.Reachable ? "svc.down" : svc.IsRunning ? "svc.on" : "svc.paused");
        RestartServiceButton.IsVisible = !svc.Reachable;
    }

    private void ShowMachine()
    {
        // Adaptadores virtuais (Hyper-V, WSL, Docker) só confundem: mostra as placas de verdade.
        var addrs = Discovery.LocalAddresses()
            .Where(a => !Discovery.IsVirtual(a.Interface))
            .Select(a => a.SpeedBps > 0 ? $"{a.Address}  ·  {a.Interface}, {a.SpeedBps / 1_000_000:N0} Mbps" : $"{a.Address}  ·  {a.Interface}");
        MachineText.Text = string.Join(Environment.NewLine, addrs.Prepend(Platform.DeviceName));
    }

    private void ShowCode()
    {
        bool running = _engine.IsRunning;
        var fixedCode = _main.Settings.FixedCode;
        var code = running ? _engine.Code : _engine.IsService ? _engine.Code : (fixedCode.Length == 6 ? fixedCode : "");
        CodeText.Text = code.Length == 6 ? $"{code[..3]} {code[3..]}" : "--- ---";
        CodeText.Opacity = running ? 1 : 0.45;
        StartStopButton.Content = L.T(running ? "recv.stop" : "recv.start");
        StartStopButton.Classes.Set("accent", !running);
        StateText.Text = L.T(running ? "recv.on" : "recv.off");
        StatePill.Classes.Set("ok", running);
        StateDot.Classes.Set("on", running);
        ReceivingChanged?.Invoke(running);
    }

    private void ShowDestination()
    {
        var path = DestinationPath;
        DestText.Text = path.Length > 0 ? path : L.T("recv.noFolder");
        DestText.Opacity = path.Length > 0 ? 1 : 0.6;
        OpenButton.IsEnabled = path.Length > 0;
    }

    /// <summary>Abre a seleção de pasta do sistema; devolve false se o usuário cancelar.</summary>
    public async Task<bool> ChooseDestination()
    {
        var top = TopLevel.GetTopLevel(this) ?? TopLevel.GetTopLevel(_main);
        if (top == null) return false;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("recv.pickDest"),
        });
        if (picked.Count == 0) return false;
        if (Platform.LocalPath(picked[0]) is not { } path)
        {
            await _main.Ask(L.T("android.badPick"));
            return false;
        }
        if (_engine.IsService)
        {
            var error = await _engine.SetDestinationAsync(path);
            await Report(error);
            ShowDestination();
            return error == null;
        }
        _main.Settings.DestinationRoot = path;
        _main.Settings.Save();
        await _engine.SetDestinationAsync(path);
        ShowDestination();
        return true;
    }

    public void EnsureReceiving()
    {
        if (!_engine.IsRunning && !_engine.IsService) Toggle();
    }

    public async void Toggle()
    {
        if (_engine.IsService)
        {
            if (_engine.IsRunning && _engine.Active.Count > 0 && !await _main.Ask(L.T("busy.stop"), yesNo: true)) return;
            await Report(_engine.IsRunning ? await _engine.StopAsync() : await _engine.StartAsync("", null));
            return;
        }

        if (_engine.IsRunning)
        {
            if (Busy && !await _main.Ask(L.T("busy.stop"), yesNo: true)) return;
            StopReceiver();
            Log(L.T("recv.stopped"));
            return;
        }

        var settings = _main.Settings;
        if (Platform.EnsureStorageAccess != null && !await Platform.EnsureStorageAccess()) return;
        // No celular a pasta padrão é Download/NetBackupTool, sem precisar escolher.
        if (settings.DestinationRoot.Length == 0 && Platform.IsAndroid && Platform.StorageRoot != null)
        {
            settings.DestinationRoot = Path.Combine(Platform.StorageRoot, "Download", "NetBackupTool");
            settings.Save();
            ShowDestination();
        }
        // Primeiro uso (ou pasta que deixou de existir / ficou sem permissão): pede para escolher.
        while (true)
        {
            if (settings.DestinationRoot.Length == 0)
            {
                if (!await ChooseDestination()) return;
                continue;
            }
            try
            {
                Directory.CreateDirectory(settings.DestinationRoot);
                break;
            }
            catch (Exception ex)
            {
                await _main.Ask(L.T("recv.destError", ex.Message), error: true);
                settings.DestinationRoot = "";
                ShowDestination();
            }
        }

        var error = await _engine.StartAsync(settings.DestinationRoot, settings.FixedCode.Length == 6 ? settings.FixedCode : null);
        if (error != null)
        {
            await _main.Ask(error, error: true);
            return;
        }
        _multicast = Platform.AcquireMulticast?.Invoke();
        _main.Host.KeepAwake(true);
        _refresh.Start();
        ShowMachine();
        Log(L.T("recv.waiting", Protocol.DefaultPort));
    }

    public void StopReceiver()
    {
        if (_engine.IsService || !_engine.IsRunning) return;
        _ = _engine.StopAsync();
        _refresh.Stop();
        _multicast?.Dispose();
        _multicast = null;
        _main.Host.KeepAwake(false);
    }

    /// <summary>Código fixo ligado/desligado pela tela de configurações (no serviço o código é sempre fixo).</summary>
    public void FixedCodeChanged(bool on)
    {
        if (_engine.IsService) return;
        _main.Settings.FixedCode = on ? (_engine.IsRunning ? _engine.Code : Protocol.NewCode()) : "";
        _main.Settings.Save();
        _engine.SetFixed(on);
        ShowCode();
    }

    private SessionRow RowFor(SessionSnapshot s)
    {
        if (!_rows.TryGetValue(s.Id, out var row))
        {
            row = new SessionRow(s.Sender);
            _rows[s.Id] = row;
            Sessions.Children.Insert(0, row.Root);
            UpdateEmptyState();
        }
        return row;
    }

    private void UpdateEmptyState()
    {
        NoSessions.IsVisible = Sessions.Children.Count == 0;
        ClearButton.IsVisible = _finished.Count > 0;
    }

    private void SessionEnded(SessionSnapshot s, SessionEnd r)
    {
        // A cópia continua na lista com o resumo, em vez de sumir (arquivos pequenos terminam num piscar).
        var row = RowFor(s);
        _rows.Remove(s.Id);
        row.Bar.Value = 1000;
        row.File.Text = "";
        row.File.IsVisible = false;
        row.Text.Text = L.T("recv.doneStatus", s.Sender, r.FilesOk, Format.Bytes(r.Bytes), r.Skipped, r.FilesFailed);
        row.State.Text = L.T(r.FilesFailed > 0 ? "recv.withFailures" : "recv.done");
        row.Pill.Classes.Set("ok", r.FilesFailed == 0);
        _finished.Add(row);
        while (_finished.Count > KeepFinished)
        {
            Sessions.Children.Remove(_finished[0].Root);
            _finished.RemoveAt(0);
        }
        UpdateEmptyState();

        // O serviço já registra o fim da sessão na atividade; aqui só o resumo do app local.
        if (!_engine.IsService) Log(row.Text.Text);
        foreach (var e in r.Errors) Log(L.T("send.failLine", e));
        if (_engine is ServiceEngine { Replaying: true }) return;
        _main.Host.Notify(L.T("recv.notifyDoneTitle"), r.FilesFailed > 0
            ? L.T("recv.notifyDoneFail", s.Sender, r.FilesOk, r.FilesFailed)
            : L.T("recv.notifyDone", s.Sender, r.FilesOk));
    }

    private void RefreshSessions()
    {
        foreach (var s in _engine.Active)
        {
            var row = RowFor(s);
            var (desc, value) = ProgressText.Describe(s, row.Meter);
            row.Text.Text = desc;
            row.Bar.Value = value;
            row.File.Text = s.CurrentFile;
        }
    }

    private void Log(string line) => ProgressText.Append(LogBox, line);
}
