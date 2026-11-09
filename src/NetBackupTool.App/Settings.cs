using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>Configurações do usuário (mesmo arquivo e formato da versão 1.x).</summary>
public sealed class Settings
{
    /// <summary>Vazio até o usuário escolher (no macOS a Área de Trabalho exige permissão).</summary>
    public string DestinationRoot { get; set; } = "";
    public int Connections { get; set; } = 4;
    public string Excludes { get; set; } = Manifest.DefaultExcludes;
    public string LastHost { get; set; } = "";
    /// <summary>Espelhar o caminho completo de origem (C\Users\...) em vez de só o item escolhido.</summary>
    public bool FullPaths { get; set; }
    /// <summary>Fechar a janela deixa o programa recebendo, com ícone perto do relógio.</summary>
    public bool KeepInTray { get; set; }
    /// <summary>Código que não muda entre usos; vazio = código novo a cada início.</summary>
    public string FixedCode { get; set; } = "";
    /// <summary>"auto" segue o idioma do sistema; ou "pt", "en", "es".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"system", "light" ou "dark".</summary>
    public string Theme { get; set; } = "system";

    // NBT_CONFIG_DIR aponta para outra pasta de configurações (testes sem tocar nas do usuário).
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("NBT_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetBackupTool"),
        "settings.json");

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

/// <summary>Estado passado para a instância reaberta como administrador (Windows).</summary>
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
