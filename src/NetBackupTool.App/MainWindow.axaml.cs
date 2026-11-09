using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>Janela do programa de desktop: hospeda a MainView e cuida da bandeja e do fechar/reabrir.</summary>
public partial class MainWindow : Window, IAppHost
{
    public static WindowIcon AppIcon { get; } = new(AssetLoader.Open(new Uri("avares://NetBackupTool.UI/Assets/icon.ico")));
    public static Bitmap AppBitmap { get; } = new(AssetLoader.Open(new Uri("avares://NetBackupTool.UI/Assets/icon.png")));

    public MainView View { get; }
    private Settings Settings => App.Settings;

    private readonly TrayIcon _tray = new();
    private readonly NativeMenuItem _trayToggle = new();
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow() : this(new StartOptions()) { } // designer

    public MainWindow(StartOptions options)
    {
        InitializeComponent();
        if (ShadowCopies.IsElevated && Platform.IsWindows) Title = L.T("app.admin");
        View = new MainView(this, options);
        Content = View;

        SetupTray();
        View.ReceivingChanged += receiving =>
        {
            _trayToggle.Header = L.T(receiving ? "recv.stop" : "recv.start");
            var tip = receiving ? L.T("tray.receiving", View.ReceivePage.CurrentCode) : L.T("tray.stopped");
            _tray.ToolTipText = tip.Length > 120 ? tip[..120] : tip;
        };
        if (options.Receive) Opened += (_, _) => View.ReceivePage.EnsureReceiving();
    }

    /// <summary>Iniciado com o sistema: sem janela, só o ícone da bandeja, já recebendo.</summary>
    public void StartHidden()
    {
        View.ShowReceivePage();
        if (Settings.DestinationRoot.Length == 0) ShowWindow();
        View.ReceivePage.EnsureReceiving();
    }

    // ---------------------------------------------------------------- IAppHost

    public bool IsDesktop => true;

    public void ShowWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.MainWindow == null)
            d.MainWindow = this;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Aviso no canto da tela quando a janela não está à vista.</summary>
    public void Notify(string title, string text)
    {
        if (IsVisible && IsActive) return;
        Toast.Show(title, text, ShowWindow);
    }

    public void SetTrayVisible(bool visible) => _tray.IsVisible = visible;

    public void KeepAwake(bool on) { }

    /// <summary>Reabre o programa (troca de idioma), mantendo a recepção se estava ligada.</summary>
    public void Restart()
    {
        var psi = new ProcessStartInfo(Platform.Exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--reiniciar");
        if (View.ReceivePage.IsReceiving) psi.ArgumentList.Add("receber");
        View.ReceivePage.StopReceiver();
        Process.Start(psi);
        ExitApp();
    }

    /// <summary>Fecha para o executável novo concluir a atualização.</summary>
    public void ExitForUpdate(string newFile)
    {
        Updater.StartFinish(newFile, View.ReceivePage.IsReceiving);
        ExitApp();
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
        Shutdown();
    }

    // ---------------------------------------------------------------- bandeja

    private void SetupTray()
    {
        _tray.Icon = AppIcon;
        _tray.ToolTipText = L.T("tray.stopped");
        var open = new NativeMenuItem(L.T("tray.open"));
        open.Click += (_, _) => ShowWindow();
        _trayToggle.Header = L.T("recv.start");
        _trayToggle.Click += (_, _) => View.ReceivePage.Toggle();
        var exit = new NativeMenuItem(L.T("tray.exit"));
        exit.Click += (_, _) => ExitApp();
        _tray.Menu = new NativeMenu { Items = { open, _trayToggle, new NativeMenuItemSeparator(), exit } };
        _tray.Clicked += (_, _) => ShowWindow();
        _tray.IsVisible = Settings.KeepInTray;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    // ---------------------------------------------------------------- fechar

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Modo persistente: o X só esconde a janela; a recepção segue com o ícone perto do relógio.
        if (!_exiting && Settings.KeepInTray && View.ReceivePage.IsReceiving && !View.SendPage.Busy)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Toast.Show(L.T("tray.keepsTitle"), L.T("tray.keepsText"), ShowWindow);
            }
            return;
        }
        if (!_exiting && View.Busy)
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
        if (await View.Ask(L.T("busy.close"), yesNo: true)) ExitApp();
    }

    private void Shutdown()
    {
        View.SendPage.CancelSend();
        View.ReceivePage.StopReceiver();
        _tray.IsVisible = false;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
