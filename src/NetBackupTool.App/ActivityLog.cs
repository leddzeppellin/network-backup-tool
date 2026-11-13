using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text.Json;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Registro de atividade da pasta da empresa: "Ana alterou Financeiro\Orçamento.xlsx".
/// O aviso de mudança da pasta (FileSystemWatcher) diz o quê; a auditoria do Windows (evento 4663
/// no log de Segurança) diz quem. Grava uma linha JSON por ação em atividade\aaaa-mm.jsonl.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActivityLog : IDisposable
{
    private const int KeepMonths = 12;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);   // espera a auditoria chegar
    private static readonly TimeSpan Match = TimeSpan.FromSeconds(60);   // janela para achar quem fez
    private static readonly TimeSpan Repeat = TimeSpan.FromMinutes(2);   // "alterou" repetido vira uma linha

    private readonly string _root;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<Change> _changes = new();
    private readonly Dictionary<string, (string User, DateTime Time)> _who = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string User, DateTime Time)> _lastChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly Action<string> _log;
    private long _lastRecord = -1;
    private int _busy;
    private bool _auditWarned;

    private sealed record Change(DateTime Time, WatcherChangeTypes Kind, string Path, string? OldPath);

    public static string Dir => Path.Combine(AgentConfig.Dir, "atividade");

    public ActivityLog(string root, Action<string> log)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _log = log;
        _lastRecord = LatestRecord();
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Enqueue(WatcherChangeTypes.Created, e.FullPath, null);
        _watcher.Changed += (_, e) => Enqueue(WatcherChangeTypes.Changed, e.FullPath, null);
        _watcher.Deleted += (_, e) => Enqueue(WatcherChangeTypes.Deleted, e.FullPath, null);
        _watcher.Renamed += (_, e) => Enqueue(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
        _watcher.Error += (_, e) => _log(e.GetException().Message);
        _watcher.EnableRaisingEvents = true;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _watcher.Dispose();
        Tick(final: true);
    }

    private void Enqueue(WatcherChangeTypes kind, string path, string? old)
    {
        var rel = Relative(path);
        if (rel == null || rel.Length == 0) return;
        _changes.Enqueue(new Change(DateTime.Now, kind, path, old));
    }

    /// <summary>Caminho relativo à pasta da empresa, ou null se estiver fora ou na pasta interna .nbt.</summary>
    private string? Relative(string path)
    {
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = path[_root.Length..].TrimStart('\\');
        if (rel.Equals(ShareConfig.InfoDir, StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith(ShareConfig.InfoDir + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        return rel;
    }

    /// <summary>Arquivos temporários do Office e de editores: não interessam a ninguém.</summary>
    public static bool IsTemp(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$") || name.StartsWith(".~lock") || name.StartsWith("~WRL", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               (name.Length == 8 && !name.Contains('.') && name.All(char.IsAsciiHexDigit)); // Excel salva assim
    }

    // ---------------------------------------------------------------- processamento

    private void Tick(bool final = false)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            try { ReadAudit(); }
            catch (Exception ex)
            {
                // Sem a auditoria ainda dá para registrar o quê (sem o quem).
                if (!_auditWarned) _log(ex.Message);
                _auditWarned = true;
            }
            var now = DateTime.Now;
            var ready = new List<Change>();
            while (_changes.TryPeek(out var c) && (final || now - c.Time >= Settle) && _changes.TryDequeue(out c)) ready.Add(c);
            var entries = new List<ActivityEntry>();
            foreach (var c in ready) if (Describe(c) is { } e) entries.Add(e);
            if (entries.Count > 0) Append(entries);
            Forget(now);
        }
        catch (Exception ex) { _log(ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private ActivityEntry? Describe(Change c)
    {
        var rel = Relative(c.Path);
        if (rel == null) return null;
        bool isDir = Directory.Exists(c.Path);
        string? oldRel = c.OldPath == null ? null : Relative(c.OldPath);

        switch (c.Kind)
        {
            case WatcherChangeTypes.Changed:
                // Pasta "mudou" quando algo dentro dela muda: a linha útil é a do arquivo.
                if (isDir || IsTemp(c.Path) || !File.Exists(c.Path)) return null;
                return Repeated(rel, "changed", c) ? null : Entry(c, "changed", rel);
            case WatcherChangeTypes.Created:
                if (IsTemp(c.Path)) return null;
                // Copiar um arquivo gera "criou" e logo "alterou": fica só o "criou".
                _lastChanged[rel] = (Who(c), c.Time);
                return Entry(c, "created", rel);
            case WatcherChangeTypes.Deleted:
                if (IsTemp(c.Path)) return null;
                return Entry(c, "deleted", rel);
            case WatcherChangeTypes.Renamed:
                if (IsTemp(c.Path)) return null; // Word renomeia o original para ~WRL...tmp ao salvar
                // Salvar do Office: grava num temporário e renomeia para o nome final = alterou.
                if (c.OldPath != null && IsTemp(c.OldPath))
                    return Repeated(rel, "changed", c) ? null : Entry(c, "changed", rel);
                return Entry(c, "renamed", rel, oldRel);
        }
        return null;
    }

    private ActivityEntry Entry(Change c, string action, string rel, string? oldRel = null) =>
        new(c.Time, Who(c), action, rel, oldRel);

    /// <summary>Mesma pessoa alterando o mesmo arquivo de novo em pouco tempo: uma linha só.</summary>
    private bool Repeated(string rel, string action, Change c)
    {
        var who = Who(c);
        if (_lastChanged.TryGetValue(rel, out var last) && last.User == who && c.Time - last.Time < Repeat) return true;
        _lastChanged[rel] = (who, c.Time);
        return false;
    }

    /// <summary>Quem fez: auditoria do próprio arquivo, do nome antigo ou da pasta onde ele está.</summary>
    private string Who(Change c)
    {
        foreach (var p in new[] { c.Path, c.OldPath, Path.GetDirectoryName(c.Path) })
        {
            if (p != null && _who.TryGetValue(p, out var w) && (c.Time - w.Time).Duration() < Match) return w.User;
        }
        return "";
    }

    private void Forget(DateTime now)
    {
        foreach (var k in _who.Where(kv => now - kv.Value.Time > Match + Match).Select(kv => kv.Key).ToList()) _who.Remove(k);
        foreach (var k in _lastChanged.Where(kv => now - kv.Value.Time > Repeat).Select(kv => kv.Key).ToList()) _lastChanged.Remove(k);
    }

    // ---------------------------------------------------------------- auditoria do Windows

    private static long LatestRecord()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=4663)]]") { ReverseDirection = true });
            using var e = reader.ReadEvent();
            return e?.RecordId ?? 0;
        }
        catch { return 0; }
    }

    private void ReadAudit()
    {
        var query = new EventLogQuery("Security", PathType.LogName, $"*[System[(EventID=4663) and (EventRecordID > {_lastRecord})]]");
        using var reader = new EventLogReader(query);
        for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
        {
            using (e)
            {
                _lastRecord = Math.Max(_lastRecord, e.RecordId ?? _lastRecord);
                var p = e.Properties;
                if (p.Count < 7) continue;
                var sid = p[0].Value?.ToString() ?? "";
                var user = p[1].Value?.ToString() ?? "";
                var obj = p[6].Value?.ToString() ?? "";
                // Contas do sistema (o próprio serviço, antivírus, indexador) não são pessoas.
                if (user.EndsWith('$') || sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue;
                if (!obj.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) continue;
                _who[obj.TrimEnd('\\')] = (user, e.TimeCreated ?? DateTime.Now);
            }
        }
    }

    // ---------------------------------------------------------------- arquivo

    private static readonly object FileGate = new();

    private static void Append(List<ActivityEntry> entries)
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(Dir);
            foreach (var g in entries.GroupBy(e => e.Time.ToString("yyyy-MM")))
                File.AppendAllLines(Path.Combine(Dir, g.Key + ".jsonl"), g.Select(e => JsonSerializer.Serialize(e)));
            // Guarda 12 meses.
            var oldest = DateTime.Now.AddMonths(-KeepMonths).ToString("yyyy-MM");
            foreach (var f in Directory.GetFiles(Dir, "*.jsonl").Where(f => string.CompareOrdinal(Path.GetFileNameWithoutExtension(f), oldest) < 0))
                File.Delete(f);
        }
    }

    /// <summary>Linhas dos últimos dias, da mais nova para a mais antiga (no máximo <paramref name="max"/>).</summary>
    public static List<ActivityEntry> Read(int days, int max = 5000)
    {
        var since = DateTime.Now.AddDays(-days);
        var result = new List<ActivityEntry>();
        lock (FileGate)
        {
            if (!Directory.Exists(Dir)) return result;
            foreach (var f in Directory.GetFiles(Dir, "*.jsonl").OrderByDescending(f => f))
            {
                if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(f), since.ToString("yyyy-MM")) < 0) break;
                foreach (var line in File.ReadLines(f).Reverse())
                {
                    ActivityEntry? e;
                    try { e = JsonSerializer.Deserialize<ActivityEntry>(line); } catch { continue; }
                    if (e == null || e.Time < since) continue;
                    result.Add(e);
                    if (result.Count >= max) return result;
                }
            }
        }
        return result;
    }
}
