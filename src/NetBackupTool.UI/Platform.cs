using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Platform.Storage;
using Microsoft.Win32;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>O que muda de um sistema para outro: pastas, iniciar com o sistema, abrir pastas e links.</summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsAndroid => OperatingSystem.IsAndroid();

    // ---- preenchidos pelo app Android (no desktop ficam com o padrão)

    /// <summary>Nome mostrado aos outros computadores. No Android o .NET devolve "localhost": o app troca pelo modelo.</summary>
    public static string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>Android: raiz do armazenamento (/storage/emulated/0).</summary>
    public static string? StorageRoot { get; set; }
    /// <summary>
    /// Versão da Play Store: a política do Google proíbe apontar para outra fonte de atualização,
    /// então "Verificar atualizações" some (a própria loja atualiza o app).
    /// </summary>
    public static bool UpdatesByStore { get; set; }
    /// <summary>Android: garante "Acesso a todos os arquivos"; false se o usuário ainda precisa conceder.</summary>
    public static Func<Task<bool>>? EnsureStorageAccess { get; set; }
    /// <summary>Android: libera o recebimento de broadcast no Wi-Fi enquanto o objeto estiver vivo.</summary>
    public static Func<IDisposable>? AcquireMulticast { get; set; }
    public static Action<string>? OpenUrlHook { get; set; }
    public static Action<string>? OpenFolderHook { get; set; }

    /// <summary>
    /// Caminho local de um item escolhido no seletor. No Android o seletor devolve endereços
    /// content://com.android.externalstorage.documents/... que viram /storage/emulated/0/...
    /// </summary>
    public static string? LocalPath(Avalonia.Platform.Storage.IStorageItem item)
    {
        if (item.TryGetLocalPath() is { } local) return local;
        var uri = item.Path;
        if (uri.Scheme != "content" || uri.Host != "com.android.externalstorage.documents") return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var marker = path.LastIndexOf("/document/", StringComparison.Ordinal);
        var id = marker >= 0 ? path[(marker + "/document/".Length)..]
            : path.StartsWith("/tree/", StringComparison.Ordinal) ? path["/tree/".Length..] : null;
        if (id == null || id.IndexOf(':') is var colon && colon < 0) return null;
        var volume = id[..colon];
        var rel = id[(colon + 1)..];
        var root = volume == "primary" ? StorageRoot ?? "/storage/emulated/0" : "/storage/" + volume;
        return rel.Length == 0 ? root : Path.Combine(root, rel);
    }
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static string Exe => Environment.ProcessPath!;
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Área de Trabalho, Documentos, Imagens, Músicas, Vídeos, Downloads (e Favoritos no Windows).</summary>
    public static List<string> UserFolders()
    {
        if (IsAndroid && StorageRoot != null)
        {
            // Fotos da câmera, imagens, documentos, downloads, vídeos, músicas e as mídias do WhatsApp.
            string[] android = ["DCIM", "Pictures", "Documents", "Download", "Movies", "Music",
                Path.Combine("Android", "media", "com.whatsapp", "WhatsApp", "Media")];
            return android.Select(n => Path.Combine(StorageRoot, n)).Where(Directory.Exists).ToList();
        }
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
        if (OpenFolderHook != null)
        {
            OpenFolderHook(path);
            return;
        }
        if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else Process.Start(IsMac ? "open" : "xdg-open", path);
    }

    public static void OpenUrl(string url)
    {
        if (OpenUrlHook != null)
        {
            OpenUrlHook(url);
            return;
        }
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
