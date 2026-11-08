using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

public partial class SendView : UserControl
{
    private readonly MainWindow _main;
    private readonly ObservableCollection<string> _sources = new();
    private List<string>? _userFolders;
    private CancellationTokenSource? _cts;
    private TransferStats? _stats;
    private readonly SpeedMeter _speed = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _discover = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, DiscoveredReceiver> _found = new();
    private DiscoveredReceiver? _selected;
    private bool _discovering;
    private bool _searchedOnce;

    public bool Busy => _cts != null;

    public SendView() : this(null!, null) { } // designer

    public SendView(MainWindow main, Handoff? handoff)
    {
        _main = main;
        InitializeComponent();
        SourceList.ItemsSource = _sources;
        _sources.CollectionChanged += (_, _) => DropHint.IsVisible = _sources.Count == 0;

        UserFoldersButton.Click += (_, _) => { foreach (var p in _userFolders ?? Platform.UserFolders()) if (Directory.Exists(p)) AddSource(p); };
        AddFolderButton.Click += async (_, _) => await PickFolders();
        AddFilesButton.Click += async (_, _) => await PickFiles();
        WhatCard.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        WhatCard.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var item in e.DataTransfer.TryGetFiles() ?? []) if (item.TryGetLocalPath() is { } p) AddSource(p);
        });

        HostBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(HostBox.Text)) Select(null);
        };
        SendButton.Click += async (_, _) => await ToggleSend();

        if (Platform.IsWindows)
        {
            AdminHint.IsVisible = true;
            bool admin = ShadowCopies.IsElevated;
            AdminText.Text = L.T(admin ? "send.adminOn" : "send.adminOff");
            ReopenAdmin.IsVisible = !admin;
            ReopenAdmin.Click += (_, _) => ReopenElevated();
        }

        if (main != null) HostBox.Text = main.Settings.LastHost;
        if (handoff != null)
        {
            // Reaberto como administrador: mantém a lista e as pastas do usuário original
            // (no UAC o técnico pode ter entrado com outra conta, de outro perfil).
            foreach (var p in handoff.Sources) AddSource(p);
            _userFolders = handoff.UserFolders;
            HostBox.Text = handoff.Target;
        }

        _refresh.Tick += (_, _) => RefreshProgress();
        _discover.Tick += async (_, _) => await Discover();
        AttachedToVisualTree += async (_, _) =>
        {
            _discover.Start();
            await Discover();
        };
        DetachedFromVisualTree += (_, _) => _discover.Stop();
    }

    private void AddSource(string path)
    {
        if (!_sources.Contains(path, StringComparer.OrdinalIgnoreCase)) _sources.Add(path);
    }

    private void RemoveSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) _sources.Remove(path);
    }

    private async Task PickFolders()
    {
        var picked = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = true, Title = L.T("send.pickFolders"),
        });
        foreach (var f in picked) if (f.TryGetLocalPath() is { } p) AddSource(p);
    }

    private async Task PickFiles()
    {
        var picked = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true, Title = L.T("send.pickFiles"),
        });
        foreach (var f in picked) if (f.TryGetLocalPath() is { } p) AddSource(p);
    }

    // ---------------------------------------------------------------- receptores na rede

    /// <summary>Procura receptores sozinho a cada poucos segundos enquanto a tela está aberta.</summary>
    private async Task Discover()
    {
        if (_discovering || Busy) return;
        _discovering = true;
        try
        {
            // Este próprio computador (se também estiver recebendo) não entra na lista.
            var list = (await Discovery.FindAsync(TimeSpan.FromSeconds(1.2)))
                .Where(r => !r.Name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)).ToList();
            var current = list.ToDictionary(r => r.Address);
            bool changed = current.Count != _found.Count || current.Keys.Any(k => !_found.ContainsKey(k));
            if (!changed && _searchedOnce) return;
            _searchedOnce = true;
            _found.Clear();
            foreach (var (k, v) in current) _found[k] = v;
            if (_selected != null && !_found.ContainsKey(_selected.Address)) _selected = null;
            // Um só receptor na rede e nada digitado: já fica escolhido.
            if (_selected == null && _found.Count == 1 && string.IsNullOrWhiteSpace(HostBox.Text)) _selected = _found.Values.First();
            RenderReceivers();
        }
        finally
        {
            _discovering = false;
        }
    }

    private void RenderReceivers()
    {
        Receivers.Children.Clear();
        foreach (var r in _found.Values.OrderBy(r => r.Name))
        {
            var chip = new ToggleButton
            {
                Classes = { "chip" },
                IsChecked = _selected?.Address == r.Address,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new PathIcon { Data = Res.Get<Geometry>("IconComputer"), Width = 14, Height = 14 },
                        new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = r.Address, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            chip.Click += (_, _) => Select(chip.IsChecked == true ? r : null);
            Receivers.Children.Add(chip);
        }
        DiscoveryText.Text = L.T(_found.Count == 0 ? "send.noneYet" : "send.foundLabel");
    }

    private void Select(DiscoveredReceiver? r)
    {
        _selected = r;
        if (r != null) HostBox.Text = "";
        foreach (var chip in Receivers.Children.OfType<ToggleButton>())
            chip.IsChecked = r != null && ((StackPanel)chip.Content!).Children.OfType<TextBlock>().Last().Text == r.Address;
    }

    private (string Host, int Port) Target()
    {
        if (_selected != null) return (_selected.Address, _selected.Port);
        var text = (HostBox.Text ?? "").Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.Count(c => c == ':') == 1 && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);
        return (text, Protocol.DefaultPort);
    }

    // ---------------------------------------------------------------- envio

    private async Task ToggleSend()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }
        var sources = _sources.ToList();
        var (host, port) = Target();
        var code = Code.Code;
        if (sources.Count == 0) { await Dialogs.Show(_main, L.T("send.needSource")); return; }
        if (host.Length == 0) { await Dialogs.Show(_main, L.T("send.needTarget")); return; }
        if (code.Length != 6) { await Dialogs.Show(_main, L.T("send.needCode")); return; }

        var settings = _main.Settings;
        settings.LastHost = _selected == null ? (port == Protocol.DefaultPort ? host : $"{host}:{port}") : "";
        settings.Save();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SendButton.Content = L.T("send.cancel");
        SendButton.Classes.Remove("accent");
        _stats = null;
        Progress.Value = 0;
        _speed.Reset();
        _refresh.Start();
        var started = Stopwatch.StartNew();
        try
        {
            Log(L.T("send.listing"));
            var excludes = settings.Excludes;
            var scan = await Task.Run(() => Manifest.Scan(sources, excludes, ct,
                n => Dispatcher.UIThread.Post(() => StatusText.Text = L.T("send.listingN", n))), ct);
            foreach (var w in scan.Warnings) Log(L.T("send.warning", w));
            if (scan.CloudOnlySkipped > 0) Log(L.T("send.cloudOnly", scan.CloudOnlySkipped));
            Log(L.T("send.scanned", scan.FileCount, Format.Bytes(scan.TotalBytes)));

            var stats = new TransferStats();
            _stats = stats;
            var sender = new Sender
            {
                Host = host, Port = port, Code = code, Connections = Math.Clamp(settings.Connections, 1, 16),
                Log = m => Dispatcher.UIThread.Post(() => Log(m)),
            };
            var result = await Task.Run(() => sender.SendAsync(scan, stats, ct), ct);
            RefreshProgress();
            var elapsed = started.Elapsed;
            var summary = L.T("send.done", Format.Duration(elapsed), result.FilesOk, stats.SkippedFiles, result.FilesFailed,
                Format.Speed(stats.BytesDone / Math.Max(elapsed.TotalSeconds, 0.001)));
            Log(summary);
            StatusText.Text = summary;
            foreach (var e in result.Errors) Log(L.T("send.failLine", e));
            if (result.FilesFailed > 0) await Dialogs.Show(_main, L.T("send.someFailed", result.FilesFailed));
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            Log(L.T("send.canceled"));
            StatusText.Text = L.T("send.canceled");
            _ = ex;
        }
        catch (Exception ex)
        {
            Log(L.T("send.error", ex.Message));
            StatusText.Text = L.T("send.error", ex.Message);
            await Dialogs.Show(_main, ex is SocketException ? L.T("send.connectFailed", host, port, ex.Message) : ex.Message, error: true);
        }
        finally
        {
            _refresh.Stop();
            _cts.Dispose();
            _cts = null;
            SendButton.Content = L.T("send.send");
            SendButton.Classes.Add("accent");
            CurrentFile.Text = "";
        }
    }

    private void RefreshProgress()
    {
        if (_stats is not { } s) return;
        var (text, value) = ProgressText.Describe(s, _speed);
        StatusText.Text = text;
        Progress.Value = value;
        if (_cts != null) CurrentFile.Text = s.CurrentFile;
    }

    private void Log(string line) => ProgressText.Append(LogBox, line);

    public void CancelSend() => _cts?.Cancel();

    private async void ReopenElevated()
    {
        if (_main.Busy)
        {
            await Dialogs.Show(_main, L.T("busy.wait"));
            return;
        }
        var state = new Handoff
        {
            Sources = _sources.ToList(),
            UserFolders = _userFolders ?? Platform.UserFolders(),
            Excludes = _main.Settings.Excludes,
            Connections = _main.Settings.Connections,
            Target = HostBox.Text ?? "",
        };
        if (Platform.StartElevated(state)) _main.ExitApp();
    }
}
