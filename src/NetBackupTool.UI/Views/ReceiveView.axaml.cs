using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

public partial class ReceiveView : UserControl
{
    private readonly MainView _main;
    private Receiver? _receiver;
    private IDisposable? _multicast;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<ReceiveSession, SessionRow> _rows = new();
    private readonly List<SessionRow> _finished = new();
    private const int KeepFinished = 5;
    private const long LogFileMinSize = 10L * 1024 * 1024;

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

    public bool IsReceiving => _receiver != null;
    public bool Busy => _receiver?.ActiveSessions.Count > 0;
    public string CurrentCode => _receiver?.Code ?? "";

    public ReceiveView() : this(null!) { } // designer

    public ReceiveView(MainView main)
    {
        _main = main;
        InitializeComponent();
        if (main == null) return;
        if (Platform.IsAndroid) SubtitleText.Text = L.T("mobile.recvSubtitle");

        ShowDestination();
        StartStopButton.Click += (_, _) => Toggle();
        NewCodeButton.Click += (_, _) =>
        {
            var code = Protocol.NewCode();
            _receiver?.SetCode(code);
            if (main.Settings.FixedCode.Length == 6)
            {
                main.Settings.FixedCode = code;
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
        OpenButton.Click += (_, _) => Platform.OpenFolder(main.Settings.DestinationRoot);
        if (Platform.IsWindows && main.Host.IsDesktop)
        {
            FirewallButton.IsVisible = true;
            FirewallButton.Click += (_, _) =>
            {
                var ok = Platform.AllowFirewall();
                Log(ok == null ? L.T("recv.fwCanceled") : ok.Value ? L.T("recv.fwCreated") : L.T("recv.fwNotConfirmed"));
            };
        }
        _refresh.Tick += (_, _) => RefreshSessions();
        ShowMachine();
        ShowCode();
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
        var fixedCode = _main.Settings.FixedCode;
        var code = _receiver?.Code ?? (fixedCode.Length == 6 ? fixedCode : "");
        CodeText.Text = code.Length == 6 ? $"{code[..3]} {code[3..]}" : "--- ---";
        CodeText.Opacity = _receiver != null ? 1 : 0.45;
        StartStopButton.Content = L.T(_receiver != null ? "recv.stop" : "recv.start");
        StartStopButton.Classes.Set("accent", _receiver == null);
        StateText.Text = L.T(_receiver != null ? "recv.on" : "recv.off");
        StatePill.Classes.Set("ok", _receiver != null);
        StateDot.Classes.Set("on", _receiver != null);
        ReceivingChanged?.Invoke(_receiver != null);
    }

    private void ShowDestination()
    {
        var path = _main.Settings.DestinationRoot;
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
        _main.Settings.DestinationRoot = path;
        _main.Settings.Save();
        if (_receiver != null) _receiver.DestinationRoot = path;
        ShowDestination();
        return true;
    }

    public void EnsureReceiving()
    {
        if (_receiver == null) Toggle();
    }

    public async void Toggle()
    {
        if (_receiver != null)
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

        var receiver = new Receiver { DestinationRoot = settings.DestinationRoot, FixedCode = settings.FixedCode.Length == 6, Name = Platform.DeviceName };
        if (receiver.FixedCode) receiver.SetCode(settings.FixedCode);
        receiver.Log += m => Dispatcher.UIThread.Post(() => Log(m));
        receiver.CodeChanged += () => Dispatcher.UIThread.Post(ShowCode);
        receiver.SessionStarted += s => Dispatcher.UIThread.Post(() =>
        {
            RowFor(s);
            _main.Host.Notify(L.T("recv.notifyStartTitle"), L.T("recv.notifyStart", s.SenderName));
        });
        receiver.FileReceived += (_, path, size) =>
        {
            // Arquivos grandes aparecem um a um na atividade; os pequenos só no total, para não inundar.
            if (size >= LogFileMinSize) Dispatcher.UIThread.Post(() => Log(L.T("recv.fileDone", path, Format.Bytes(size))));
        };
        receiver.SessionEnded += (s, r) => Dispatcher.UIThread.Post(() => SessionEnded(s, r));
        try
        {
            receiver.Start();
        }
        catch (SocketException ex)
        {
            await _main.Ask(L.T("recv.portError", Protocol.DefaultPort, ex.Message), error: true);
            return;
        }
        _receiver = receiver;
        _multicast = Platform.AcquireMulticast?.Invoke();
        _main.Host.KeepAwake(true);
        _refresh.Start();
        ShowCode();
        ShowMachine();
        Log(L.T("recv.waiting", Protocol.DefaultPort));
    }

    public void StopReceiver()
    {
        if (_receiver == null) return;
        _receiver.Stop();
        _receiver = null;
        _refresh.Stop();
        _multicast?.Dispose();
        _multicast = null;
        _main.Host.KeepAwake(false);
        ShowCode();
    }

    /// <summary>Código fixo ligado/desligado pela tela de configurações.</summary>
    public void FixedCodeChanged(bool on)
    {
        _main.Settings.FixedCode = on ? (_receiver?.Code ?? Protocol.NewCode()) : "";
        _main.Settings.Save();
        if (_receiver != null) _receiver.FixedCode = on;
        ShowCode();
    }

    private SessionRow RowFor(ReceiveSession s)
    {
        if (!_rows.TryGetValue(s, out var row))
        {
            row = new SessionRow(s.SenderName);
            _rows[s] = row;
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

    private void SessionEnded(ReceiveSession s, TransferResult r)
    {
        // A cópia continua na lista com o resumo, em vez de sumir (arquivos pequenos terminam num piscar).
        var row = RowFor(s);
        _rows.Remove(s);
        row.Bar.Value = 1000;
        row.File.Text = "";
        row.File.IsVisible = false;
        row.Text.Text = L.T("recv.doneStatus", s.SenderName, r.FilesOk, Format.Bytes(s.Stats.BytesDone), s.Stats.SkippedFiles, r.FilesFailed);
        row.State.Text = L.T(r.FilesFailed > 0 ? "recv.withFailures" : "recv.done");
        row.Pill.Classes.Set("ok", r.FilesFailed == 0);
        _finished.Add(row);
        while (_finished.Count > KeepFinished)
        {
            Sessions.Children.Remove(_finished[0].Root);
            _finished.RemoveAt(0);
        }
        UpdateEmptyState();

        Log(row.Text.Text);
        foreach (var e in r.Errors) Log(L.T("send.failLine", e));
        _main.Host.Notify(L.T("recv.notifyDoneTitle"), r.FilesFailed > 0
            ? L.T("recv.notifyDoneFail", s.SenderName, r.FilesOk, r.FilesFailed)
            : L.T("recv.notifyDone", s.SenderName, r.FilesOk));
    }

    private void RefreshSessions()
    {
        if (_receiver == null) return;
        foreach (var s in _receiver.ActiveSessions)
        {
            var row = RowFor(s);
            var (desc, value) = ProgressText.Describe(s.Stats, row.Meter);
            row.Text.Text = desc;
            row.Bar.Value = value;
            row.File.Text = s.Stats.CurrentFile;
        }
    }

    private void Log(string line) => ProgressText.Append(LogBox, line);
}
