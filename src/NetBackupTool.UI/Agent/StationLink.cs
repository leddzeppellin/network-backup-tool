using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Nbt.Core;

namespace NetBackupTool.Agent;

/// <summary>Ligação desta estação com as pastas da empresa (guardada nas configurações do usuário).</summary>
public sealed class StationConfig
{
    public string Server { get; set; } = "";
    /// <summary>Android: endereço IP achado na busca (o celular não resolve nomes de computador Windows).</summary>
    public string? Address { get; set; }
    public string Share { get; set; } = "";
    public string Login { get; set; } = "";
    /// <summary>Letra da unidade, ex.: "Z:".</summary>
    public string Drive { get; set; } = "";
    /// <summary>Atalhos que este programa fixou (só esses ele tira depois).</summary>
    public List<string> Pinned { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string Unc => $@"\\{Server}\{Share}";

    private static string FilePath => Path.Combine(Settings.Dir, "estacao.json");

    public static StationConfig? Load()
    {
        try { return JsonSerializer.Deserialize<StationConfig>(File.ReadAllText(FilePath)) is { Server.Length: > 0 } c ? c : null; }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }
}

/// <summary>
/// Windows: unidade mapeada permanente (Z:), senha guardada no Gerenciador de Credenciais e
/// pastas fixadas no Acesso rápido. Tudo como o próprio usuário, sem pedir administrador.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StationLink
{
    /// <summary>Conecta e grava a configuração. Devolve null se deu certo, ou a mensagem para a pessoa.</summary>
    public static string? Connect(string server, string share, string login, string password, out StationConfig? config)
    {
        config = null;
        server = server.Trim().TrimStart('\\');
        share = share.Trim().Trim('\\');
        login = login.Trim();
        // "SERVIDOR\ana" ou só "ana" (conta local do servidor).
        var user = login.Contains('\\') ? login : $@"{server}\{login}";
        var unc = $@"\\{server}\{share}";

        var drive = FreeDrive();
        if (drive == null) return L.T("sta.noDrive");

        // Guarda a senha no Windows: a unidade volta sozinha depois de reiniciar.
        if (!SaveCredential(server, user, password)) return L.T("sta.credFailed", Marshal.GetLastWin32Error());

        int rc = AddConnection(drive, unc, user, password);
        if (rc == 1219)
        {
            // Já havia conexão com este servidor usando outra conta: desfaz e tenta de novo.
            foreach (var old in ConnectionsTo(server)) WNetCancelConnection2(old, CONNECT_UPDATE_PROFILE, true);
            rc = AddConnection(drive, unc, user, password);
        }
        if (rc != 0)
        {
            DeleteCredential(server);
            return rc switch
            {
                86 or 1326 or 2202 => L.T("sta.badLogin"),
                1327 or 1331 or 1909 => L.T("sta.accountProblem"),
                53 or 1231 or 1203 or 64 => L.T("sta.noServer", server),
                67 => L.T("sta.noShare", share, server),
                5 => L.T("sta.denied"),
                _ => L.T("sta.connectFailed", new System.ComponentModel.Win32Exception(rc).Message),
            };
        }
        SetLabel(server, share);
        config = new StationConfig { Server = server, Share = share, Login = login.Contains('\\') ? login.Split('\\')[1] : login, Drive = drive };
        config.Save();
        return null;
    }

    /// <summary>A unidade está conectada (ou pelo menos lembrada pelo Windows)?</summary>
    public static bool IsMapped(StationConfig c) => Directory.Exists(c.Drive + "\\");

    /// <summary>Reconecta a unidade se o Windows ainda não a ligou (ex.: logo após iniciar).</summary>
    public static void Reconnect(StationConfig c)
    {
        if (IsMapped(c)) return;
        AddConnection(c.Drive, c.Unc, null, null);
    }

    public static void Disconnect(StationConfig c)
    {
        foreach (var p in c.Pinned) QuickAccess.Unpin(p);
        WNetCancelConnection2(c.Drive, CONNECT_UPDATE_PROFILE, true);
        WNetCancelConnection2(c.Unc, CONNECT_UPDATE_PROFILE, true);
        DeleteCredential(c.Server);
        RemoveLabel(c.Server, c.Share);
        StationConfig.Delete();
    }

    /// <summary>Pastas que esta pessoa enxerga (a enumeração por acesso já esconde as outras).</summary>
    public static List<string> Folders(StationConfig c)
    {
        try
        {
            return new DirectoryInfo(c.Drive + "\\").GetDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                .Select(d => d.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Lê o arquivo de atalhos da pessoa no servidor e acerta o Acesso rápido. Devolve as pastas fixadas.</summary>
    public static List<string> SyncPins(StationConfig c)
    {
        StationInfo? info = null;
        try
        {
            var file = Path.Combine(c.Drive + "\\", Agent.ShareConfig.InfoDir, c.Login + ".json");
            info = JsonSerializer.Deserialize<StationInfo>(File.ReadAllText(file));
        }
        catch { /* servidor antigo ou sem atalhos para esta pessoa */ }

        var wanted = (info?.Pins ?? new())
            .Select(f => Path.Combine(c.Drive + "\\", f))
            .Where(Directory.Exists)
            .ToList();
        foreach (var p in c.Pinned.Where(p => !wanted.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            QuickAccess.Unpin(p);
            c.Pinned.Remove(p);
        }
        foreach (var p in wanted)
        {
            if (QuickAccess.Pin(p) && !c.Pinned.Contains(p, StringComparer.OrdinalIgnoreCase)) c.Pinned.Add(p);
        }
        c.Save();
        return c.Pinned.ToList();
    }

    // ---------------------------------------------------------------- unidade

    private static string? FreeDrive()
    {
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        // Unidades lembradas mas desconectadas não aparecem em GetDrives.
        using (var net = Registry.CurrentUser.OpenSubKey("Network"))
            foreach (var n in net?.GetSubKeyNames() ?? []) if (n.Length == 1) used.Add(char.ToUpperInvariant(n[0]));
        for (char d = 'Z'; d >= 'G'; d--) if (!used.Contains(d)) return $"{d}:";
        return null;
    }

    private static int AddConnection(string drive, string unc, string? user, string? password)
    {
        var res = new NETRESOURCE { dwType = RESOURCETYPE_DISK, lpLocalName = drive, lpRemoteName = unc };
        return WNetAddConnection2(ref res, password, user, CONNECT_UPDATE_PROFILE);
    }

    /// <summary>Conexões existentes com o servidor (unidades e caminhos \\servidor\...).</summary>
    private static List<string> ConnectionsTo(string server)
    {
        var list = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("net", "use") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var prefix = $@"\\{server}\";
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var remote = parts.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (remote == null) continue;
                var local = parts.FirstOrDefault(x => x.Length == 2 && x[1] == ':');
                list.Add(local ?? remote);
            }
            p.WaitForExit();
        }
        catch { }
        return list;
    }

    /// <summary>Nome da unidade no Explorer: "Empresa (SERVIDOR)" em vez do caminho.</summary>
    private static void SetLabel(string server, string share)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(LabelKey(server, share));
            k.SetValue("_LabelFromReg", $"{share} ({server})");
        }
        catch { }
    }

    private static string LabelKey(string server, string share) =>
        $@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\##{server}#{share}";

    private static void RemoveLabel(string server, string share)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(LabelKey(server, share), throwOnMissingSubKey: false); } catch { }
    }

    // ---------------------------------------------------------------- senha

    private static bool SaveCredential(string server, string user, string password)
    {
        var secret = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD, TargetName = server, UserName = user,
                CredentialBlob = blob, CredentialBlobSize = secret.Length, Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            return CredWrite(ref cred, 0);
        }
        finally
        {
            for (int i = 0; i < secret.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Array.Clear(secret);
        }
    }

    private static void DeleteCredential(string server) => CredDelete(server, CRED_TYPE_DOMAIN_PASSWORD, 0);

    // ---------------------------------------------------------------- Win32

    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_UPDATE_PROFILE = 1;
    private const int CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE res, string? password, string? user, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL cred, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);
}

