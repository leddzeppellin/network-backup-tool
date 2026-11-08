using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>O que muda de um sistema para outro: pastas, iniciar com o sistema, abrir pastas e links.</summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static string Exe => Environment.ProcessPath!;
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DefaultDestination()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var root = Directory.Exists(desktop) && desktop.Length > 0 ? desktop : Home;
        return Path.Combine(root, L.T("dest.default"));
    }

    /// <summary>Área de Trabalho, Documentos, Imagens, Músicas, Vídeos, Downloads (e Favoritos no Windows).</summary>
    public static List<string> UserFolders()
    {
        if (IsWindows)
        {
            Environment.SpecialFolder[] folders =
            [
                Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites,
            ];
            var list = folders.Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToList();
            list.Add(Path.Combine(Home, "Downloads"));
            return list;
        }
        // Linux e macOS: pastas padrão dentro da pasta pessoal (no Unix, "MyDocuments" é a própria home).
        string[] names = IsMac
            ? ["Desktop", "Documents", "Pictures", "Music", "Movies", "Downloads"]
            : ["Desktop", "Documents", "Pictures", "Music", "Videos", "Downloads",
               "Área de Trabalho", "Documentos", "Imagens", "Música", "Vídeos", "Escritorio", "Imágenes", "Descargas"];
        return names.Select(n => Path.Combine(Home, n)).Where(Directory.Exists).ToList();
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else Process.Start(IsMac ? "open" : "xdg-open", path);
    }

    public static void OpenUrl(string url)
    {
        if (IsWindows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else Process.Start(IsMac ? "open" : "xdg-open", url);
    }

    // ---------------------------------------------------------------- iniciar com o sistema

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetBackupTool";
    private static string LinuxAutostart => Path.Combine(Home, ".config", "autostart", "netbackuptool.desktop");
    private static string MacAgent => Path.Combine(Home, "Library", "LaunchAgents", "io.github.leddzeppellin.netbackuptool.plist");

    public static bool IsAutostart()
    {
        if (OperatingSystem.IsWindows()) return WinIsAutostart();
        return File.Exists(IsMac ? MacAgent : LinuxAutostart);
    }

    /// <summary>Só para o usuário atual, sem administrador. Abre escondido e já recebendo.</summary>
    public static void SetAutostart(bool on)
    {
        if (OperatingSystem.IsWindows())
        {
            WinSetAutostart(on);
            return;
        }
        var file = IsMac ? MacAgent : LinuxAutostart;
        if (!on)
        {
            File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, IsMac
            ? $"""
              <?xml version="1.0" encoding="UTF-8"?>
              <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
              <plist version="1.0">
              <dict>
                <key>Label</key><string>io.github.leddzeppellin.netbackuptool</string>
                <key>ProgramArguments</key>
                <array><string>{Exe}</string><string>receber</string><string>--bandeja</string></array>
                <key>RunAtLoad</key><true/>
              </dict>
              </plist>
              """
            : $"""
              [Desktop Entry]
              Type=Application
              Name=Network Backup Tool
              Exec="{Exe}" receber --bandeja
              X-GNOME-Autostart-enabled=true
              """);
    }

    [SupportedOSPlatform("windows")]
    private static bool WinIsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    [SupportedOSPlatform("windows")]
    private static void WinSetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Exe}\" receber --bandeja");
        else key.DeleteValue(RunValue, false);
    }

    // ---------------------------------------------------------------- só Windows

    /// <summary>Regra de entrada só para este executável, em todos os perfis de rede. Pede admin.</summary>
    public static bool? AllowFirewall()
    {
        var args = $"advfirewall firewall add rule name=\"Network Backup Tool\" dir=in action=allow program=\"{Exe}\" enable=yes profile=any";
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // UAC recusado
        }
    }

    /// <summary>Reabre como administrador levando o estado da tela Enviar. false se o UAC foi recusado.</summary>
    public static bool StartElevated(Handoff state)
    {
        var file = state.Write();
        try
        {
            var psi = new ProcessStartInfo(Exe) { Verb = "runas", UseShellExecute = true };
            psi.ArgumentList.Add(Handoff.Arg);
            psi.ArgumentList.Add(file);
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            File.Delete(file);
            return false;
        }
    }
}
