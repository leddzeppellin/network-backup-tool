using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Transforma o painel em configuração do Windows: contas e grupos locais, permissões NTFS nas
/// pastas e um compartilhamento SMB com enumeração por acesso (cada um só vê o que pode abrir).
/// Roda dentro do serviço (LocalSystem). Só cria, altera e apaga o que tem a marca do programa.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShareApplier
{
    public const string Marker = "Network Backup Tool";

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier EveryoneSid = new(WellKnownSidType.WorldSid, null);

    /// <summary>Aplica tudo. Devolve null se deu certo, ou as mensagens do que falhou.</summary>
    public static string? Apply(ShareApplyRequest req)
    {
        var cfg = req.Config;
        if (cfg.Validate() is { } bad) return $"Nome inválido: {bad}";
        if (cfg.Root.Length == 0) return "Escolha a pasta da empresa.";
        var errors = new List<string>();
        var previous = ShareConfig.Load();

        Try(errors, "SMB", EnsureSmb);
        Directory.CreateDirectory(cfg.Root);

        using var ctx = new PrincipalContext(ContextType.Machine);

        // Grupos: NBT-Todos (todas as pessoas do painel) e um por grupo do painel.
        var wanted = cfg.Groups.Select(g => ShareConfig.WindowsGroup(g.Name)).Append(ShareConfig.AllGroup).ToList();
        foreach (var name in wanted) Try(errors, name, () => EnsureGroup(ctx, name));

        // Pessoas.
        foreach (var u in cfg.Users)
        {
            Try(errors, u.Login, () =>
            {
                req.Passwords.TryGetValue(u.Login, out var password);
                using var user = EnsureUser(ctx, u, password);
                var mine = new HashSet<string>(u.Groups.Select(ShareConfig.WindowsGroup).Append(ShareConfig.AllGroup), StringComparer.OrdinalIgnoreCase);
                foreach (var gname in wanted)
                {
                    using var g = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, gname);
                    if (g == null) continue;
                    bool member = g.Members.Contains(user);
                    if (mine.Contains(gname) && !member) { g.Members.Add(user); g.Save(); }
                    if (!mine.Contains(gname) && member) { g.Members.Remove(user); g.Save(); }
                }
            });
        }

        // Quem saiu do painel: só contas e grupos criados pelo programa.
        var logins = new HashSet<string>(cfg.Users.Select(u => u.Login), StringComparer.OrdinalIgnoreCase);
        foreach (var old in previous.Users.Where(u => !logins.Contains(u.Login)))
            Try(errors, old.Login, () =>
            {
                using var p = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, old.Login);
                if (p != null && p.Description == Marker) p.Delete();
            });
        var groupSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        foreach (var old in previous.Groups.Select(g => ShareConfig.WindowsGroup(g.Name)).Where(n => !groupSet.Contains(n)))
            Try(errors, old, () =>
            {
                using var g = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, old);
                if (g != null && g.Description == Marker) g.Delete();
            });

        // Permissões: raiz (todos listam), depois cada pasta.
        Try(errors, cfg.Root, () => SetRootAcl(cfg.Root));
        foreach (var f in cfg.Folders)
            Try(errors, f.Name, () =>
            {
                var path = Path.Combine(cfg.Root, f.Name);
                Directory.CreateDirectory(path);
                SetFolderAcl(path, f, cfg);
            });

        // Atalhos de cada pessoa, lidos pelas estações.
        Try(errors, ShareConfig.InfoDir, () => WriteStationInfo(cfg));

        // Compartilhamento.
        Try(errors, cfg.ShareName, () => EnsureShare(cfg, previous));

        cfg.Save();
        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    private static void Try(List<string> errors, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- contas

    private static void EnsureGroup(PrincipalContext ctx, string name)
    {
        using var g = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, name);
        if (g != null) return;
        using var created = new GroupPrincipal(ctx, name) { Description = Marker };
        created.Save();
    }

    private static UserPrincipal EnsureUser(PrincipalContext ctx, ShareUser u, string? password)
    {
        var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, u.Login);
        if (user == null)
        {
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("defina uma senha para a pessoa nova");
            user = new UserPrincipal(ctx, u.Login, password, !u.Disabled)
            {
                DisplayName = u.FullName,
                Description = Marker,
                PasswordNeverExpires = true,
            };
            user.Save();
            return user;
        }
        // Conta que já existia no PC (não criada aqui): só entra nos grupos; nome e senha ficam como estão.
        if (user.Description == Marker)
        {
            user.DisplayName = u.FullName;
            user.Enabled = !u.Disabled;
            if (!string.IsNullOrEmpty(password)) user.SetPassword(password);
            user.Save();
        }
        return user;
    }

    private static SecurityIdentifier Sid(string account) =>
        (SecurityIdentifier)new NTAccount(Environment.MachineName, account).Translate(typeof(SecurityIdentifier));

    // ---------------------------------------------------------------- permissões

    private static DirectorySecurity BaseSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false); // não herda da pasta de cima
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    private static void SetRootAcl(string root)
    {
        // Todos do painel abrem a raiz e veem as pastas (a enumeração por acesso esconde as outras).
        var sec = BaseSecurity();
        sec.AddAccessRule(new FileSystemAccessRule(Sid(ShareConfig.AllGroup), FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(sec);
    }

    private static void SetFolderAcl(string path, ShareFolder f, ShareConfig cfg)
    {
        var sec = BaseSecurity();
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(SecurityIdentifier sid, FileSystemRights r) =>
            sec.AddAccessRule(new FileSystemAccessRule(sid, r, all, PropagationFlags.None, AccessControlType.Allow));
        void Deny(SecurityIdentifier sid, FileSystemRights r) =>
            sec.AddAccessRule(new FileSystemAccessRule(sid, r, all, PropagationFlags.None, AccessControlType.Deny));

        foreach (var rule in f.Rules)
        {
            switch (rule.Kind)
            {
                case RuleKind.Everyone:
                case RuleKind.Group:
                    var sid = Sid(rule.Kind == RuleKind.Everyone ? ShareConfig.AllGroup : ShareConfig.WindowsGroup(rule.Name));
                    if (rule.Access == Access.Read) Allow(sid, FileSystemRights.ReadAndExecute);
                    if (rule.Access == Access.Write) Allow(sid, FileSystemRights.Modify);
                    break;
                case RuleKind.User when cfg.Users.Any(u => u.Login.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)):
                    // Exceção de pessoa: vence o que vier do grupo (negação no Windows vence permissão).
                    var usid = Sid(rule.Name);
                    if (rule.Access == Access.None) Deny(usid, FileSystemRights.FullControl);
                    if (rule.Access == Access.Read) { Allow(usid, FileSystemRights.ReadAndExecute); Deny(usid, WriteRights); }
                    if (rule.Access == Access.Write) Allow(usid, FileSystemRights.Modify);
                    break;
            }
        }
        new DirectoryInfo(path).SetAccessControl(sec);
    }

    /// <summary>
    /// Raiz\.nbt\login.json para cada pessoa. A pasta fica oculta e só deixa passar (sem listar), e cada
    /// arquivo só a própria pessoa lê: ninguém vê os nomes nem os atalhos dos colegas.
    /// </summary>
    private static void WriteStationInfo(ShareConfig cfg)
    {
        var dir = new DirectoryInfo(Path.Combine(cfg.Root, ShareConfig.InfoDir));
        dir.Create();
        dir.Attributes |= FileAttributes.Hidden;
        var dsec = BaseSecurity();
        dsec.AddAccessRule(new FileSystemAccessRule(Sid(ShareConfig.AllGroup), FileSystemRights.Traverse,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(dsec);

        var logins = new HashSet<string>(cfg.Users.Select(u => u.Login), StringComparer.OrdinalIgnoreCase);
        foreach (var old in dir.GetFiles("*.json").Where(f => !logins.Contains(Path.GetFileNameWithoutExtension(f.Name))))
            old.Delete();

        foreach (var u in cfg.Users)
        {
            var info = new StationInfo
            {
                ShareName = cfg.ShareName,
                Pins = cfg.Folders.Where(f => f.PinFor.Contains(u.Login, StringComparer.OrdinalIgnoreCase)).Select(f => f.Name).ToList(),
            };
            var file = new FileInfo(Path.Combine(dir.FullName, u.Login + ".json"));
            File.WriteAllText(file.FullName, System.Text.Json.JsonSerializer.Serialize(info));
            var fsec = new FileSecurity();
            fsec.SetAccessRuleProtection(true, false);
            fsec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fsec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fsec.AddAccessRule(new FileSystemAccessRule(Sid(u.Login), FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(fsec);
        }
    }

    // ---------------------------------------------------------------- SMB

    /// <summary>Roda um script PowerShell; valores entram por variáveis de ambiente (sem montar comando com texto do usuário).</summary>
    private static string Ps(string script, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(stderr.Trim().Split('\n').FirstOrDefault()?.Trim() ?? $"código {p.ExitCode}");
        return stdout.Result;
    }

    private static void EnsureSmb() => Ps("""
        $ErrorActionPreference = 'Stop'
        Set-Service LanmanServer -StartupType Automatic
        Start-Service LanmanServer
        # Só a entrada SMB (porta 445) do grupo Compartilhamento de Arquivos e Impressoras, em todos os perfis.
        Get-NetFirewallRule -Name 'FPS-SMB-In-TCP*' | Enable-NetFirewallRule
        """);

    private static void EnsureShare(ShareConfig cfg, ShareConfig previous)
    {
        var everyone = EveryoneSid.Translate(typeof(NTAccount)).Value;
        Ps("""
            $ErrorActionPreference = 'Stop'
            if ($env:NBT_OLD -and $env:NBT_OLD -ne $env:NBT_SHARE) {
              $o = Get-SmbShare -Name $env:NBT_OLD -ErrorAction SilentlyContinue
              if ($o -and $o.Description -eq $env:NBT_MARK) { Remove-SmbShare -Name $env:NBT_OLD -Force }
            }
            $s = Get-SmbShare -Name $env:NBT_SHARE -ErrorAction SilentlyContinue
            if ($s -and $s.Path -ne $env:NBT_PATH) {
              if ($s.Description -ne $env:NBT_MARK) { throw "já existe outro compartilhamento com este nome" }
              Remove-SmbShare -Name $env:NBT_SHARE -Force; $s = $null
            }
            if (-not $s) {
              New-SmbShare -Name $env:NBT_SHARE -Path $env:NBT_PATH -FullAccess $env:NBT_EVERYONE `
                -FolderEnumerationMode AccessBased -Description $env:NBT_MARK | Out-Null
            } else {
              Set-SmbShare -Name $env:NBT_SHARE -FolderEnumerationMode AccessBased -Force
            }
            """, new()
        {
            ["NBT_SHARE"] = cfg.ShareName, ["NBT_OLD"] = previous.ShareName, ["NBT_PATH"] = cfg.Root,
            ["NBT_EVERYONE"] = everyone, ["NBT_MARK"] = Marker,
        });
    }

    /// <summary>Quem está conectado e com quais arquivos abertos (só os da pasta da empresa).</summary>
    public static ShareOverview Overview()
    {
        var cfg = ShareConfig.Load();
        if (cfg.Root.Length == 0) return new ShareOverview(cfg, 0, [], null);
        try
        {
            var json = Ps("""
                $ErrorActionPreference = 'Stop'
                $files = @(Get-SmbOpenFile | Where-Object { $_.Path -like ($env:NBT_PATH + '*') -and $_.Path -ne $env:NBT_PATH } |
                  ForEach-Object { [pscustomobject]@{ User = $_.ClientUserName; Path = $_.Path } })
                $sessions = @(Get-SmbSession).Count
                [pscustomobject]@{ Sessions = $sessions; Files = $files } | ConvertTo-Json -Depth 3 -Compress
                """, new() { ["NBT_PATH"] = cfg.Root.TrimEnd('\\') + "\\" });
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var files = new List<OpenFile>();
            if (root.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var e in f.EnumerateArray())
                    files.Add(new OpenFile(e.GetProperty("User").GetString() ?? "", e.GetProperty("Path").GetString() ?? ""));
            return new ShareOverview(cfg, root.GetProperty("Sessions").GetInt32(), files, null);
        }
        catch (Exception ex)
        {
            return new ShareOverview(cfg, 0, [], ex.Message);
        }
    }
}
