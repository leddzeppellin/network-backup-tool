using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool.Agent;

/// <summary>Configuração do serviço (versão instalada, papel Servidor). Fica em ProgramData.</summary>
public sealed class AgentConfig
{
    public string Role { get; set; } = "Servidor";
    public string DestinationRoot { get; set; } = "";
    /// <summary>No serviço o código é sempre fixo: as estações e os técnicos o conhecem.</summary>
    public string Code { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // NBT_AGENT_DIR permite testar sem instalar (outra pasta de configuração).
    public static string Dir => Environment.GetEnvironmentVariable("NBT_AGENT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetBackupTool");
    public static string FilePath => Path.Combine(Dir, "servico.json");

    public static AgentConfig? Load()
    {
        try { return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath)); }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Esta máquina tem o Network Backup Tool instalado como servidor?</summary>
    public static bool ServerInstalled => OperatingSystem.IsWindows() && Load() is { Role: "Servidor" };
}

public sealed record AgentEvent(long Seq, string Kind, string? Text = null, SessionSnapshot? Session = null, SessionEnd? End = null);

public sealed record AgentStatus(
    bool Running, string Code, string Destination, string Machine,
    List<SessionSnapshot> Active, List<AgentEvent> Events, long Seq);

public sealed record AgentRequest(string Cmd, long Since = 0, string? Arg = null);

public sealed record AgentResponse(bool Ok, string? Error = null, AgentStatus? Status = null);

/// <summary>Conversa com o serviço por um named pipe local (uma pergunta e uma resposta por conexão).</summary>
public static class AgentClient
{
    public const string PipeName = "NetBackupTool-Servico";
    public const string CmdArg = "--servico-cmd";

    public static readonly string[] AdminCommands = ["start", "stop", "newcode", "dest", "restart"];

    public static async Task<AgentResponse> SendAsync(AgentRequest req, int timeoutMs = 1500)
    {
        try
        {
            // Identification: o serviço precisa ver quem pergunta para checar se é administrador.
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeoutMs);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(req));
            var line = await reader.ReadLineAsync();
            return line == null
                ? new AgentResponse(false, "sem resposta")
                : JsonSerializer.Deserialize<AgentResponse>(line) ?? new AgentResponse(false, "resposta inválida");
        }
        catch (Exception ex)
        {
            return new AgentResponse(false, ex.Message);
        }
    }

    public static bool IsElevated => OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess;

    /// <summary>
    /// Comando que muda o serviço. Sem administrador, reabre este programa elevado (pedido do Windows)
    /// só para mandar o comando. Devolve null se deu certo, ou a mensagem de erro.
    /// </summary>
    public static async Task<string?> CommandAsync(string cmd, string? arg = null)
    {
        if (IsElevated || !OperatingSystem.IsWindows())
        {
            var r = await SendAsync(new AgentRequest(cmd, 0, arg), 5000);
            return r.Ok ? null : r.Error ?? "?";
        }
        try
        {
            var psi = new ProcessStartInfo(Platform.Exe) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            psi.ArgumentList.Add(CmdArg);
            psi.ArgumentList.Add(cmd);
            if (arg != null) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : L.T("svc.cmdFailed");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return L.T("svc.needAdmin");
        }
    }
}