/// <summary>Fixar e tirar pastas do Acesso rápido do Explorer (verbo do shell, como o clique direito).</summary>
[SupportedOSPlatform("windows")]
public static class QuickAccess
{
    private const string Home = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public static bool Pin(string path) => OnSta(() =>
    {
        if (FindPinned(path) != null) return true;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic? folder = shell.Namespace(path);
        if (folder == null) return false;
        folder.Self.InvokeVerb("pintohome");
        return true;
    });

    public static void Unpin(string path) => OnSta(() =>
    {
        dynamic? item = FindPinned(path);
        item?.InvokeVerb("unpinfromhome");
        return true;
    });

    private static dynamic? FindPinned(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic? home = shell.Namespace(Home);
        if (home == null) return null;
        foreach (dynamic item in home.Items())
        {
            string? p = item.Path;
            if (p == null || !p.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            // O Acesso rápido mistura fixadas e frequentes.
            if (item.ExtendedProperty("System.Home.IsPinned") is true) return item;
        }
        return null;
    }

    /// <summary>O shell quer thread STA; erros viram "não deu".</summary>
    private static bool OnSta(Func<bool> f)
    {
        bool ok = false;
        var t = new Thread(() => { try { ok = f(); } catch { ok = false; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(20));
        return ok;
    }
}
