using System.IO.Enumeration;

namespace Nbt.Core;

public sealed record FileEntry(string RelativePath, long Size, long LastWriteUtcTicks, bool IsDirectory)
{
    /// <summary>Caminho completo no PC de origem (só existe do lado do remetente).</summary>
    public string? SourcePath { get; init; }
}

public sealed class ScanResult
{
    public List<FileEntry> Entries { get; } = new();
    public List<string> Warnings { get; } = new();
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public int CloudOnlySkipped { get; set; }
}

public static class Manifest
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: arquivo do OneDrive que só existe na nuvem.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static readonly string DefaultExcludes = "*.tmp;~$*;Thumbs.db";

    /// <summary>
    /// Espelha o caminho de origem no destino: C:\Users\joao\Docs → C\Users\joao\Docs,
    /// \\srv\share\x → UNC\srv\share\x. Fica estável entre execuções, o que permite retomar.
    /// </summary>
    public static string MapToRelative(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath).TrimEnd('\\', '/');
        if (fullPath.StartsWith(@"\\"))
            return "UNC\\" + fullPath[2..];
        if (fullPath.Length >= 2 && fullPath[1] == ':')
            return char.ToUpperInvariant(fullPath[0]) + fullPath[2..];
        return fullPath.TrimStart('\\', '/');
    }

    public static ScanResult Scan(IEnumerable<string> sources, string excludePatterns, CancellationToken ct, Action<int>? progress = null)
    {
        var result = new ScanResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludes = excludePatterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool Excluded(string name) => excludes.Any(p => FileSystemName.MatchesSimpleExpression(p, name));

        void AddFile(FileInfo fi)
        {
            if (Excluded(fi.Name)) return;
            if ((fi.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0)
            {
                result.CloudOnlySkipped++;
                return;
            }
            var rel = MapToRelative(fi.FullName);
            if (!seen.Add(rel)) return;
            result.Entries.Add(new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc.Ticks, false) { SourcePath = fi.FullName });
            result.TotalBytes += fi.Length;
            result.FileCount++;
            if (result.FileCount % 500 == 0) progress?.Invoke(result.FileCount);
        }

        void AddDir(DirectoryInfo root)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                var rel = MapToRelative(dir.FullName);
                if (seen.Add(rel))
                    result.Entries.Add(new FileEntry(rel, 0, dir.LastWriteTimeUtc.Ticks, true) { SourcePath = dir.FullName });

                IEnumerable<FileSystemInfo> children;
                try
                {
                    // AttributesToSkip = 0: por padrão o .NET pula ocultos e de sistema, e aqui queremos tudo.
                    children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = 0,
                    }).ToList();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(L.T("scan.noAccess", dir.FullName, ex.Message));
                    continue;
                }

                foreach (var child in children)
                {
                    if (child is DirectoryInfo sub)
                    {
                        // Junções/links (ex.: "Dados de Aplicativos", "Meus Documentos") apontam para
                        // pastas que já estão na árvore ou criam laços infinitos.
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 && sub.LinkTarget != null)
                            continue;
                        if (Excluded(sub.Name)) continue;
                        stack.Push(sub);
                    }
                    else if (child is FileInfo fi)
                    {
                        AddFile(fi);
                    }
                }
            }
        }

        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(src)) AddDir(new DirectoryInfo(src));
            else if (File.Exists(src)) AddFile(new FileInfo(src));
            else result.Warnings.Add(L.T("scan.notFound", src));
        }
        progress?.Invoke(result.FileCount);
        return result;
    }

    public static void Write(BinaryWriter w, IReadOnlyList<FileEntry> entries)
    {
        w.Write(entries.Count);
        foreach (var e in entries)
        {
            w.Write(e.RelativePath);
            w.Write(e.Size);
            w.Write(e.LastWriteUtcTicks);
            w.Write(e.IsDirectory);
        }
    }

    public static List<FileEntry> Read(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 50_000_000) throw new InvalidDataException(L.T("man.invalid"));
        var list = new List<FileEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var path = r.ReadString();
            var size = r.ReadInt64();
            var ticks = r.ReadInt64();
            var isDir = r.ReadBoolean();
            if (size < 0) throw new InvalidDataException(L.T("man.badSize"));
            list.Add(new FileEntry(path, size, ticks, isDir));
        }
        return list;
    }

    /// <summary>
    /// Resolve um caminho relativo vindo da rede dentro de baseDir, recusando qualquer
    /// tentativa de escapar da pasta (.., caminho absoluto, unidade).
    /// </summary>
    public static string ResolveSafe(string baseDir, string relative)
    {
        var parts = relative.Split('\\', '/');
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..")
                throw new InvalidDataException(L.T("man.badPath", relative));
            var fixedPart = new string(p.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
            if (fixedPart.Length == 0) fixedPart = "_";
            clean.Add(fixedPart);
        }
        var full = Path.GetFullPath(Path.Combine(baseDir, Path.Combine(clean.ToArray())));
        var root = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
            throw new InvalidDataException(L.T("man.outside", relative));
        return full;
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return s.Length == 0 ? "remetente" : s;
    }
}
