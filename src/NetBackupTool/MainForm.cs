using System.Diagnostics;
using Microsoft.Win32;
using Nbt.Core;

namespace NetBackupTool;

public sealed class MainForm : Form
{
    private readonly Settings _settings = Settings.Load();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    // Enviar
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, AllowDrop = true, IntegralHeight = false };
    private readonly TextBox _excludes = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _target = new() { Dock = DockStyle.Fill };
    private readonly TextBox _code = new() { Width = 110, MaxLength = 6, Font = new Font("Segoe UI", 14f) };
    private readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 16, Width = 60 };
    private readonly Button _send = new() { Text = L.T("send.send"), AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(16, 4, 16, 4) };
    private readonly ProgressBar _sendProgress = new() { Dock = DockStyle.Fill, Height = 22, Maximum = 1000 };
    private readonly Label _sendStatus = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _sendCurrent = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Height = 20 };
    private readonly TextBox _sendLog = NewLog();
    private CancellationTokenSource? _sendCts;
    private TransferStats? _sendStats;
    private readonly SpeedMeter _sendSpeed = new();

    // Receber
    private readonly TextBox _dest = new() { Dock = DockStyle.Fill };
    private readonly Label _codeLabel = new() { AutoSize = true, Font = new Font("Consolas", 40f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 90, 160) };
    private readonly Label _myAddress = new() { AutoSize = true, Font = new Font("Segoe UI", 10f) };
    private readonly Button _receive = new() { Text = L.T("recv.start"), AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(16, 4, 16, 4) };
    private readonly ProgressBar _recvProgress = new() { Dock = DockStyle.Fill, Height = 22, Maximum = 1000 };
    private readonly Label _recvStatus = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly TextBox _recvLog = NewLog();
    private Receiver? _receiver;
    private readonly SpeedMeter _recvSpeed = new();

    private List<string>? _userFolders;

    // Modo persistente
    private readonly CheckBox _chkTray = new() { Text = L.T("recv.chkTray"), AutoSize = true };
    private readonly CheckBox _chkAutostart = new() { Text = L.T("recv.chkAutostart"), AutoSize = true };
    private readonly CheckBox _chkFixed = new() { Text = L.T("recv.chkFixed"), AutoSize = true };
    private readonly NotifyIcon _tray = new() { Text = "Network Backup Tool" };
    private readonly ToolStripMenuItem _trayToggle = new(L.T("recv.start"));
    private bool _startHidden;
    private bool _exiting;
    private bool _trayHintShown;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetBackupTool";

    public MainForm(bool startReceiving, Handoff? handoff = null, bool startHidden = false, EventWaitHandle? showSignal = null)
    {
        Text = ShadowCopies.IsElevated ? L.T("app.admin") : "Network Backup Tool";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _tabs.TabPages.Add(BuildSendTab());
        _tabs.TabPages.Add(BuildReceiveTab());
        Controls.Add(_tabs);
        var menu = BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;

        _connections.Value = Math.Clamp(_settings.Connections, 1, 16);
        _excludes.Text = _settings.Excludes;
        _target.Text = _settings.LastHost;
        _dest.Text = _settings.DestinationRoot;
        if (handoff != null)
        {
            // Reaberto como administrador: mantém a lista e as pastas do usuário original
            // (no UAC o técnico pode ter entrado com outra conta, de outro perfil).
            foreach (var p in handoff.Sources) AddSource(p);
            _userFolders = handoff.UserFolders;
            _excludes.Text = handoff.Excludes;
            _connections.Value = Math.Clamp(handoff.Connections, 1, 16);
            _target.Text = handoff.Target;
        }

        _timer.Tick += (_, _) => RefreshProgress();
        _timer.Start();

        SetupTray();
        _startHidden = startHidden && _settings.KeepInTray;
        if (showSignal != null)
        {
            // Outra execução do .exe pede para mostrar esta janela.
            var t = new Thread(() =>
            {
                while (showSignal.WaitOne())
                    if (IsHandleCreated) BeginInvoke(ShowWindow);
            }) { IsBackground = true };
            t.Start();
        }

        if (startReceiving)
        {
            _tabs.SelectedIndex = 1;
            HandleCreated += (_, _) => BeginInvoke(() => { if (_receiver == null) ToggleReceive(); });
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        // Iniciado com o Windows: cria a janela mas a mantém escondida, só com o ícone da bandeja.
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private void SetupTray()
    {
        _tray.Icon = Icon;
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("tray.open"), null, (_, _) => ShowWindow());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => { _exiting = true; Close(); });
        _trayToggle.Click += (_, _) => ToggleReceive();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.Visible = _settings.KeepInTray;
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void Notify(string title, string text)
    {
        if (_tray.Visible && !Visible) _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    private static TextBox NewLog() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, WordWrap = false,
    };

    // ---------------------------------------------------------------- Enviar

    private TabPage BuildSendTab()
    {
        var page = new TabPage(L.T("tab.send")) { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 1. O que copiar
        var what = new GroupBox { Text = L.T("send.what"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        var whatGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        whatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        whatGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        whatGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(NewButton(L.T("send.userFolders"), (_, _) => AddUserFolders()));
        buttons.Controls.Add(NewButton(L.T("send.addFolder"), (_, _) => AddFolder()));
        buttons.Controls.Add(NewButton(L.T("send.addFiles"), (_, _) => AddFiles()));
        buttons.Controls.Add(NewButton(L.T("send.remove"), (_, _) =>
        {
            foreach (var item in _sources.SelectedItems.Cast<object>().ToList()) _sources.Items.Remove(item);
        }));
        var excl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        excl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        excl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        excl.Controls.Add(new Label { Text = L.T("send.ignore"), AutoSize = true, Anchor = AnchorStyles.Left });
        excl.Controls.Add(_excludes);
        whatGrid.Controls.Add(_sources, 0, 0);
        whatGrid.Controls.Add(buttons, 1, 0);
        whatGrid.Controls.Add(excl, 0, 1);
        whatGrid.SetColumnSpan(excl, 2);
        what.Controls.Add(whatGrid);

        _sources.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _sources.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) foreach (var p in paths) AddSource(p);
        };
        _sources.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
                foreach (var item in _sources.SelectedItems.Cast<object>().ToList()) _sources.Items.Remove(item);
        };

        // 2. Para onde
        var where = new GroupBox { Text = L.T("send.where"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var whereGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var find = NewButton(L.T("send.find"), async (_, _) => await FindReceivers());
        whereGrid.Controls.Add(new Label { Text = L.T("send.computer"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        whereGrid.Controls.Add(_target, 1, 0);
        whereGrid.Controls.Add(find, 2, 0);
        var codeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        codeRow.Controls.Add(_code);
        codeRow.Controls.Add(new Label { Text = L.T("send.connections"), AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 0, 0) });
        codeRow.Controls.Add(_connections);
        whereGrid.Controls.Add(new Label { Text = L.T("send.code"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        whereGrid.Controls.Add(codeRow, 1, 1);
        var vss = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };
        if (ShadowCopies.IsElevated)
        {
            vss.Text = L.T("send.adminOn");
            vss.LinkArea = new LinkArea(0, 0);
        }
        else
        {
            var reopen = L.T("send.reopenAdmin");
            vss.Text = L.T("send.adminOff") + "  " + reopen;
            vss.LinkArea = new LinkArea(vss.Text.Length - reopen.Length, reopen.Length);
            vss.LinkClicked += (_, _) => RestartElevated();
        }
        whereGrid.Controls.Add(vss, 0, 2);
        whereGrid.SetColumnSpan(vss, 3);
        where.Controls.Add(whereGrid);
        _code.KeyPress += (_, e) => e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);

        // 3. Ação e progresso
        var action = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        action.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        action.Controls.Add(_send, 0, 0);
        action.SetRowSpan(_send, 3);
        action.Controls.Add(_sendProgress, 1, 0);
        action.Controls.Add(_sendStatus, 1, 1);
        action.Controls.Add(_sendCurrent, 1, 2);
        _send.Click += async (_, _) => await ToggleSend();

        grid.Controls.Add(what, 0, 0);
        grid.Controls.Add(where, 0, 1);
        grid.Controls.Add(action, 0, 2);
        grid.Controls.Add(_sendLog, 0, 3);
        page.Controls.Add(grid);
        return page;
    }

    private void RestartElevated()
    {
        if (_sendCts != null || _receiver?.ActiveSessions.Count > 0)
        {
            Warn(L.T("busy.wait"));
            return;
        }
        var file = new Handoff
        {
            Sources = _sources.Items.Cast<string>().ToList(),
            UserFolders = _userFolders ?? CurrentUserFolders(),
            Excludes = _excludes.Text,
            Connections = (int)_connections.Value,
            Target = _target.Text,
        }.Write();
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true };
            psi.ArgumentList.Add(Handoff.Arg);
            psi.ArgumentList.Add(file);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            File.Delete(file);
            return; // UAC recusado
        }
        _receiver?.Stop();
        _receiver = null;
        Application.Exit();
    }

    private static Button NewButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(150, 0), Margin = new Padding(4, 2, 0, 2) };
        b.Click += onClick;
        return b;
    }

    private void AddSource(string path)
    {
        if (!_sources.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _sources.Items.Add(path);
    }

    private void AddUserFolders()
    {
        foreach (var p in _userFolders ?? CurrentUserFolders())
            if (Directory.Exists(p)) AddSource(p);
    }

    private static List<string> CurrentUserFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites,
        ];
        var list = folders.Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToList();
        list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return list;
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Multiselect = true, Description = L.T("send.pickFolders") };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (var p in dlg.SelectedPaths) AddSource(p);
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("send.pickFiles") };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (var p in dlg.FileNames) AddSource(p);
    }

    private async Task FindReceivers()
    {
        AppendLog(_sendLog, L.T("send.searching"));
        var list = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5));
        var typed = _target.Text;
        _target.Items.Clear();
        foreach (var r in list) _target.Items.Add(r);
        if (list.Count == 0)
        {
            AppendLog(_sendLog, L.T("send.noneFound"));
            _target.Text = typed;
            return;
        }
        AppendLog(_sendLog, L.T("send.found", string.Join(", ", list)));
        _target.SelectedIndex = 0;
        _target.DroppedDown = list.Count > 1;
    }

    private (string Host, int Port) ParseTarget()
    {
        if (_target.SelectedItem is DiscoveredReceiver r && _target.Text == r.ToString()) return (r.Address, r.Port);
        var text = _target.Text.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.Count(c => c == ':') == 1 && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);
        return (text, Protocol.DefaultPort);
    }

    private async Task ToggleSend()
    {
        if (_sendCts != null)
        {
            _sendCts.Cancel();
            return;
        }
        var sources = _sources.Items.Cast<string>().ToList();
        var (host, port) = ParseTarget();
        var code = _code.Text.Trim();
        if (sources.Count == 0) { Warn(L.T("send.needSource")); return; }
        if (host.Length == 0) { Warn(L.T("send.needTarget")); return; }
        if (code.Length != 6) { Warn(L.T("send.needCode")); return; }

        _settings.Connections = (int)_connections.Value;
        _settings.Excludes = _excludes.Text;
        _settings.LastHost = port == Protocol.DefaultPort ? host : $"{host}:{port}";
        _settings.Save();

        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        _send.Text = L.T("send.cancel");
        _sendStats = null;
        _sendProgress.Value = 0;
        _sendSpeed.Reset();
        int connections = (int)_connections.Value;
        var excludes = _excludes.Text;
        var started = Stopwatch.StartNew();
        try
        {
            AppendLog(_sendLog, L.T("send.listing"));
            var scan = await Task.Run(() => Manifest.Scan(sources, excludes, ct,
                n => BeginInvoke(() => _sendStatus.Text = L.T("send.listingN", n))), ct);
            foreach (var w in scan.Warnings) AppendLog(_sendLog, L.T("send.warning", w));
            if (scan.CloudOnlySkipped > 0)
                AppendLog(_sendLog, L.T("send.cloudOnly", scan.CloudOnlySkipped));
            AppendLog(_sendLog, L.T("send.scanned", scan.FileCount, Format.Bytes(scan.TotalBytes)));

            var stats = new TransferStats();
            _sendStats = stats;
            var sender = new Sender
            {
                Host = host, Port = port, Code = code, Connections = connections,
                Log = m => BeginInvoke(() => AppendLog(_sendLog, m)),
            };
            var result = await Task.Run(() => sender.SendAsync(scan, stats, ct), ct);
            RefreshProgress();
            var elapsed = started.Elapsed;
            AppendLog(_sendLog, L.T("send.done", Format.Duration(elapsed), result.FilesOk, stats.SkippedFiles, result.FilesFailed,
                Format.Speed(stats.BytesDone / Math.Max(elapsed.TotalSeconds, 0.001))));
            foreach (var e in result.Errors) AppendLog(_sendLog, L.T("send.failLine", e));
            if (result.FilesFailed > 0)
                Warn(L.T("send.someFailed", result.FilesFailed));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppendLog(_sendLog, L.T("send.canceled"));
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            AppendLog(_sendLog, L.T("send.canceledEx", ex.GetType().Name));
        }
        catch (Exception ex)
        {
            AppendLog(_sendLog, L.T("send.error", ex.Message));
            Warn(ex is System.Net.Sockets.SocketException
                ? L.T("send.connectFailed", host, port, ex.Message)
                : ex.Message);
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            _send.Text = L.T("send.send");
            _sendCurrent.Text = "";
        }
    }

    // ---------------------------------------------------------------- Receber

    private TabPage BuildReceiveTab()
    {
        var page = new TabPage(L.T("tab.receive")) { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var destBox = new GroupBox { Text = L.T("recv.saveIn"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var destGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destGrid.Controls.Add(_dest, 0, 0);
        destGrid.Controls.Add(NewButton(L.T("recv.choose"), (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _dest.Text, Description = L.T("recv.pickDest") };
            if (dlg.ShowDialog(this) == DialogResult.OK) _dest.Text = dlg.SelectedPath;
        }), 1, 0);
        destGrid.Controls.Add(NewButton(L.T("recv.openFolder"), (_, _) => OpenDestination()), 2, 0);
        destBox.Controls.Add(destGrid);

        var codeBox = new GroupBox { Text = L.T("recv.tellClient"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var codeFlow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        codeFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        codeFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var codeSide = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        codeSide.Controls.Add(_myAddress);
        codeSide.Controls.Add(NewButton(L.T("recv.newCode"), (_, _) =>
        {
            var code = Protocol.NewCode();
            _receiver?.SetCode(code);
            if (_chkFixed.Checked) { _settings.FixedCode = code; _settings.Save(); }
            ShowCode();
        }));
        codeSide.Controls.Add(NewButton(L.T("recv.firewall"), (_, _) => AllowFirewall()));
        codeFlow.Controls.Add(_codeLabel, 0, 0);
        codeFlow.Controls.Add(codeSide, 1, 0);
        codeBox.Controls.Add(codeFlow);
        ShowAddresses();

        var persist = new GroupBox { Text = L.T("recv.persist"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var persistFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        persistFlow.Controls.Add(_chkTray);
        persistFlow.Controls.Add(_chkAutostart);
        persistFlow.Controls.Add(_chkFixed);
        persist.Controls.Add(persistFlow);
        _chkTray.Checked = _settings.KeepInTray;
        _chkAutostart.Checked = IsAutostart();
        _chkFixed.Checked = _settings.FixedCode.Length == 6;
        ShowCode();
        _chkTray.CheckedChanged += (_, _) =>
        {
            _settings.KeepInTray = _chkTray.Checked;
            _settings.Save();
            _tray.Visible = _chkTray.Checked;
            if (!_chkTray.Checked && _chkAutostart.Checked) _chkAutostart.Checked = false;
        };
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            SetAutostart(_chkAutostart.Checked);
            if (_chkAutostart.Checked && !_chkTray.Checked) _chkTray.Checked = true;
        };
        _chkFixed.CheckedChanged += (_, _) =>
        {
            _settings.FixedCode = _chkFixed.Checked ? (_receiver?.Code ?? Protocol.NewCode()) : "";
            _settings.Save();
            if (_receiver != null) _receiver.FixedCode = _chkFixed.Checked;
            ShowCode();
        };

        var action = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        action.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        action.Controls.Add(_receive, 0, 0);
        action.SetRowSpan(_receive, 2);
        action.Controls.Add(_recvProgress, 1, 0);
        action.Controls.Add(_recvStatus, 1, 1);
        _receive.Click += (_, _) => ToggleReceive();

        grid.Controls.Add(destBox, 0, 0);
        grid.Controls.Add(codeBox, 0, 1);
        grid.Controls.Add(persist, 0, 2);
        grid.Controls.Add(action, 0, 3);
        grid.Controls.Add(_recvLog, 0, 4);
        page.Controls.Add(grid);
        return page;
    }

    private void ShowAddresses()
    {
        var lines = Discovery.LocalAddresses()
            .Select(a => $"  {a.Address}   ({a.Interface}{(a.SpeedBps > 0 ? $", {a.SpeedBps / 1_000_000:N0} Mbps" : "")})");
        _myAddress.Text = L.T("recv.addresses", Environment.MachineName, string.Join("\n", lines));
    }

    private void ShowCode()
    {
        _codeLabel.Text = _receiver?.Code ?? (_settings.FixedCode.Length == 6 ? _settings.FixedCode : "------");
        _codeLabel.ForeColor = _receiver != null ? Color.FromArgb(0, 90, 160) : SystemColors.GrayText;
        var tip = _receiver != null ? L.T("tray.receiving", _receiver.Code) : L.T("tray.stopped");
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void SetAutostart(bool on)
    {
        // HKCU\...\Run: só para este usuário, sem precisar de administrador.
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" receber --bandeja");
        else key.DeleteValue(RunValue, false);
    }

    private void ToggleReceive()
    {
        if (_receiver != null)
        {
            if (_receiver.ActiveSessions.Count > 0 &&
                MessageBox.Show(this, L.T("busy.stop"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            _receiver.Stop();
            _receiver = null;
            _receive.Text = L.T("recv.start");
            _trayToggle.Text = L.T("recv.start");
            _dest.Enabled = true;
            ShowCode();
            AppendLog(_recvLog, L.T("recv.stopped"));
            return;
        }

        try
        {
            Directory.CreateDirectory(_dest.Text);
        }
        catch (Exception ex)
        {
            Warn(L.T("recv.destError", ex.Message));
            return;
        }
        _settings.DestinationRoot = _dest.Text;
        _settings.Save();

        var receiver = new Receiver { DestinationRoot = _dest.Text, FixedCode = _settings.FixedCode.Length == 6 };
        if (receiver.FixedCode) receiver.SetCode(_settings.FixedCode);
        receiver.Log += m => BeginInvoke(() => AppendLog(_recvLog, m));
        receiver.CodeChanged += () => BeginInvoke(ShowCode);
        receiver.SessionStarted += s => BeginInvoke(() =>
        {
            _recvSpeed.Reset();
            Notify(L.T("recv.notifyStartTitle"), L.T("recv.notifyStart", s.SenderName));
        });
        receiver.SessionEnded += (s, r) => BeginInvoke(() =>
        {
            if (_receiver?.ActiveSessions.Count == 0)
            {
                _recvProgress.Value = r.FilesFailed == 0 ? 1000 : _recvProgress.Value;
                _recvStatus.Text = L.T("recv.doneStatus", s.SenderName, r.FilesOk, Format.Bytes(s.Stats.BytesDone), s.Stats.SkippedFiles, r.FilesFailed);
            }
            foreach (var e in r.Errors) AppendLog(_recvLog, L.T("send.failLine", e));
            Notify(L.T("recv.notifyDoneTitle"), r.FilesFailed > 0 ? L.T("recv.notifyDoneFail", s.SenderName, r.FilesOk, r.FilesFailed) : L.T("recv.notifyDone", s.SenderName, r.FilesOk));
        });
        try
        {
            receiver.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Warn(L.T("recv.portError", Protocol.DefaultPort, ex.Message));
            return;
        }
        _receiver = receiver;
        _receive.Text = L.T("recv.stop");
        _trayToggle.Text = L.T("recv.stop");
        _dest.Enabled = false;
        ShowCode();
        ShowAddresses();
        AppendLog(_recvLog, L.T("recv.waiting", Protocol.DefaultPort));
    }

    private void AllowFirewall()
    {
        var exe = Environment.ProcessPath!;
        // Regra de entrada só para este executável, em todos os perfis (rede de cliente costuma estar como "Pública").
        var args = $"advfirewall firewall add rule name=\"Network Backup Tool\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit();
            AppendLog(_recvLog, p?.ExitCode == 0 ? L.T("recv.fwCreated") : L.T("recv.fwNotConfirmed"));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AppendLog(_recvLog, L.T("recv.fwCanceled"));
        }
    }

    // ---------------------------------------------------------------- comum

    private void RefreshProgress()
    {
        if (_sendStats is { } s)
        {
            UpdateProgress(s, _sendSpeed, _sendProgress, _sendStatus);
            _sendCurrent.Text = _sendCts != null ? s.CurrentFile : "";
        }

        var sessions = _receiver?.ActiveSessions ?? [];
        if (sessions.Count == 1)
        {
            var one = sessions.First();
            UpdateProgress(one.Stats, _recvSpeed, _recvProgress, _recvStatus);
            _recvStatus.Text = $"{one.SenderName}: {_recvStatus.Text}";
        }
        else if (sessions.Count > 1)
        {
            long done = sessions.Sum(x => x.Stats.BytesDone), total = sessions.Sum(x => x.Stats.TotalBytes);
            var speed = _recvSpeed.Sample(done);
            _recvProgress.Value = total > 0 ? (int)(1000 * done / total) : 0;
            _recvStatus.Text = L.T("recv.many", sessions.Count, Format.Bytes(done), Format.Bytes(total), Format.Speed(speed));
        }
        else if (_receiver != null && _recvStatus.Text.Length == 0)
        {
            _recvStatus.Text = L.T("recv.waitingConn");
        }
    }

    private static void UpdateProgress(TransferStats s, SpeedMeter meter, ProgressBar bar, Label label)
    {
        bar.Maximum = 1000;
        long done = s.BytesDone, total = s.TotalBytes;
        bar.Value = total > 0 ? (int)Math.Min(1000, 1000 * done / total) : (s.TotalFiles > 0 && s.FilesDone + s.FilesFailed >= s.TotalFiles ? 1000 : 0);
        var speed = meter.Sample(done);
        var eta = speed > 1 && total > done ? L.T("progress.eta", Format.Duration(TimeSpan.FromSeconds((total - done) / speed))) : "";
        label.Text = L.T("progress", s.FilesDone + s.FilesFailed, s.TotalFiles, Format.Bytes(done), Format.Bytes(total), Format.Speed(speed)) + eta +
                     (s.FilesFailed > 0 ? L.T("progress.failed", s.FilesFailed) : "");
    }

    private static void AppendLog(TextBox box, string line)
    {
        if (box.TextLength > 2_000_000) box.Clear();
        box.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    // ---------------------------------------------------------------- menus

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        var lang = new ToolStripMenuItem(L.T("menu.language"));
        (string Code, string Name)[] options = [("auto", L.T("menu.langAuto")), ("pt", "Português"), ("en", "English"), ("es", "Español")];
        foreach (var (code, name) in options)
        {
            var item = new ToolStripMenuItem(name) { Checked = _settings.Language == code };
            item.Click += (_, _) => ChangeLanguage(code);
            lang.DropDownItems.Add(item);
        }

        var help = new ToolStripMenuItem(L.T("menu.help"));
        help.DropDownItems.Add(L.T("menu.howTo"), null, (_, _) =>
            MessageBox.Show(this, L.T("howto.text"), L.T("howto.title"), MessageBoxButtons.OK, MessageBoxIcon.Information));
        help.DropDownItems.Add(L.T("menu.update"), null, async (_, _) => await CheckForUpdates());
        help.DropDownItems.Add(L.T("menu.reports"), null, (_, _) => OpenDestination());
        help.DropDownItems.Add(L.T("menu.project"), null, (_, _) => Updater.OpenUrl(Updater.ProjectUrl));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(L.T("menu.about"), null, (_, _) =>
        {
            using var about = new AboutForm(Icon!);
            about.ShowDialog(this);
        });

        menu.Items.Add(lang);
        menu.Items.Add(help);
        return menu;
    }

    private void OpenDestination()
    {
        try
        {
            Directory.CreateDirectory(_dest.Text);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dest.Text}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn(L.T("recv.destError", ex.Message));
        }
    }

    private bool Busy => _sendCts != null || _receiver?.ActiveSessions.Count > 0;

    private void ChangeLanguage(string code)
    {
        if (_settings.Language == code) return;
        _settings.Language = code;
        _settings.Save();
        L.Set(code);
        foreach (ToolStripMenuItem item in ((ToolStripMenuItem)MainMenuStrip!.Items[0]).DropDownItems) item.Checked = false;
        ((ToolStripMenuItem)((ToolStripMenuItem)MainMenuStrip.Items[0]).DropDownItems[Array.IndexOf(["auto", "pt", "en", "es"], code)]).Checked = true;
        if (Busy)
        {
            Warn(L.T("busy.wait"));
            return;
        }
        if (MessageBox.Show(this, L.T("menu.langChanged"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Restart();
    }

    /// <summary>Reabre o programa (troca de idioma, atualização), mantendo a recepção se estava ligada.</summary>
    private void Restart()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--reiniciar");
        if (_receiver != null) psi.ArgumentList.Add("receber");
        _receiver?.Stop();
        _receiver = null;
        Process.Start(psi);
        _exiting = true;
        Close();
    }

    private async Task CheckForUpdates()
    {
        if (Busy)
        {
            Warn(L.T("busy.wait"));
            return;
        }
        var title = L.T("upd.title");
        ReleaseInfo latest;
        try
        {
            UseWaitCursor = true;
            latest = await Updater.GetLatestAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("upd.failed", ex.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        var current = Updater.Current;
        if (latest.Version <= current)
        {
            MessageBox.Show(this, L.T("upd.upToDate", current.ToString(3)), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (latest.AssetUrl == null)
        {
            MessageBox.Show(this, L.T("upd.noAsset", latest.Version.ToString(3)), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, L.T("upd.available", latest.Version.ToString(3), current.ToString(3)), title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var newFile = Environment.ProcessPath + ".new";
        var oldTitle = Text;
        try
        {
            UseWaitCursor = true;
            var progress = new Progress<int>(p => Text = L.T("upd.downloading", latest.Version.ToString(3), p));
            await Updater.DownloadAsync(latest, newFile, progress, CancellationToken.None);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        catch (Exception ex)
        {
            // Pasta sem permissão de escrita (ex.: Arquivos de Programas) ou rede fora do ar.
            if (MessageBox.Show(this, L.T("upd.cantReplace", ex.Message), title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Updater.OpenUrl(latest.HtmlUrl);
            return;
        }
        finally
        {
            UseWaitCursor = false;
            Text = oldTitle;
        }
        try
        {
            Updater.StartFinish(newFile, _receiver != null);
        }
        catch (Exception ex)
        {
            if (MessageBox.Show(this, L.T("upd.cantReplace", ex.Message), title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Updater.OpenUrl(latest.HtmlUrl);
            return;
        }
        _receiver?.Stop();
        _receiver = null;
        _exiting = true;
        Close();
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Modo persistente: o X só esconde a janela; a recepção segue com o ícone perto do relógio.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _settings.KeepInTray && _receiver != null && _sendCts == null)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(5000, L.T("tray.keepsTitle"), L.T("tray.keepsText"), ToolTipIcon.Info);
            }
            return;
        }
        bool busy = _sendCts != null || _receiver?.ActiveSessions.Count > 0;
        if (busy && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, L.T("busy.close"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
            _exiting = false;
            return;
        }
        _sendCts?.Cancel();
        _receiver?.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    /// <summary>Velocidade média dos últimos ~3 s, para o número não ficar pulando.</summary>
    private sealed class SpeedMeter
    {
        private readonly Queue<(double T, long Bytes)> _samples = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Reset() => _samples.Clear();

        public double Sample(long bytes)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_samples.Count > 0 && bytes < _samples.Last().Bytes) _samples.Clear();
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().T > 3) _samples.Dequeue();
            var first = _samples.Peek();
            return now - first.T > 0.2 ? (bytes - first.Bytes) / (now - first.T) : 0;
        }
    }
}
