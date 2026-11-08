using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool;

public sealed class Settings
{
    public string DestinationRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Backups de clientes");
    public int Connections { get; set; } = 4;
    public string Excludes { get; set; } = Manifest.DefaultExcludes;
    public string LastHost { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetBackupTool", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* configurações são conveniência; não atrapalhar a cópia */ }
    }
}

/// <summary>Estado passado para a instância reaberta como administrador.</summary>
public sealed class Handoff
{
    public const string Arg = "--continuar";

    public List<string> Sources { get; set; } = new();
    public List<string> UserFolders { get; set; } = new();
    public string Excludes { get; set; } = "";
    public int Connections { get; set; } = 4;
    public string Target { get; set; } = "";

    public string Write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nbt_continuar_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this));
        return path;
    }

    public static Handoff? FromArgs(string[] args)
    {
        int i = Array.IndexOf(args, Arg);
        if (i < 0 || i + 1 >= args.Length) return null;
        try
        {
            var h = JsonSerializer.Deserialize<Handoff>(File.ReadAllText(args[i + 1]));
            File.Delete(args[i + 1]);
            return h;
        }
        catch { return null; }
    }
}
