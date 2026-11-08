using Avalonia;
using Nbt.Core;

namespace NetBackupTool;

public sealed record StartOptions(bool Receive = false, bool Hidden = false, Handoff? Handoff = null, Exception? FinishError = null);

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = Settings.Load();
        L.Set(settings.Language);
        bool Has(string a) => args.Any(x => x.Trim('-', '/').Equals(a, StringComparison.OrdinalIgnoreCase));

        // Segunda metade da atualização: este processo é o executável novo, que substitui o antigo.
        Exception? finishError = null;
        if (args.Length >= 3 && args[0] == Updater.FinishArg && int.TryParse(args[2], out var oldPid))
        {
            finishError = Updater.Finish(args[1], oldPid, args.Skip(3).ToArray());
            if (finishError == null) return;
        }

        var handoff = Handoff.FromArgs(args);
        if (finishError == null)
        {
            if (Has("reiniciar")) SingleInstance.WaitForPreviousToExit(TimeSpan.FromSeconds(15));
            else if (handoff == null && SingleInstance.SignalExisting()) return; // já aberto: só mostra a janela
            Updater.CleanupOld();
        }

        // "receber" abre direto na recepção; "--bandeja" inicia escondido (usado ao iniciar com o sistema).
        App.Settings = settings;
        App.Options = new StartOptions(Has("receber"), Has("bandeja") && settings.KeepInTray, handoff, finishError);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Usado também pelo designer do Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
