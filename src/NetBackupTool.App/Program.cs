using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Versão instalada: o mesmo executável é o serviço do Windows e o instalador o registra.
        if (OperatingSystem.IsWindows() && args.Length > 0)
        {
            switch (args[0])
            {
                case AgentSetup.RunArg:
                    System.ServiceProcess.ServiceBase.Run(new AgentWindowsService());
                    return 0;
                case AgentSetup.ConsoleArg:
                    return AgentSetup.RunConsole();
                case AgentSetup.InstallArg:
                    return AgentSetup.Install(args.Length > 1 ? args[1] : null);
                case AgentSetup.RemoveArg:
                    return AgentSetup.Remove();
                case NetBackupTool.Agent.AgentClient.CmdArg when args.Length > 1:
                    return AgentSetup.Command(args[1], args.Length > 2 ? args[2] : null);
            }
        }

        var settings = Settings.Load();
        L.Set(settings.Language);
        bool Has(string a) => args.Any(x => x.Trim('-', '/').Equals(a, StringComparison.OrdinalIgnoreCase));

        // Segunda metade da atualização: este processo é o executável novo, que substitui o antigo.
        Exception? finishError = null;
        if (args.Length >= 3 && args[0] == Updater.FinishArg && int.TryParse(args[2], out var oldPid))
        {
            finishError = Updater.Finish(args[1], oldPid, args.Skip(3).ToArray());
            if (finishError == null) return 0;
        }

        var handoff = Handoff.FromArgs(args);
        if (finishError == null)
        {
            if (Has("reiniciar")) SingleInstance.WaitForPreviousToExit(TimeSpan.FromSeconds(15));
            else if (handoff == null && SingleInstance.SignalExisting()) return 0; // já aberto: só mostra a janela
            Updater.CleanupOld();
        }

        // "receber" abre direto na recepção; "--bandeja" inicia escondido (usado ao iniciar com o sistema).
        App.Settings = settings;
        App.Options = new StartOptions(Has("receber"), Has("bandeja") && settings.KeepInTray, handoff, finishError);
        App.Startup = Start;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static void Start(Application app, IApplicationLifetime? lifetime)
    {
        if (lifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var options = App.Options;

        if (options.FinishError is { } error)
        {
            // A troca do executável falhou: avisa e oferece a página de download.
            var dlg = Dialogs.Create(L.T("upd.cantReplace", error.Message), L.T("upd.title"), yesNo: true);
            dlg.Closed += (_, _) =>
            {
                if (dlg.Result) Platform.OpenUrl(Updater.ProjectUrl + "/releases/latest");
                desktop.Shutdown();
            };
            dlg.Show();
            return;
        }

        var window = new MainWindow(options);
        if (!options.Hidden) desktop.MainWindow = window;
        else window.StartHidden();
        SingleInstance.Listen(() => Dispatcher.UIThread.Post(window.ShowWindow));
        // macOS: clicar no ícone do Dock com a janela escondida volta a mostrá-la.
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) window.ShowWindow();
            };
    }

    // Usado também pelo designer do Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
