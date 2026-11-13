using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Painel do servidor (versão instalada): pasta da empresa, pastas da equipe com permissões,
/// pessoas e grupos. Tudo é editado num rascunho e vai para o Windows de uma vez no "Aplicar".
/// </summary>
public partial class ServerView : UserControl
{
    private readonly MainView _main;
    private ShareConfig _saved = new();
    private ShareConfig _draft = new();
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private string? _selected;
    private readonly DispatcherTimer _overviewTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public ServerView() : this(null!) { } // designer

    public ServerView(MainView main)
    {
        _main = main;
        InitializeComponent();
        if (main == null) return;

        TabOverview.Click += (_, _) => ShowTab(0);
        TabFolders.Click += (_, _) => ShowTab(1);
        TabPeople.Click += (_, _) => ShowTab(2);
        TabActivity.Click += async (_, _) => { ShowTab(3); await LoadActivity(); };
        ActivityRefresh.Click += async (_, _) => await LoadActivity();
        ActivityPeriod.SelectionChanged += async (_, _) => { if (ActivityPanel.IsVisible) await LoadActivity(); };
        ActivitySearch.TextChanged += (_, _) => RenderActivity();
        ActivityOn.IsCheckedChanged += (_, _) =>
        {
            if (ActivityOn.IsChecked == _draft.Activity) return;
            _draft.Activity = ActivityOn.IsChecked == true;
            Changed(false);
        };
        ShowTab(0);

        ChooseRootButton.Click += async (_, _) => await ChooseRoot();
        ShareNameBox.TextChanged += (_, _) =>
        {
            if (ShareNameBox.Text != _draft.ShareName)
            {
                _draft.ShareName = (ShareNameBox.Text ?? "").Trim();
                AddressText.Text = $@"\\{Platform.DeviceName}\{_draft.ShareName}";
                Changed(false);
            }
        };
        AddFolderButton.Click += (_, _) => AddFolder();
        NewFolderBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) AddFolder(); };
        AddGroupButton.Click += (_, _) => AddGroup();
        NewGroupBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) AddGroup(); };
        AddPersonButton.Click += async (_, _) => await AddPerson();
        ApplyButton.Click += async (_, _) => await Apply();
        DiscardButton.Click += (_, _) => { _draft = _saved.Clone(); _passwords.Clear(); RenderAll(); };

        _overviewTimer.Tick += async (_, _) => await RefreshOverview();
        AttachedToVisualTree += async (_, _) =>
        {
            await Reload();
            _overviewTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _overviewTimer.Stop();
    }

    private bool Dirty => _draft.ToJson() != _saved.ToJson() || _passwords.Count > 0;

    private async Task Reload()
    {
        var cfg = await AgentClient.GetAsync<ShareConfig>("shares-get");
        ProblemText.IsVisible = cfg == null;
        ProblemText.Text = L.T("srv.noService");
        if (cfg == null) return;
        // Não descarta o que está sendo editado ao voltar para a tela.
        if (!Dirty) _draft = cfg.Clone();
        _saved = cfg;
        RenderAll();
        await RefreshOverview();
    }

    private void ShowTab(int i)
    {
        TabOverview.IsChecked = i == 0;
        TabFolders.IsChecked = i == 1;
        TabPeople.IsChecked = i == 2;
        TabActivity.IsChecked = i == 3;
        OverviewPanel.IsVisible = i == 0;
        FoldersPanel.IsVisible = i == 1;
        PeoplePanel.IsVisible = i == 2;
        ActivityPanel.IsVisible = i == 3;
    }

    /// <summary>Marca o rascunho como alterado; rerender redesenha as listas.</summary>
    private void Changed(bool rerender = true)
    {
        if (rerender) RenderAll();
        ApplyBar.IsVisible = Dirty;
    }

    private void RenderAll()
    {
        bool configured = _saved.Root.Length > 0;
        SetupTitle.IsVisible = SetupText.IsVisible = !configured;
        RootText.Text = _draft.Root.Length > 0 ? _draft.Root : L.T("recv.noFolder");
        if (ShareNameBox.Text != _draft.ShareName) ShareNameBox.Text = _draft.ShareName;
        AddressText.Text = $@"\\{Platform.DeviceName}\{_draft.ShareName}";
        PeopleCountText.Text = _saved.Users.Count.ToString();
        ActivityOn.IsChecked = _draft.Activity;
        RenderFolders();
        RenderPeople();
        ApplyBar.IsVisible = Dirty;
    }

    // ---------------------------------------------------------------- visão geral

    private async Task RefreshOverview()
    {
        if (!OverviewPanel.IsVisible && SessionsText.Text != "—") return;
        var o = await AgentClient.GetAsync<ShareOverview>("shares-overview");
        if (o == null) return;
        SessionsText.Text = o.Sessions.ToString();
        OpenCountText.Text = o.OpenFiles.Count.ToString();
        NoOpenFiles.IsVisible = o.OpenFiles.Count == 0;
        if (_saved.Root.Length == 0) NoOpenFiles.Text = L.T("srv.notApplied");
        OpenFilesList.Children.Clear();
        foreach (var f in o.OpenFiles.Take(50))
        {
            var rel = f.Path.StartsWith(_saved.Root, StringComparison.OrdinalIgnoreCase) ? f.Path[_saved.Root.Length..].TrimStart('\\') : f.Path;
            OpenFilesList.Children.Add(new DockPanel
            {
                Children =
                {
                    Pill(f.User.Split('\\').Last(), dock: Dock.Right),
                    new TextBlock { Text = rel, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
    }

    private static Border Pill(string text, Dock? dock = null)
    {
        var b = new Border { Classes = { "pill" }, Child = new TextBlock { Text = text, FontSize = 12 }, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        if (dock != null) DockPanel.SetDock(b, dock.Value);
        return b;
    }

    // ---------------------------------------------------------------- pasta da empresa

    private async Task ChooseRoot()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("srv.root") });
        if (picked.Count == 0 || Platform.LocalPath(picked[0]) is not { } path) return;
        _draft.Root = path;
        // Pastas que já existem dentro da raiz entram na lista (sem permissões até você marcar).
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(d);
                if (!_draft.Folders.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    _draft.Folders.Add(new ShareFolder { Name = name });
            }
        }
        catch { /* sem acesso para listar: segue com a lista atual */ }
        Changed();
    }

    // ---------------------------------------------------------------- pastas

    private void AddFolder()
    {
        var name = (NewFolderBox.Text ?? "").Trim();
        if (name.Length == 0 || _draft.Folders.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _draft.Folders.Add(new ShareFolder { Name = name });
        _selected = name;
        NewFolderBox.Text = "";
        Changed();
    }

    private void RenderFolders()
    {
        FolderList.Children.Clear();
        foreach (var f in _draft.Folders)
        {
            var b = new Button
            {
                Classes = { "nav" },
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { new PathIcon { Data = Res.Get<Geometry>("IconFolder"), Width = 16, Height = 16 }, new TextBlock { Text = f.Name } },
                },
            };
            b.Classes.Set("active", f.Name == _selected);
            b.Click += (_, _) => { _selected = f.Name; RenderFolders(); };
            FolderList.Children.Add(b);
        }
        RenderFolderDetail();
    }

    private void RenderFolderDetail()
    {
        FolderDetail.Children.Clear();
        var f = _draft.Folders.FirstOrDefault(x => x.Name == _selected);
        if (f == null)
        {
            FolderDetail.Children.Add(new TextBlock { Classes = { "muted" }, Text = L.T("srv.selectFolder") });
            return;
        }
        FolderDetail.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Classes = { "h2" }, Text = f.Name },
                new TextBlock { Classes = { "caption" }, Text = _draft.Root.Length > 0 ? Path.Combine(_draft.Root, f.Name) : "" },
            },
        });
        FolderDetail.Children.Add(new TextBlock { Classes = { "caption" }, Text = L.T("srv.whoCanUse"), Margin = new Avalonia.Thickness(0, 6, 0, 0) });
        if (_draft.Groups.Count == 0) FolderDetail.Children.Add(new TextBlock { Classes = { "muted" }, Text = L.T("srv.noGroups") });

        foreach (var g in _draft.Groups)
            FolderDetail.Children.Add(AccessRow(Icon("IconUser"), g.Name, null, f.AccessOf(RuleKind.Group, g.Name), a => f.SetAccess(RuleKind.Group, g.Name, a)));
        foreach (var r in f.Rules.Where(r => r.Kind == RuleKind.User).ToList())
        {
            var person = _draft.Users.FirstOrDefault(u => u.Login.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            FolderDetail.Children.Add(AccessRow(Icon("IconUser"), person?.FullName is { Length: > 0 } n ? n : r.Name, L.T("srv.exception"),
                r.Access, a => f.SetAccess(RuleKind.User, r.Name, a), remove: () => f.ClearUser(r.Name)));
        }
        FolderDetail.Children.Add(AccessRow(Icon("IconComputer"), L.T("srv.everyoneElse"), null, f.AccessOf(RuleKind.Everyone, ""), a => f.SetAccess(RuleKind.Everyone, "", a)));

        // Exceção para uma pessoa.
        var candidates = _draft.Users.Where(u => !f.Rules.Any(r => r.Kind == RuleKind.User && r.Name.Equals(u.Login, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count > 0)
        {
            var combo = new ComboBox { PlaceholderText = L.T("srv.addException"), MinWidth = 240, ItemsSource = candidates.Select(u => u.FullName.Length > 0 ? $"{u.FullName} ({u.Login})" : u.Login).ToList() };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                f.SetAccess(RuleKind.User, candidates[combo.SelectedIndex].Login, Access.None);
                Changed();
            };
            FolderDetail.Children.Add(combo);
        }

        // Fixar no Acesso rápido (aplicado pelas estações).
        if (_draft.Groups.Count > 0)
        {
            var pins = new WrapPanel();
            foreach (var g in _draft.Groups)
            {
                var cb = new CheckBox { Content = g.Name, IsChecked = f.PinFor.Contains(g.Name, StringComparer.OrdinalIgnoreCase), Margin = new Avalonia.Thickness(0, 0, 14, 0) };
                cb.IsCheckedChanged += (_, _) =>
                {
                    f.PinFor.RemoveAll(p => p.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                    if (cb.IsChecked == true) f.PinFor.Add(g.Name);
                    Changed(false);
                };
                pins.Children.Add(cb);
            }
            FolderDetail.Children.Add(new StackPanel
            {
                Spacing = 4, Margin = new Avalonia.Thickness(0, 8, 0, 0),
                Children = { new TextBlock { Classes = { "caption" }, Text = L.T("srv.pinFor") }, pins },
            });
        }

        var remove = new Button { Content = L.T("srv.removeFolder"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        remove.Click += (_, _) => { _draft.Folders.Remove(f); _selected = null; Changed(); };
        FolderDetail.Children.Add(remove);
    }

    private static PathIcon Icon(string key) => new() { Data = Res.Get<Geometry>(key), Width = 14, Height = 14, Margin = new Avalonia.Thickness(0, 0, 10, 0) };

    /// <summary>Linha "nome ...... [Sem acesso | Ler | Ler e editar]".</summary>
    private Control AccessRow(PathIcon icon, string label, string? tag, string access, Action<string> set, Action? remove = null)
    {
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var (value, text) in new[] { (Access.None, L.T("srv.none")), (Access.Read, L.T("srv.read")), (Access.Write, L.T("srv.write")) })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = text, IsChecked = access == value };
            b.Click += (_, _) => { set(value); Changed(); };
            seg.Children.Add(b);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { seg } };
        if (remove != null)
        {
            var x = new Button { Classes = { "icon" }, Content = new PathIcon { Data = Res.Get<Geometry>("IconClose"), Width = 10, Height = 10 } };
            x.Click += (_, _) => { remove(); Changed(); };
            right.Children.Add(x);
        }
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
        if (tag != null) left.Children.Add(Pill(tag));
        return new Border
        {
            BorderBrush = Res.Get<IBrush>("CardBorder"), BorderThickness = new Avalonia.Thickness(0, 1, 0, 0), Padding = new Avalonia.Thickness(0, 8, 0, 0),
            Child = new DockPanel { Children = { right, left } },
        };
    }

    // ---------------------------------------------------------------- pessoas e grupos

    private void AddGroup()
    {
        var name = (NewGroupBox.Text ?? "").Trim();
        if (name.Length == 0 || _draft.Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _draft.Groups.Add(new ShareGroup { Name = name });
        NewGroupBox.Text = "";
        Changed();
    }

    private async Task AddPerson()
    {
        var login = (NewLoginBox.Text ?? "").Trim();
        var password = NewPasswordBox.Text ?? "";
        if (login.Length == 0 || _draft.Users.Any(u => u.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            await _main.Ask(L.T("srv.invalidName", login), error: true);
            return;
        }
        if (password.Length < 6)
        {
            await _main.Ask(L.T("srv.passwordShort"), error: true);
            return;
        }
        _draft.Users.Add(new ShareUser { Login = login, FullName = (NewNameBox.Text ?? "").Trim() });
        _passwords[login] = password;
        NewLoginBox.Text = NewNameBox.Text = NewPasswordBox.Text = "";
        Changed();
    }

    private void RenderPeople()
    {
        GroupList.Children.Clear();
        foreach (var g in _draft.Groups)
        {
            var x = new Button { Classes = { "icon" }, Content = new PathIcon { Data = Res.Get<Geometry>("IconClose"), Width = 9, Height = 9 }, Padding = new Avalonia.Thickness(4) };
            x.Click += (_, _) =>
            {
                _draft.Groups.Remove(g);
                foreach (var u in _draft.Users) u.Groups.RemoveAll(n => n.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var f in _draft.Folders)
                {
                    f.Rules.RemoveAll(r => r.Kind == RuleKind.Group && r.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                    f.PinFor.RemoveAll(n => n.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                }
                Changed();
            };
            GroupList.Children.Add(new Border
            {
                Classes = { "pill" }, Margin = new Avalonia.Thickness(0, 0, 8, 8), Padding = new Avalonia.Thickness(12, 4, 4, 4),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = g.Name, VerticalAlignment = VerticalAlignment.Center }, x } },
            });
        }

        PeopleList.Children.Clear();
        foreach (var u in _draft.Users)
        {
            var name = new TextBox { Text = u.FullName, Watermark = L.T("srv.fullName"), Width = 200 };
            name.LostFocus += (_, _) => { if (name.Text != u.FullName) { u.FullName = (name.Text ?? "").Trim(); Changed(false); } };
            var pass = new TextBox { Watermark = L.T("srv.newPassword"), PasswordChar = '●', Width = 230, Text = _passwords.GetValueOrDefault(u.Login, "") };
            pass.LostFocus += (_, _) =>
            {
                var p = pass.Text ?? "";
                if (p.Length == 0) _passwords.Remove(u.Login);
                else if (p.Length >= 6) _passwords[u.Login] = p;
                Changed(false);
            };
            var disabled = new CheckBox { Content = L.T("srv.disabled"), IsChecked = u.Disabled };
            disabled.IsCheckedChanged += (_, _) => { u.Disabled = disabled.IsChecked == true; Changed(false); };
            var remove = new Button { Classes = { "icon" }, Content = new PathIcon { Data = Res.Get<Geometry>("IconClose"), Width = 10, Height = 10 } };
            remove.Click += (_, _) =>
            {
                _draft.Users.Remove(u);
                _passwords.Remove(u.Login);
                foreach (var f in _draft.Folders) f.ClearUser(u.Login);
                Changed();
            };
            var groups = new WrapPanel();
            foreach (var g in _draft.Groups)
            {
                var cb = new CheckBox { Content = g.Name, IsChecked = u.Groups.Contains(g.Name, StringComparer.OrdinalIgnoreCase), Margin = new Avalonia.Thickness(0, 0, 14, 0) };
                cb.IsCheckedChanged += (_, _) =>
                {
                    u.Groups.RemoveAll(n => n.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                    if (cb.IsChecked == true) u.Groups.Add(g.Name);
                    Changed(false);
                };
                groups.Children.Add(cb);
            }
            DockPanel.SetDock(remove, Dock.Right);
            PeopleList.Children.Add(new Border
            {
                BorderBrush = Res.Get<IBrush>("CardBorder"), BorderThickness = new Avalonia.Thickness(0, 0, 0, 1), Padding = new Avalonia.Thickness(0, 0, 0, 12),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new DockPanel { Children = { remove, new TextBlock { Text = u.Login, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
                        new WrapPanel { Children = { name, new Border { Width = 8 }, pass, new Border { Width = 12 }, disabled } },
                        groups,
                    },
                },
            });
        }
    }

    // ---------------------------------------------------------------- aplicar

    private async Task Apply()
    {
        if (_draft.Root.Length == 0)
        {
            await _main.Ask(L.T("srv.setupText"));
            return;
        }
        if (_draft.Validate() is { } bad)
        {
            await _main.Ask(L.T("srv.invalidName", bad), error: true);
            return;
        }
        ApplyButton.IsEnabled = DiscardButton.IsEnabled = false;
        ApplyText.Text = L.T("srv.applying");
        try
        {
            var req = new ShareApplyRequest { Config = _draft, Passwords = new(_passwords) };
            var error = await AgentClient.CommandAsync("shares-apply", System.Text.Json.JsonSerializer.Serialize(req));
            if (error == L.T("svc.needAdmin"))
            {
                // Pedido de administrador cancelado: o rascunho continua como estava.
                await _main.Ask(error, error: true);
                return;
            }
            _passwords.Clear();
            _draft = new ShareConfig(); // força recarregar do serviço
            var cfg = await AgentClient.GetAsync<ShareConfig>("shares-get");
            if (cfg != null) { _saved = cfg; _draft = cfg.Clone(); }
            RenderAll();
            await _main.Ask(error == null ? L.T("srv.appliedOk") : L.T("srv.applyFailed", error), error: error != null);
        }
        finally
        {
            ApplyButton.IsEnabled = DiscardButton.IsEnabled = true;
            ApplyText.Text = L.T("srv.pending");
        }
    }
}
