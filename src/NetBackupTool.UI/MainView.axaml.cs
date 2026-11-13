using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

public sealed record StartOptions(bool Receive = false, bool Hidden = false, Handoff? Handoff = null, Exception? FinishError = null);

/// <summary>O que muda entre o programa de desktop e o app Android.</summary>
public interface IAppHost
{
    /// <summary>Desktop: ícone na bandeja, iniciar com o sistema, firewall, VSS.</summary>
    bool IsDesktop { get; }
    void Notify(string title, string text);
    void ShowWindow();
    void SetTrayVisible(bool visible);
    /// <summary>Reabrir o programa (troca de idioma).</summary>
    void Restart();
    void ExitApp();
    void ExitForUpdate(string newFile);
    /// <summary>Celular: manter a tela ligada durante uma cópia (o sistema derruba a rede com a tela apagada).</summary>
    void KeepAwake(bool on);
}

/// <summary>
/// A interface inteira: navegação (barra lateral em telas largas, abas embaixo no celular),
/// as quatro telas e as caixas de mensagem sobre a própria tela.
/// </summary>
public partial class MainView : UserControl
{
    public IAppHost Host { get; }
    public Settings Settings => App.Settings;
    public SendView SendPage { get; }
    public ReceiveView ReceivePage { get; }
    private readonly SettingsView _settingsPage;
    private readonly AboutView _aboutPage;
    /// <summary>Só na versão instalada como servidor.</summary>
    private readonly ServerView? _serverPage;
    /// <summary>Computador que não é o servidor: liga às pastas da empresa.</summary>
    private readonly StationView? _stationPage;
    private TaskCompletionSource<bool>? _overlay;
    private const double CompactWidth = 700;

    public event Action<bool>? ReceivingChanged;

    public bool Busy => SendPage.Busy || ReceivePage.Busy;

    public MainView() : this(null!, new StartOptions()) { } // designer

    public MainView(IAppHost host, StartOptions options)
    {
        Host = host;
        InitializeComponent();
        SendPage = new SendView(this, options.Handoff);
        ReceivePage = new ReceiveView(this);
        _settingsPage = new SettingsView(this);
        _aboutPage = new AboutView(this);
        if (host != null && host.IsDesktop && Agent.AgentConfig.ServerInstalled)
        {
            _serverPage = new ServerView(this);
            NavServer.IsVisible = true;
            NavServer.Click += (_, _) => Navigate(_serverPage);
        }
        else if (host != null)
        {
            // Técnico num PC da equipe: administra o servidor pela rede (aparece no menu depois de usar).
            if (host.IsDesktop && OperatingSystem.IsWindows())
            {
                _serverPage = new ServerView(this);
                NavServer.Click += (_, _) => Navigate(_serverPage);
            }
            // Computador da equipe ou celular: liga às pastas da empresa no servidor.
            _stationPage = new StationView(this);
            NavCompany.IsVisible = TabCompany.IsVisible = true;
            TabGrid.Columns = 5;
            NavCompany.Click += (_, _) => Navigate(_stationPage);
            TabCompany.Click += (_, _) => Navigate(_stationPage);
        }

        void Link(Button nav, Button tab, Control page)
        {
            nav.Click += (_, _) => Navigate(page);
            tab.Click += (_, _) => Navigate(page);
        }
        Link(NavSend, TabSend, SendPage);
        Link(NavReceive, TabReceive, ReceivePage);
        Link(NavSettings, TabSettings, _settingsPage);
        Link(NavAbout, TabAbout, _aboutPage);
        Navigate(options.Receive ? ReceivePage : SendPage);

        ReceivePage.ReceivingChanged += receiving =>
        {
            ReceivingDot.IsVisible = receiving;
            ReceivingChanged?.Invoke(receiving);
        };

        OverlayYes.Click += (_, _) => CloseOverlay(true);
        OverlayNo.Click += (_, _) => CloseOverlay(false);
        SizeChanged += (_, e) => SetCompact(e.NewSize.Width < CompactWidth);
        Loaded += (_, _) =>
        {
            // Celular: o teclado na tela cobre a parte de baixo. A tela encolhe para caber acima dele
            // e o campo em que a pessoa está digitando rola até ficar visível.
            var pane = TopLevel.GetTopLevel(this)?.InputPane;
            if (pane != null) pane.StateChanged += (_, e) => FitAboveKeyboard(e.EndRect, e.NewState == Avalonia.Controls.Platform.InputPaneState.Open);
        };
        GotFocus += (_, e) =>
        {
            if (_keyboardOpen && e.Source is Control c) Dispatcher.UIThread.Post(() => c.BringIntoView(), DispatcherPriority.Background);
        };
        Focusable = true;
        Loaded += (_, _) => Focus();
    }

