namespace NetBackupTool;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // "NetBackupTool.exe receber" abre direto na recepção (útil num atalho no PC do técnico).
        bool receive = args.Any(a => a.Trim('-', '/').Equals("receber", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(receive, Handoff.FromArgs(args)));
    }
}
