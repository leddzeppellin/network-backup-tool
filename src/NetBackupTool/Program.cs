using Nbt.Core;

namespace NetBackupTool;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        L.Set(Settings.Load().Language);

        // Segunda metade da atualização: este processo é o .exe novo (".new"), que substitui o antigo.
        if (args.Length >= 3 && args[0] == Updater.FinishArg)
        {
            Updater.Finish(args[1], int.Parse(args[2]), args.Skip(3).ToArray());
            return;
        }

        var handoff = Handoff.FromArgs(args);
        bool Has(string a) => args.Any(x => x.Trim('-', '/').Equals(a, StringComparison.OrdinalIgnoreCase));

        // Uma instância por usuário: abrir de novo só traz a janela existente para a frente
        // (importante com o modo persistente, que deixa o programa rodando perto do relógio).
        // A instância reaberta como administrador pode não ter acesso aos objetos da comum: segue sem eles.
        Mutex? mutex = null;
        EventWaitHandle? showSignal = null;
        bool first = true;
        try
        {
            mutex = new Mutex(true, @"Local\NetBackupTool.Instance", out first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NetBackupTool.Show");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

        // Reabertura (troca de idioma, atualização): espera a instância anterior terminar de fechar.
        if (!first && Has("reiniciar") && mutex != null)
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }

        if (!first && handoff == null)
        {
            showSignal?.Set();
            return;
        }

        Updater.CleanupOld();

        // "receber" abre direto na recepção; "--bandeja" inicia escondido (usado ao iniciar com o Windows).
        Application.Run(new MainForm(Has("receber"), handoff, Has("bandeja"), showSignal));
    }
}
