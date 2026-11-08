using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nbt.Core;

namespace NetBackupTool;

public partial class MainWindow : Window
{
    public static WindowIcon AppIcon { get; } = new(AssetLoader.Open(new Uri("avares://NetBackupTool/Assets/icon.ico")));
    public static Bitmap AppBitmap { get; } = new(AssetLoader.Open(new Uri("avares://NetBackupTool/Assets/icon.png")));

    public Settings Settings => App.Settings;
    public SendView SendPage { get; }
    public ReceiveView ReceivePage { get; }
    private readonly SettingsView _settingsPage;
    private readonly AboutView _aboutPage;

    private readonly TrayIcon _tray = new();
    private readonly NativeMenuItem _trayToggle = new();
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow() : this(new StartOptions()) { } // designer

    public MainWindow(StartOptions options)
    {
        InitializeComponent();
        if (ShadowCopies.IsElevated && Platform.IsWindows) Title = L.T("app.admin");

        SendPage = new SendView(this, options.Handoff);
        ReceivePage = new ReceiveView(this);
        _settingsPage = new SettingsView(this);
        _aboutPage = new AboutView(this);

        NavSend.Click += (_, _) => Navigate(SendPage, NavSend);
        NavReceive.Click += (_, _) => Navigate(ReceivePage, NavReceive);
        NavSettings.Click += (_, _) => Navigate(_settingsPage, NavSettings);
        NavAbout.Click += (_, _) => Navigate(_aboutPage, NavAbout);
        Navigate(options.Receive ? ReceivePage : SendPage, options.Receive ? NavReceive : NavSend);

        SetupTray();
        ReceivePage.ReceivingChanged += receiving =>
        {
            ReceivingDot.IsVisible = receiving;
            _trayToggle.Header = L.T(receiving ? "recv.stop" : "recv.start");
            _tray.ToolTipText = Trim(receiving ? L.T("tray.receiving", ReceivePage.CurrentCode) : L.T("tray.stopped"));
        };
        if (options.Receive) Opened += (_, _) => ReceivePage.EnsureReceiving();
    }

    private static string Trim(string s) => s.Length > 120 ? s[..120] : s;

    private void Navigate(Control page, Button nav)
    {
        Host.Content = page;
        foreach (var b in new[] { NavSend, NavReceive, NavSettings, NavAbout }) b.Classes.Set("active", b == nav);
    }

    /// <summary>Iniciado com o sistema: sem janela, só o ícone da bandeja, já recebendo.</summary>
    public void StartHidden()
    {
        Navigate(ReceivePage, NavReceive);
        if (Settings.DestinationRoot.Length == 0) ShowFromBackground();
        ReceivePage.EnsureReceiving();
    }

    public void ShowFromBackground()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.MainWindow == null)
            d.MainWindow = this;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- bandeja

    private void SetupTray()
    {
        _tray.Icon = AppIcon;
        _tray.ToolTipText = L.T("tray.stopped");
        var open = new NativeMenuItem(L.T("tray.open"));
        open.Click += (_, _) => ShowFromBackground();
        _trayToggle.Header = L.T("recv.start");
        _trayToggle.Click += (_, _) => ReceivePage.Toggle();
        var exit = new NativeMenuItem(L.T("tray.exit"));
        exit.Click += (_, _) => ExitApp();
        _tray.Menu = new NativeMenu { Items = { open, _trayToggle, new NativeMenuItemSeparator(), exit } };
        _tray.Clicked += (_, _) => ShowFromBackground();
        _tray.IsVisible = Settings.KeepInTray;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    public void SetTrayVisible(bool visible) => _tray.IsVisible = visible;

    /// <summary>Aviso no canto da tela quando a janela não está à vista.</summary>
    public void Notify(string title, string text)
    {
        if (IsVisible && IsActive) return;
        Toast.Show(title, text, ShowFromBackground);
    }

    // ---------------------------------------------------------------- fechar, sair, reabrir

    public bool Busy => SendPage.Busy || ReceivePage.Busy;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Modo persistente: o X só esconde a janela; a recepção segue com o ícone perto do relógio.
        if (!_exiting && Settings.KeepInTray && ReceivePage.IsReceiving && !SendPage.Busy)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Toast.Show(L.T("tray.keepsTitle"), L.T("tray.keepsText"), ShowFromBackground);
            }
            return;
        }
        if (!_exiting && Busy)
        {
            e.Cancel = true;
            _ = ConfirmExit();
            return;
        }
        Shutdown();
        base.OnClosing(e);
    }

    private async Task ConfirmExit()
    {
        if (await Dialogs.Show(this, L.T("busy.close"), yesNo: true)) ExitApp();
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
        Shutdown();
    }

    private void Shutdown()
    {
        SendPage.CancelSend();
        ReceivePage.StopReceiver();
        _tray.IsVisible = false;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>Reabre o programa (troca de idioma), mantendo a recepção se estava ligada.</summary>
    public void Restart()
    {
        var psi = new ProcessStartInfo(Platform.Exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--reiniciar");
        if (ReceivePage.IsReceiving) psi.ArgumentList.Add("receber");
        ReceivePage.StopReceiver();
        Process.Start(psi);
        ExitApp();
    }

    /// <summary>Fecha para o executável novo concluir a atualização.</summary>
    public void ExitForUpdate(string newFile)
    {
        Updater.StartFinish(newFile, ReceivePage.IsReceiving);
        ExitApp();
    }
}
