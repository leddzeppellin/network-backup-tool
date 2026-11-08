using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

public partial class ReceiveView : UserControl
{
    private readonly MainWindow _main;
    private Receiver? _receiver;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<ReceiveSession, (SpeedMeter Meter, TextBlock Text, ProgressBar Bar, Control Root)> _rows = new();

    public event Action<bool>? ReceivingChanged;

    public bool IsReceiving => _receiver != null;
    public bool Busy => _receiver?.ActiveSessions.Count > 0;
    public string CurrentCode => _receiver?.Code ?? "";

    public ReceiveView() : this(null!) { } // designer

    public ReceiveView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        if (main == null) return;

        DestText.Text = main.Settings.DestinationRoot;
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
        OpenButton.Click += (_, _) => Platform.OpenFolder(main.Settings.DestinationRoot);
        if (Platform.IsWindows)
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
        MachineText.Text = string.Join(Environment.NewLine, addrs.Prepend(Environment.MachineName));
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

    private async Task ChooseDestination()
    {
        var picked = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("recv.pickDest"),
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _main.Settings.DestinationRoot = path;
            _main.Settings.Save();
            DestText.Text = path;
            if (_receiver != null) _receiver.DestinationRoot = path;
        }
    }

    public void EnsureReceiving()
    {
        if (_receiver == null) Toggle();
    }

    public async void Toggle()
    {
        if (_receiver != null)
        {
            if (Busy && !await Dialogs.Show(_main, L.T("busy.stop"), yesNo: true)) return;
            StopReceiver();
            Log(L.T("recv.stopped"));
            return;
        }

        var settings = _main.Settings;
        try
        {
            Directory.CreateDirectory(settings.DestinationRoot);
        }
        catch (Exception ex)
        {
            await Dialogs.Show(_main, L.T("recv.destError", ex.Message), error: true);
            return;
        }

        var receiver = new Receiver { DestinationRoot = settings.DestinationRoot, FixedCode = settings.FixedCode.Length == 6 };
        if (receiver.FixedCode) receiver.SetCode(settings.FixedCode);
        receiver.Log += m => Dispatcher.UIThread.Post(() => Log(m));
        receiver.CodeChanged += () => Dispatcher.UIThread.Post(ShowCode);
        receiver.SessionStarted += s => Dispatcher.UIThread.Post(() =>
            _main.Notify(L.T("recv.notifyStartTitle"), L.T("recv.notifyStart", s.SenderName)));
        receiver.SessionEnded += (s, r) => Dispatcher.UIThread.Post(() => SessionEnded(s, r));
        try
        {
            receiver.Start();
        }
        catch (SocketException ex)
        {
            await Dialogs.Show(_main, L.T("recv.portError", Protocol.DefaultPort, ex.Message), error: true);
            return;
        }
        _receiver = receiver;
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

    private void SessionEnded(ReceiveSession s, TransferResult r)
    {
        if (_rows.Remove(s, out var row)) Sessions.Children.Remove(row.Root);
        NoSessions.IsVisible = _rows.Count == 0;
        var summary = L.T("recv.doneStatus", s.SenderName, r.FilesOk, Format.Bytes(s.Stats.BytesDone), s.Stats.SkippedFiles, r.FilesFailed);
        Log(summary);
        foreach (var e in r.Errors) Log(L.T("send.failLine", e));
        _main.Notify(L.T("recv.notifyDoneTitle"), r.FilesFailed > 0
            ? L.T("recv.notifyDoneFail", s.SenderName, r.FilesOk, r.FilesFailed)
            : L.T("recv.notifyDone", s.SenderName, r.FilesOk));
    }

    private void RefreshSessions()
    {
        if (_receiver == null) return;
        foreach (var s in _receiver.ActiveSessions)
        {
            if (!_rows.TryGetValue(s, out var row))
            {
                var name = new TextBlock { Text = s.SenderName, FontWeight = FontWeight.SemiBold };
                var text = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
                var bar = new ProgressBar { Maximum = 1000 };
                var root = new StackPanel { Spacing = 6, Children = { name, bar, text } };
                row = (new SpeedMeter(), text, bar, root);
                _rows[s] = row;
                Sessions.Children.Add(root);
            }
            var (desc, value) = ProgressText.Describe(s.Stats, row.Meter);
            row.Text.Text = desc;
            row.Bar.Value = value;
        }
        NoSessions.IsVisible = _rows.Count == 0;
    }

    private void Log(string line) => ProgressText.Append(LogBox, line);
}
