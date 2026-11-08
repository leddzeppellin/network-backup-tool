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
