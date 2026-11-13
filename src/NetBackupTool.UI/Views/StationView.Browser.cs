using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Celular: as pastas da empresa num navegador dentro do app. Abrir um arquivo baixa uma cópia e
/// entrega ao app certo (Word, Excel, PDF...); se a cópia for alterada, o app oferece enviar de volta.
/// </summary>
public partial class StationView
{
    private SmbBrowser? _browser;
    private string _path = "";
    private List<SmbEntry> _entries = new();
    private bool _browserBusy;

    /// <summary>Cópias abertas em outros apps: cópia local → arquivo no servidor e datas da abertura.</summary>
    private readonly Dictionary<string, OpenedCopy> _opened = new();
    private sealed record OpenedCopy(string Remote, DateTime LocalWrite, DateTime ServerModified);

    private const string SecretKey = "empresa-senha";

    private void SetupBrowser()
    {
        UpButton.Click += async (_, _) => await LoadFolder(Parent(_path));
        UploadButton.Click += async (_, _) => await Upload();
        NewFolderButton.Click += (_, _) =>
        {
            NewFolderRow.IsVisible = !NewFolderRow.IsVisible;
            if (NewFolderRow.IsVisible) NewFolderNameBox.Focus();
        };
        CreateFolderButton.Click += async (_, _) => await CreateFolder();
        NewFolderNameBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await CreateFolder(); };
        BrowserDisconnectButton.Click += async (_, _) => await DisconnectBrowser();
        Platform.Resumed += async () => await CheckEditedCopies();
    }

    /// <summary>Ao abrir o app: já conectado antes e com a senha guardada.</summary>
    private void ResumeBrowser()
    {
        if (_config == null) return;
        var password = Platform.LoadSecret?.Invoke(SecretKey);
        if (password == null) return; // pede de novo
        _browser = new SmbBrowser(_config.Address ?? _config.Server, _config.Share, _config.Login, password);
    }

    private async Task ConnectBrowser(string server, string? address, string share, string login, string password)
    {
        _busy = true;
        ConnectButton.IsEnabled = false;
        Status(L.T("sta.connecting"), error: false);
        var host = address ?? server;
        var browser = new SmbBrowser(host, share, login, password);
        var error = await browser.ConnectAsync();
        _busy = false;
        ConnectButton.IsEnabled = true;
        if (error != null)
        {
            browser.Dispose();
            Status(error, error: true);
            return;
        }
        _config = new StationConfig { Server = server, Address = host, Share = share, Login = login };
        _config.Save();
        Platform.SaveSecret?.Invoke(SecretKey, password);
        PasswordBox.Text = "";
        Status("", error: false);
        _browser = browser;
        Render();
        await LoadFolder("");
    }

    private async Task DisconnectBrowser()
    {
        if (_browser == null || !await _main.Ask(L.T("sta.disconnectAskAndroid"), yesNo: true)) return;
        _browser.Dispose();
        _browser = null;
        _opened.Clear();
        Platform.SaveSecret?.Invoke(SecretKey, null);
        StationConfig.Delete();
        // Cópias locais dos arquivos do servidor.
        try { if (CopiesDir is { } dir && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Render();
        await Search();
    }

    // ---------------------------------------------------------------- navegar

    private static string Parent(string path) => path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";

    private async Task LoadFolder(string path)
    {
        if (_browser == null || _browserBusy) return;
        _browserBusy = true;
        BrowserMessage(L.T("sta.loading"));
        try
        {
            _entries = await _browser.ListAsync(path);
            _path = path;
            BrowserMessage(null);
        }
        catch (Exception ex)
        {
            BrowserMessage(ex.Message, error: true);
        }
        finally { _browserBusy = false; }
        UpButton.IsVisible = _path.Length > 0;
        PathText.Text = _path.Length == 0 ? _config?.Share ?? "" : _path.Replace("\\", " › ");
        RenderEntries();
    }

    private void RenderEntries()
    {
        EntryList.Children.Clear();
        EmptyText.IsVisible = _entries.Count == 0;
        foreach (var e in _entries)
        {
            var icon = new PathIcon
            {
                Data = (Geometry)this.FindResource(e.IsFolder ? "IconFolder" : "IconFile")!, Width = 20, Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = e.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Classes = { "caption" },
                Text = e.IsFolder ? e.Modified.ToString("g") : $"{Format.Bytes(e.Size)} · {e.Modified:g}",
            });
            var row = new DockPanel { LastChildFill = true };
            if (!e.IsFolder)
            {
                // Guardar uma cópia na pasta Download do celular.
                var save = new Button
                {
                    Content = new PathIcon { Data = (Geometry)this.FindResource("IconDownload")!, Width = 16, Height = 16 },
                    Padding = new(10, 8), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(save, L.T("sta.saveToPhone"));
                save.Click += async (_, _) => await SaveToPhone(e);
                DockPanel.SetDock(save, Dock.Right);
                row.Children.Add(save);
            }
            var main = new Button
            {
                Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 8),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, text } },
            };
            main.Click += async (_, _) =>
            {
                if (e.IsFolder) await LoadFolder(SmbBrowser.Join(_path, e.Name));
                else await OpenRemote(e);
            };
            row.Children.Add(main);
            EntryList.Children.Add(row);
        }
    }

    private void BrowserMessage(string? text, bool error = false)
    {
        BrowserStatus.IsVisible = !string.IsNullOrEmpty(text);
        BrowserStatus.Text = text;
        if (error) BrowserStatus.Foreground = Brushes.IndianRed;
        else BrowserStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    private IProgress<long> ProgressFor(string verb, string name, long total) => new Progress<long>(done =>
        BrowserMessage(total > 0 ? $"{verb} {name}… {Math.Min(100, done * 100 / total)}%" : $"{verb} {name}… {Format.Bytes(done)}"));

    // ---------------------------------------------------------------- arquivos

    private static string? CopiesDir => Platform.CacheDir == null ? null : Path.Combine(Platform.CacheDir, "empresa");

    /// <summary>Baixa uma cópia e abre no app padrão do tipo de arquivo.</summary>
    private async Task OpenRemote(SmbEntry e)
    {
        if (_browser == null || CopiesDir == null || _browserBusy) return;
        var remote = SmbBrowser.Join(_path, e.Name);
        // Uma pasta por arquivo do servidor: o nome continua o original no app que abrir.
        var local = Path.Combine(CopiesDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(remote.ToLowerInvariant())))[..12], e.Name);
        _browserBusy = true;
        try
        {
            var serverTime = await _browser.DownloadAsync(remote, local, ProgressFor(L.T("sta.downloading"), e.Name, e.Size));
            _opened[local] = new OpenedCopy(remote, File.GetLastWriteTimeUtc(local), serverTime);
            BrowserMessage(null);
            Platform.OpenFileHook?.Invoke(local);
        }
        catch (Exception ex) { BrowserMessage(ex.Message, error: true); }
        finally { _browserBusy = false; }
    }

    /// <summary>De volta ao app: alguma cópia aberta foi salva por outro app? Oferece mandar ao servidor.</summary>
    private async Task CheckEditedCopies()
    {
        if (_browser == null) return;
        foreach (var (local, copy) in _opened.ToList())
        {
            if (!File.Exists(local) || File.GetLastWriteTimeUtc(local) <= copy.LocalWrite) continue;
            var name = Path.GetFileName(local);
            if (!await _main.Ask(L.T("sta.sendBackAsk", name), yesNo: true))
            {
                // Não pergunta de novo pela mesma alteração.
                _opened[local] = copy with { LocalWrite = File.GetLastWriteTimeUtc(local) };
                continue;
            }
            try
            {
                // Alguém mudou o arquivo no servidor enquanto ele estava aberto aqui?
                var now = await _browser.ModifiedAsync(copy.Remote);
                if (now != null && Math.Abs((now.Value - copy.ServerModified).TotalSeconds) > 2 &&
                    !await _main.Ask(L.T("sta.conflictAsk", name), yesNo: true))
                    continue;
                await using (var input = File.OpenRead(local))
                    await _browser.UploadAsync(input, copy.Remote, ProgressFor(L.T("sta.uploading"), name, input.Length));
                var serverTime = await _browser.ModifiedAsync(copy.Remote) ?? DateTime.Now;
                _opened[local] = new OpenedCopy(copy.Remote, File.GetLastWriteTimeUtc(local), serverTime);
                BrowserMessage(L.T("sta.sentBack", name));
                if (Parent(copy.Remote) == _path) await LoadFolder(_path);
            }
            catch (Exception ex) { BrowserMessage(ex.Message, error: true); }
        }
    }

    private async Task SaveToPhone(SmbEntry e)
    {
        if (_browser == null || _browserBusy) return;
        if (Platform.EnsureStorageAccess != null && !await Platform.EnsureStorageAccess()) return;
        var dir = Path.Combine(Platform.StorageRoot ?? "/storage/emulated/0", "Download");
        var target = Path.Combine(dir, e.Name);
        for (int i = 2; File.Exists(target); i++)
            target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(e.Name)} ({i}){Path.GetExtension(e.Name)}");
        _browserBusy = true;
        try
        {
            await _browser.DownloadAsync(SmbBrowser.Join(_path, e.Name), target, ProgressFor(L.T("sta.downloading"), e.Name, e.Size));
            BrowserMessage(L.T("sta.savedTo", target));
        }
        catch (Exception ex) { BrowserMessage(ex.Message, error: true); }
        finally { _browserBusy = false; }
    }

    private async Task Upload()
    {
        if (_browser == null || _browserBusy) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = L.T("sta.upload") });
        if (files.Count == 0) return;
        var existing = files.Where(f => _entries.Any(x => !x.IsFolder && x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (existing.Count > 0 && !await _main.Ask(L.T("sta.replaceAsk", string.Join(", ", existing.Select(f => f.Name))), yesNo: true))
            return;
        _browserBusy = true;
        try
        {
            foreach (var f in files)
            {
                var props = await f.GetBasicPropertiesAsync();
                await using var input = await f.OpenReadAsync();
                await _browser.UploadAsync(input, SmbBrowser.Join(_path, f.Name),
                    ProgressFor(L.T("sta.uploading"), f.Name, (long)(props.Size ?? 0)));
            }
            BrowserMessage(L.T("sta.uploaded", files.Count));
        }
        catch (Exception ex) { BrowserMessage(ex.Message, error: true); }
        finally { _browserBusy = false; }
        await LoadFolder(_path);
    }

    private async Task CreateFolder()
    {
        var name = (NewFolderNameBox.Text ?? "").Trim();
        if (_browser == null || name.Length == 0) return;
        if (name.IndexOfAny("\"/\\[]:;|=,+*?<>".ToCharArray()) >= 0 || name.EndsWith('.'))
        {
            BrowserMessage(L.T("srv.invalidName", name), error: true);
            return;
        }
        try
        {
            await _browser.CreateFolderAsync(SmbBrowser.Join(_path, name));
            NewFolderNameBox.Text = "";
            NewFolderRow.IsVisible = false;
            await LoadFolder(_path);
        }
        catch (Exception ex) { BrowserMessage(ex.Message, error: true); }
    }
}
