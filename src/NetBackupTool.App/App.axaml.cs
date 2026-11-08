using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

public partial class App : Application
{
    public static Settings Settings { get; set; } = new();
    public static StartOptions Options { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyTheme(Settings.Theme);

            if (Options.FinishError is { } error)
            {
                // A troca do executável falhou: avisa e oferece a página de download.
                var dlg = Dialogs.Create(L.T("upd.cantReplace", error.Message), L.T("upd.title"), yesNo: true);
                dlg.Closed += (_, _) =>
                {
                    if (dlg.Result) Platform.OpenUrl(Updater.ProjectUrl + "/releases/latest");
                    desktop.Shutdown();
                };
                dlg.Show();
            }
            else
            {
                var window = new MainWindow(Options);
                if (!Options.Hidden) desktop.MainWindow = window;
                else window.StartHidden();
                SingleInstance.Listen(() => Dispatcher.UIThread.Post(window.ShowFromBackground));
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                    activatable.Activated += (_, e) =>
                    {
                        if (e.Kind == ActivationKind.Reopen) window.ShowFromBackground();
                    };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme) =>
        Current!.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