    public void Navigate(Control page)
    {
        PageHost.Content = page;
        var pages = new Control[] { SendPage, ReceivePage, _settingsPage, _aboutPage };
        var navs = new[] { NavSend, NavReceive, NavSettings, NavAbout };
        var tabs = new[] { TabSend, TabReceive, TabSettings, TabAbout };
        for (int i = 0; i < pages.Length; i++)
        {
            navs[i].Classes.Set("active", pages[i] == page);
            tabs[i].Classes.Set("active", pages[i] == page);
        }
        NavServer.Classes.Set("active", page == _serverPage && page != null);
        NavCompany.Classes.Set("active", page == _stationPage && page != null);
        TabCompany.Classes.Set("active", page == _stationPage && page != null);
    }

    public void ShowReceivePage() => Navigate(ReceivePage);

    /// <summary>Página do servidor (num PC da equipe: administração de outro servidor pela rede).</summary>
    public bool CanAdministerServer => _serverPage != null;

    public void ShowServerPage()
    {
        if (_serverPage == null) return;
        NavServer.IsVisible = true;
        Navigate(_serverPage);
    }

    /// <summary>Celular (tela estreita): abas embaixo e margens menores.</summary>
    private bool _keyboardOpen;
    private bool _compact;

    private void FitAboveKeyboard(Rect keyboard, bool open)
    {
        var top = TopLevel.GetTopLevel(this);
        // Quanto do app o teclado cobre (zero se o sistema já encolheu a janela).
        double covered = 0;
        if (open && top != null && keyboard.Height > 0)
        {
            var bottom = this.TranslatePoint(new Point(0, Bounds.Height), top)?.Y ?? top.Bounds.Height;
            covered = Math.Max(0, bottom - keyboard.Top);
        }
        _keyboardOpen = open;
        Padding = new Thickness(0, 0, 0, covered);
        // Com o teclado aberto as abas de baixo só tomam espaço.
        BottomBar.IsVisible = _compact && !open;
        if (open && top?.FocusManager?.GetFocusedElement() is Control focused)
            Dispatcher.UIThread.Post(() => focused.BringIntoView(), DispatcherPriority.Background);
    }

    private void SetCompact(bool compact)
    {
        Classes.Set("compact", compact);
        Sidebar.IsVisible = !compact;
        _compact = compact;
        BottomBar.IsVisible = compact && !_keyboardOpen;
        Root.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(210);
    }

    /// <summary>Mensagem sobre a tela; com yesNo devolve true para "Sim".</summary>
    public Task<bool> Ask(string text, bool yesNo = false, bool error = false, string? title = null)
    {
        _overlay?.TrySetResult(false);
        Host.ShowWindow();
        OverlayText.Text = title == null ? text : $"{title}\n\n{text}";
        OverlayIcon.Data = Res.Get<Geometry>(error ? "IconShield" : "IconInfo");
        OverlayIcon.Foreground = error ? Brushes.IndianRed : Res.Get<IBrush>("AccentText");
        OverlayYes.Content = L.T(yesNo ? "dlg.yes" : "dlg.ok");
        OverlayNo.Content = L.T("dlg.no");
        OverlayNo.IsVisible = yesNo;
        Overlay.IsVisible = true;
        _overlay = new TaskCompletionSource<bool>();
        return _overlay.Task;
    }

    private void CloseOverlay(bool result)
    {
        Overlay.IsVisible = false;
        _overlay?.TrySetResult(result);
        _overlay = null;
    }
}
