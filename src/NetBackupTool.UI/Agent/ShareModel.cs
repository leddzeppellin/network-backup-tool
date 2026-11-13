using System.Text.Json;

namespace NetBackupTool.Agent;

/// <summary>Nível de acesso de uma pessoa ou grupo a uma pasta da empresa.</summary>
public static class Access
{
    public const string None = "none";
    public const string Read = "read";
    public const string Write = "write";
}

/// <summary>A quem uma regra se aplica: um grupo, uma pessoa (exceção) ou todos os outros.</summary>
public static class RuleKind
{
    public const string Group = "group";
    public const string User = "user";
    public const string Everyone = "all";
}

public sealed class ShareGroup
{
    public string Name { get; set; } = "";
}

public sealed class ShareUser
{
    /// <summary>Nome de login (conta local do Windows), ex.: joao.lima</summary>
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public bool Disabled { get; set; }
}

public sealed class ShareRule
{
    public string Kind { get; set; } = RuleKind.Group;
    public string Name { get; set; } = "";
    public string Access { get; set; } = Agent.Access.None;
}

public sealed class ShareFolder
{
    /// <summary>Nome da subpasta dentro da pasta da empresa.</summary>
    public string Name { get; set; } = "";
    public List<ShareRule> Rules { get; set; } = new();
    /// <summary>Grupos que recebem a pasta fixada no Acesso rápido (usado pelas estações).</summary>
    public List<string> PinFor { get; set; } = new();

    public string AccessOf(string kind, string name) =>
        Rules.FirstOrDefault(r => r.Kind == kind && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Access ?? Access.None;

    public void SetAccess(string kind, string name, string access)
    {
        Rules.RemoveAll(r => r.Kind == kind && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        // Grupo ou "todos" sem acesso = sem regra; exceção de pessoa "sem acesso" é uma regra (nega).
        if (access != Access.None || kind == RuleKind.User) Rules.Add(new ShareRule { Kind = kind, Name = name, Access = access });
    }

    public void ClearUser(string login) =>
        Rules.RemoveAll(r => r.Kind == RuleKind.User && r.Name.Equals(login, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Tudo o que o servidor compartilha: a pasta da empresa, as pastas, as pessoas e os grupos.</summary>
public sealed class ShareConfig
{
    public string Root { get; set; } = "";
    public string ShareName { get; set; } = "Empresa";
    public List<ShareGroup> Groups { get; set; } = new();
    public List<ShareUser> Users { get; set; } = new();
    public List<ShareFolder> Folders { get; set; } = new();
    /// <summary>Registrar quem criou, alterou ou apagou cada arquivo (auditoria do Windows na pasta da empresa).</summary>
    public bool Activity { get; set; } = true;

    public const string GroupPrefix = "NBT-";
    public const string AllGroup = "NBT-Todos";
    public static string WindowsGroup(string name) => GroupPrefix + name;

    /// <summary>Pasta oculta na raiz do compartilhamento com um arquivo por pessoa (os atalhos dela).</summary>
    public const string InfoDir = ".nbt";

    public static string FilePath => Path.Combine(AgentConfig.Dir, "pastas.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ShareConfig Load()
    {
        try { return JsonSerializer.Deserialize<ShareConfig>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AgentConfig.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public ShareConfig Clone() => JsonSerializer.Deserialize<ShareConfig>(JsonSerializer.Serialize(this))!;

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Nomes válidos para Windows (contas, grupos, pastas). Devolve a mensagem de erro ou null.</summary>
    public string? Validate()
    {
        static bool BadName(string s, bool login) =>
            string.IsNullOrWhiteSpace(s) || s.Length > (login ? 20 : 60) ||
            s.IndexOfAny(login ? "\"/\\[]:;|=,+*?<>@ ".ToCharArray() : "\"/\\[]:;|=,+*?<>".ToCharArray()) >= 0 ||
            s.Trim() != s || s.EndsWith('.');

        if (Root.Length > 0 && !Path.IsPathFullyQualified(Root)) return "Root";
        if (BadName(ShareName, false)) return ShareName;
        foreach (var g in Groups) if (BadName(g.Name, false) || g.Name.Length > 16) return g.Name;
        foreach (var u in Users) if (BadName(u.Login, true)) return u.Login;
        foreach (var f in Folders) if (BadName(f.Name, false) || f.Name.Equals(InfoDir, StringComparison.OrdinalIgnoreCase)) return f.Name;
        bool Dup(IEnumerable<string> names) => names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        if (Dup(Groups.Select(g => g.Name)) || Dup(Users.Select(u => u.Login)) || Dup(Folders.Select(f => f.Name))) return "dup";
        return null;
    }
}

/// <summary>Pedido de "Aplicar": a configuração nova e as senhas que mudaram (não ficam gravadas em lugar nenhum).</summary>
public sealed class ShareApplyRequest
{
    public ShareConfig Config { get; set; } = new();
    public Dictionary<string, string> Passwords { get; set; } = new();
}

/// <summary>
/// O que a estação lê no servidor, com a conta da própria pessoa: \\SERVIDOR\Empresa\.nbt\login.json.
/// Só aquela pessoa consegue ler o arquivo dela.
/// </summary>
public sealed class StationInfo
{
    public string ShareName { get; set; } = "";
    /// <summary>Pastas para fixar no Acesso rápido dessa pessoa.</summary>
    public List<string> Pins { get; set; } = new();
}

/// <summary>Uma linha do registro de atividade. Action: created, changed, deleted, renamed.</summary>
public sealed record ActivityEntry(DateTime Time, string User, string Action, string Path, string? OldPath = null);

public sealed record OpenFile(string User, string Path);

public sealed record ShareOverview(ShareConfig Config, int Sessions, List<OpenFile> OpenFiles, string? Problem);
