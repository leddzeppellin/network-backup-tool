using System.Management;
using System.Security.Principal;

namespace Nbt.Core;

/// <summary>
/// Cópias de sombra (VSS) criadas sob demanda, uma por volume, para ler arquivos que outro
/// programa mantém abertos com exclusividade (Outlook .pst, bancos de dados, etc.).
/// A cópia é um retrato do disco no instante da criação; é apagada no Dispose.
/// Exige executar como administrador e só vale para discos locais.
/// </summary>
public sealed class ShadowCopies : IDisposable
{
    private readonly Dictionary<string, string?> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ids = new();
    private readonly Action<string> _log;

    // IDs criados ficam anotados aqui; se o programa morrer no meio, a próxima execução apaga.
    private static readonly string LedgerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBackupTool", "shadows.txt");

    public ShadowCopies(Action<string> log)
    {
        _log = log;
        CleanupLeftovers();
    }

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) ou ERROR_LOCK_VIOLATION (33).</summary>
    public static bool IsLockError(Exception ex) => ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// Caminho equivalente dentro da cópia de sombra, criando-a na primeira vez para o volume.
    /// Devolve null se não for possível (sem admin, disco de rede, VSS indisponível).
    /// </summary>
    public string? Resolve(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
        root = root.TrimEnd('\\') + "\\";

        string? device;
        lock (_devices)
        {
            if (!_devices.TryGetValue(root, out device))
            {
                device = Create(root);
                _devices[root] = device;
            }
        }
        return device == null ? null : device + "\\" + fullPath[root.Length..];
    }

    private string? Create(string volume)
    {
        if (!IsElevated)
        {
            _log("Arquivo em uso: para copiá-lo via Shadow Copy, abra o programa como administrador.");
            return null;
        }
        try
        {
            if (new DriveInfo(volume).DriveType != DriveType.Fixed) return null;
            _log($"Arquivo em uso: criando Shadow Copy de {volume} (alguns segundos)...");
            using var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
            using var input = cls.GetMethodParameters("Create");
            input["Volume"] = volume;
            input["Context"] = "ClientAccessible";
            using var output = cls.InvokeMethod("Create", input, null);
            var rc = Convert.ToUInt32(output["ReturnValue"]);
            if (rc != 0)
            {
                _log($"Não foi possível criar a Shadow Copy de {volume} (código {rc}).");
                return null;
            }
            var id = (string)output["ShadowID"];
            _ids.Add(id);
            Ledger(add: id);

            using var searcher = new ManagementObjectSearcher($"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{id}'");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    var device = (string)o["DeviceObject"];
                    _log($"Shadow Copy pronta para {volume}: arquivos em uso serão lidos dela.");
                    return device;
                }
            return null;
        }
        catch (Exception ex)
        {
            _log($"Shadow Copy indisponível para {volume}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var id in _ids) Delete(id);
        _ids.Clear();
    }

    private void Delete(string id)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
            foreach (ManagementObject o in searcher.Get())
                using (o) o.Delete();
            Ledger(remove: id);
        }
        catch (Exception ex)
        {
            _log($"Não consegui apagar a Shadow Copy {id}: {ex.Message}");
        }
    }

    private void CleanupLeftovers()
    {
        if (!IsElevated || !File.Exists(LedgerPath)) return;
        foreach (var id in File.ReadAllLines(LedgerPath).Where(l => l.Length > 0))
            Delete(id);
    }

    private static readonly object LedgerLock = new();

    private static void Ledger(string? add = null, string? remove = null)
    {
        lock (LedgerLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
                var ids = File.Exists(LedgerPath) ? File.ReadAllLines(LedgerPath).ToList() : new List<string>();
                if (add != null) ids.Add(add);
                if (remove != null) ids.RemoveAll(i => i == remove);
                File.WriteAllLines(LedgerPath, ids);
            }
            catch { /* anotação é só para limpeza futura */ }
        }
    }
}
