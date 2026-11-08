using System.Diagnostics;
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
    private readonly Button _send = new() { Text = "Enviar", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(16, 4, 16, 4) };
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
    private readonly Button _receive = new() { Text = "Iniciar recepção", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(16, 4, 16, 4) };
    private readonly ProgressBar _recvProgress = new() { Dock = DockStyle.Fill, Height = 22, Maximum = 1000 };
    private readonly Label _recvStatus = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly TextBox _recvLog = NewLog();
    private Receiver? _receiver;
    private readonly SpeedMeter _recvSpeed = new();

    public MainForm(bool startReceiving)
    {
        Text = "Network Backup Tool";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _tabs.TabPages.Add(BuildSendTab());
        _tabs.TabPages.Add(BuildReceiveTab());
        Controls.Add(_tabs);

        _connections.Value = Math.Clamp(_settings.Connections, 1, 16);
        _excludes.Text = _settings.Excludes;
        _target.Text = _settings.LastHost;
        _dest.Text = _settings.DestinationRoot;

        _timer.Tick += (_, _) => RefreshProgress();
        _timer.Start();

        if (startReceiving)
        {
            _tabs.SelectedIndex = 1;
            Shown += (_, _) => ToggleReceive();
        }
    }

    private static TextBox NewLog() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, WordWrap = false,
    };

    // ---------------------------------------------------------------- Enviar

    private TabPage BuildSendTab()
    {
        var page = new TabPage("Enviar  (PC do cliente)") { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 1. O que copiar
        var what = new GroupBox { Text = "1. O que copiar (arraste pastas e arquivos aqui)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var whatGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        whatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whatGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        whatGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        whatGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(NewButton("Pastas do usuário", (_, _) => AddUserFolders()));
        buttons.Controls.Add(NewButton("Adicionar pasta...", (_, _) => AddFolder()));
        buttons.Controls.Add(NewButton("Adicionar arquivos...", (_, _) => AddFiles()));
        buttons.Controls.Add(NewButton("Remover", (_, _) =>
        {
            foreach (var item in _sources.SelectedItems.Cast<object>().ToList()) _sources.Items.Remove(item);
        }));
        var excl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        excl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        excl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        excl.Controls.Add(new Label { Text = "Ignorar:", AutoSize = true, Anchor = AnchorStyles.Left });
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
        var where = new GroupBox { Text = "2. Para onde", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var whereGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whereGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var find = NewButton("Procurar na rede", async (_, _) => await FindReceivers());
        whereGrid.Controls.Add(new Label { Text = "Computador (nome ou IP):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        whereGrid.Controls.Add(_target, 1, 0);
        whereGrid.Controls.Add(find, 2, 0);
        var codeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        codeRow.Controls.Add(_code);
        codeRow.Controls.Add(new Label { Text = "    Conexões paralelas:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 0, 0) });
        codeRow.Controls.Add(_connections);
        whereGrid.Controls.Add(new Label { Text = "Código do receptor:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        whereGrid.Controls.Add(codeRow, 1, 1);
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
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites,
        ];
        foreach (var f in folders)
        {
            var p = Environment.GetFolderPath(f);
            if (Directory.Exists(p)) AddSource(p);
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) AddSource(downloads);
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Multiselect = true, Description = "Pastas para copiar" };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (var p in dlg.SelectedPaths) AddSource(p);
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Arquivos para copiar" };
        if (dlg.ShowDialog(this) == DialogResult.OK) foreach (var p in dlg.FileNames) AddSource(p);
    }

    private async Task FindReceivers()
    {
        AppendLog(_sendLog, "Procurando receptores na rede...");
        var list = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5));
        var typed = _target.Text;
        _target.Items.Clear();
        foreach (var r in list) _target.Items.Add(r);
        if (list.Count == 0)
        {
            AppendLog(_sendLog, "Nenhum receptor respondeu. Confira se a recepção está iniciada no outro PC, ou digite o IP dele.");
            _target.Text = typed;
            return;
        }
        AppendLog(_sendLog, $"Encontrado(s): {string.Join(", ", list)}");
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
        if (sources.Count == 0) { Warn("Adicione ao menos uma pasta ou arquivo."); return; }
        if (host.Length == 0) { Warn("Informe o computador que vai receber (use \"Procurar na rede\" ou digite o IP)."); return; }
        if (code.Length != 6) { Warn("Digite o código de 6 dígitos que aparece no computador que recebe."); return; }

        _settings.Connections = (int)_connections.Value;
        _settings.Excludes = _excludes.Text;
        _settings.LastHost = port == Protocol.DefaultPort ? host : $"{host}:{port}";
        _settings.Save();

        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        _send.Text = "Cancelar";
        _sendStats = null;
        _sendProgress.Value = 0;
        _sendSpeed.Reset();
        int connections = (int)_connections.Value;
        var excludes = _excludes.Text;
        var started = Stopwatch.StartNew();
        try
        {
            AppendLog(_sendLog, "Listando arquivos...");
            var scan = await Task.Run(() => Manifest.Scan(sources, excludes, ct,
                n => BeginInvoke(() => _sendStatus.Text = $"Listando... {n:N0} arquivos")), ct);
            foreach (var w in scan.Warnings) AppendLog(_sendLog, "Aviso: " + w);
            if (scan.CloudOnlySkipped > 0)
                AppendLog(_sendLog, $"{scan.CloudOnlySkipped:N0} arquivos estão só na nuvem (OneDrive) e não foram baixados.");
            AppendLog(_sendLog, $"{scan.FileCount:N0} arquivos, {Format.Bytes(scan.TotalBytes)}.");

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
            AppendLog(_sendLog, $"Concluído em {Format.Duration(elapsed)}: {result.FilesOk:N0} copiados, " +
                $"{stats.SkippedFiles:N0} já existiam, {result.FilesFailed:N0} com falha. " +
                $"Média {Format.Speed(stats.BytesDone / Math.Max(elapsed.TotalSeconds, 0.001))}.");
            foreach (var e in result.Errors) AppendLog(_sendLog, "  FALHA: " + e);
            if (result.FilesFailed > 0)
                Warn($"{result.FilesFailed:N0} arquivo(s) não foram copiados. Veja a lista no registro (e no relatório gravado no destino).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppendLog(_sendLog, "Cancelado. O que já foi copiado fica no destino; ao enviar de novo, continua de onde parou.");
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            AppendLog(_sendLog, $"Cancelado ({ex.GetType().Name}).");
        }
        catch (Exception ex)
        {
            AppendLog(_sendLog, "ERRO: " + ex.Message);
            Warn(ex is System.Net.Sockets.SocketException
                ? $"Não consegui conectar em {host}:{port}.\n\n{ex.Message}\n\nConfira se a recepção está iniciada no outro PC e se o firewall dele permite o Network Backup Tool."
                : ex.Message);
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            _send.Text = "Enviar";
            _sendCurrent.Text = "";
        }
    }

    // ---------------------------------------------------------------- Receber

    private TabPage BuildReceiveTab()
    {
        var page = new TabPage("Receber  (meu PC)") { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var destBox = new GroupBox { Text = "Salvar em (uma subpasta por computador de origem)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var destGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destGrid.Controls.Add(_dest, 0, 0);
        destGrid.Controls.Add(NewButton("Escolher...", (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _dest.Text, Description = "Pasta onde os backups serão gravados" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _dest.Text = dlg.SelectedPath;
        }), 1, 0);
        destGrid.Controls.Add(NewButton("Abrir pasta", (_, _) =>
        {
            Directory.CreateDirectory(_dest.Text);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dest.Text}\"") { UseShellExecute = true });
        }), 2, 0);
        destBox.Controls.Add(destGrid);

        var codeBox = new GroupBox { Text = "No PC do cliente, informe", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var codeFlow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        codeFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        codeFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var codeSide = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        codeSide.Controls.Add(_myAddress);
        codeSide.Controls.Add(NewButton("Gerar novo código", (_, _) =>
        {
            if (_receiver != null) { _receiver.SetCode(Protocol.NewCode()); ShowCode(); }
        }));
        codeSide.Controls.Add(NewButton("Liberar no firewall", (_, _) => AllowFirewall()));
        codeFlow.Controls.Add(_codeLabel, 0, 0);
        codeFlow.Controls.Add(codeSide, 1, 0);
        codeBox.Controls.Add(codeFlow);
        _codeLabel.Text = "------";
        ShowAddresses();

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
        grid.Controls.Add(action, 0, 2);
        grid.Controls.Add(_recvLog, 0, 3);
        page.Controls.Add(grid);
        return page;
    }

    private void ShowAddresses()
    {
        var lines = Discovery.LocalAddresses()
            .Select(a => $"  {a.Address}   ({a.Interface}{(a.SpeedBps > 0 ? $", {a.SpeedBps / 1_000_000:N0} Mbps" : "")})");
        _myAddress.Text = $"Computador: {Environment.MachineName}\nEndereços:\n{string.Join("\n", lines)}";
    }

    private void ShowCode() => _codeLabel.Text = _receiver?.Code ?? "------";

    private void ToggleReceive()
    {
        if (_receiver != null)
        {
            if (_receiver.ActiveSessions.Count > 0 &&
                MessageBox.Show(this, "Há cópia em andamento. Parar mesmo assim?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            _receiver.Stop();
            _receiver = null;
            _receive.Text = "Iniciar recepção";
            _dest.Enabled = true;
            ShowCode();
            AppendLog(_recvLog, "Recepção parada.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_dest.Text);
        }
        catch (Exception ex)
        {
            Warn($"Não consegui usar a pasta de destino:\n{ex.Message}");
            return;
        }
        _settings.DestinationRoot = _dest.Text;
        _settings.Save();

        var receiver = new Receiver { DestinationRoot = _dest.Text };
        receiver.Log += m => BeginInvoke(() => AppendLog(_recvLog, m));
        receiver.CodeChanged += () => BeginInvoke(ShowCode);
        receiver.SessionStarted += _ => BeginInvoke(() => _recvSpeed.Reset());
        receiver.SessionEnded += (s, r) => BeginInvoke(() =>
        {
            if (_receiver?.ActiveSessions.Count == 0)
            {
                _recvProgress.Value = r.FilesFailed == 0 ? 1000 : _recvProgress.Value;
                _recvStatus.Text = $"{s.SenderName}: concluído — {r.FilesOk:N0} copiados ({Format.Bytes(s.Stats.BytesDone)}), " +
                                   $"{s.Stats.SkippedFiles:N0} já existiam, {r.FilesFailed:N0} falha(s).";
            }
            foreach (var e in r.Errors) AppendLog(_recvLog, "  FALHA: " + e);
        });
        try
        {
            receiver.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Warn($"Não consegui abrir a porta {Protocol.DefaultPort}: {ex.Message}\n\nOutro Network Backup Tool já está recebendo neste PC?");
            return;
        }
        _receiver = receiver;
        _receive.Text = "Parar recepção";
        _dest.Enabled = false;
        ShowCode();
        ShowAddresses();
        AppendLog(_recvLog, $"Aguardando na porta {Protocol.DefaultPort}. No PC do cliente: aba Enviar → \"Procurar na rede\" → digite o código.");
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
            AppendLog(_recvLog, p?.ExitCode == 0 ? "Regra de firewall criada." : "O netsh não confirmou a regra de firewall.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AppendLog(_recvLog, "Liberação do firewall cancelada.");
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
            _recvStatus.Text = $"{sessions.Count} computadores enviando — {Format.Bytes(done)} de {Format.Bytes(total)} — {Format.Speed(speed)}";
        }
        else if (_receiver != null && _recvStatus.Text.Length == 0)
        {
            _recvStatus.Text = "Aguardando conexão...";
        }
    }

    private static void UpdateProgress(TransferStats s, SpeedMeter meter, ProgressBar bar, Label label)
    {
        bar.Maximum = 1000;
        long done = s.BytesDone, total = s.TotalBytes;
        bar.Value = total > 0 ? (int)Math.Min(1000, 1000 * done / total) : (s.TotalFiles > 0 && s.FilesDone + s.FilesFailed >= s.TotalFiles ? 1000 : 0);
        var speed = meter.Sample(done);
        var eta = speed > 1 && total > done ? $" — faltam {Format.Duration(TimeSpan.FromSeconds((total - done) / speed))}" : "";
        label.Text = $"{s.FilesDone + s.FilesFailed:N0} de {s.TotalFiles:N0} arquivos — {Format.Bytes(done)} de {Format.Bytes(total)} — {Format.Speed(speed)}{eta}" +
                     (s.FilesFailed > 0 ? $" — {s.FilesFailed:N0} falha(s)" : "");
    }

    private static void AppendLog(TextBox box, string line)
    {
        if (box.TextLength > 2_000_000) box.Clear();
        box.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool busy = _sendCts != null || _receiver?.ActiveSessions.Count > 0;
        if (busy && MessageBox.Show(this, "Há cópia em andamento. Fechar mesmo assim?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _sendCts?.Cancel();
        _receiver?.Stop();
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
