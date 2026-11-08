namespace NetBackupTool;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var handoff = Handoff.FromArgs(args);

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

        if (!first && handoff == null)
        {
            showSignal?.Set();
            return;
        }

        // "receber" abre direto na recepção; "--bandeja" inicia escondido (usado ao iniciar com o Windows).
        bool Has(string a) => args.Any(x => x.Trim('-', '/').Equals(a, StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(Has("receber"), handoff, Has("bandeja"), showSignal));
    }
}
