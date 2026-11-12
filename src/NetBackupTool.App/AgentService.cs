using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Versão instalada, papel Servidor: recebe cópias como serviço do Windows (sem ninguém logado)
/// e atende o painel do app pelo named pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentCore : IDisposable
{
    private const int KeepEvents = 300;
    private readonly object _gate = new();
    private readonly List<AgentEvent> _events = new();
    private long _seq;
    private Receiver? _receiver;
    private AgentConfig _config = new();
    private CancellationTokenSource? _cts;

    public void Start()
    {
        _config = AgentConfig.Load() ?? new AgentConfig();
        if (_config.Code.Length != 6)
        {
            _config.Code = Protocol.NewCode();
            TrySave();
        }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PipeLoop(_cts.Token));
        if (_config.Enabled) StartReceiver();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _receiver?.Stop();
        _receiver = null;
    }

    private void TrySave()
    {
        try { _config.Save(); } catch (Exception ex) { Add("log", ex.Message); }
    }

    private void Add(string kind, string? text = null, SessionSnapshot? s = null, SessionEnd? end = null)
    {
        lock (_gate)
        {
            _events.Add(new AgentEvent(++_seq, kind, text == null ? null : $"{DateTime.Now:dd/MM HH:mm:ss}  {text}", s, end));
            if (_events.Count > KeepEvents) _events.RemoveRange(0, _events.Count - KeepEvents);
        }
    }

    private string? StartReceiver()
    {
        if (_receiver != null) return null;
        if (_config.DestinationRoot.Length == 0) return L.T("recv.noFolder");
        try
        {
            Directory.CreateDirectory(_config.DestinationRoot);
            var r = new Receiver { DestinationRoot = _config.DestinationRoot, FixedCode = true, Name = Environment.MachineName };
            r.SetCode(_config.Code);
            r.Log += m => Add("log", m);
            r.SessionStarted += s => Add("start", null, s.Snapshot());
            r.FileReceived += (_, path, size) =>
            {
                if (size >= 10L * 1024 * 1024) Add("log", L.T("recv.fileDone", path, Format.Bytes(size)));
            };
            r.SessionEnded += (s, res) => Add("end", null, s.Snapshot(), s.EndOf(res));
            r.Start();
            _receiver = r;
            Add("log", L.T("recv.waiting", Protocol.DefaultPort));
            return null;
        }
        catch (Exception ex)
        {
            Add("log", ex.Message);
            return ex.Message;
        }
    }

    private AgentStatus Status(long since)
    {
        lock (_gate)
        {
            return new AgentStatus(
                _receiver != null, _config.Code, _config.DestinationRoot, Environment.MachineName,
                _receiver?.ActiveSessions.Select(s => s.Snapshot()).ToList() ?? [],
                _events.Where(e => e.Seq > since).ToList(), _seq);
        }
    }

    private AgentResponse Handle(AgentRequest req, bool isAdmin)
    {
        if (req.Cmd != "status" && !isAdmin) return new AgentResponse(false, L.T("svc.needAdmin"));
        string? error = null;
        switch (req.Cmd)
        {
            case "status":
                break;
            case "start":
                _config.Enabled = true;
                TrySave();
                error = StartReceiver();
                break;
            case "stop":
                _config.Enabled = false;
                TrySave();
                _receiver?.Stop();
                _receiver = null;
                Add("log", L.T("recv.stopped"));
                break;
            case "newcode":
                _config.Code = Protocol.NewCode();
                TrySave();
                _receiver?.SetCode(_config.Code);
                break;
            case "dest" when !string.IsNullOrWhiteSpace(req.Arg):
                try { Directory.CreateDirectory(req.Arg); }
                catch (Exception ex) { return new AgentResponse(false, L.T("recv.destError", ex.Message)); }
                _config.DestinationRoot = req.Arg;
                TrySave();
                if (_receiver != null) _receiver.DestinationRoot = req.Arg;
                else if (_config.Enabled) error = StartReceiver();
                break;
            default:
                return new AgentResponse(false, "?");
        }
        return new AgentResponse(error == null, error, Status(req.Since));
    }

    private async Task PipeLoop(CancellationToken ct)
    {
        // Qualquer usuário do PC lê o estado; o serviço confere quem é administrador antes de mudar algo.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(AgentClient.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(ct);
                var client = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(client), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Add("log", ex.Message);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var req = line == null ? null : JsonSerializer.Deserialize<AgentRequest>(line);
                if (req == null) return;
                bool isAdmin = false;
                if (req.Cmd != "status")
                {
                    // Vê o token de quem conectou: com o UAC, só o app elevado conta como administrador.
                    pipe.RunAsClient(() =>
                    {
                        using var id = WindowsIdentity.GetCurrent();
                        isAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    });
                }
                AgentResponse resp;
                lock (this) resp = Handle(req, isAdmin);
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
            }
            catch { /* cliente sumiu */ }
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class AgentWindowsService : ServiceBase
{
    private AgentCore? _core;

    public AgentWindowsService() => ServiceName = AgentSetup.ServiceName;

    protected override void OnStart(string[] args)
    {
        _core = new AgentCore();
        _core.Start();
    }

    protected override void OnStop()
    {
        _core?.Dispose();
        _core = null;
    }
}

/// <summary>Registro do serviço no Windows. Chamado pelo instalador (já elevado) e pelo painel.</summary>
[SupportedOSPlatform("windows")]
public static class AgentSetup
{
    public const string ServiceName = "NetBackupTool";
    public const string RunArg = "--servico";
    public const string ConsoleArg = "--servico-console";
    public const string InstallArg = "--instalar-servico";
    public const string RemoveArg = "--remover-servico";

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static bool Exists() => Sc("query", ServiceName) == 0;

    private static void WaitStopped()
    {
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Stopped) return;
            }
            catch { return; }
            Thread.Sleep(250);
        }
    }

    /// <summary>Cria (ou atualiza) e inicia o serviço. destination vazio mantém a pasta já configurada.</summary>
    public static int Install(string? destination)
    {
        var config = AgentConfig.Load() ?? new AgentConfig();
        config.Role = "Servidor";
        if (!string.IsNullOrWhiteSpace(destination)) config.DestinationRoot = destination;
        if (config.Code.Length != 6) config.Code = Protocol.NewCode();
        config.Save();
        if (config.DestinationRoot.Length > 0) Directory.CreateDirectory(config.DestinationRoot);

        var bin = $"\"{Platform.Exe}\" {RunArg}";
        if (Exists())
        {
            Sc("stop", ServiceName);
            WaitStopped();
            Sc("config", ServiceName, "binPath=", bin, "start=", "auto");
        }
        else
        {
            if (Sc("create", ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", "Network Backup Tool") != 0) return 2;
        }
        Sc("description", ServiceName, "Recebe cópias de arquivos pela rede local (Network Backup Tool).");
        // Se cair, volta sozinho depois de 5 s.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        return Sc("start", ServiceName) == 0 ? 0 : 3;
    }

    public static int Remove()
    {
        if (!Exists()) return 0;
        Sc("stop", ServiceName);
        WaitStopped();
        return Sc("delete", ServiceName) == 0 ? 0 : 4;
    }

    public static int Restart()
    {
        Sc("stop", ServiceName);
        WaitStopped();
        return Sc("start", ServiceName) == 0 ? 0 : 5;
    }

    /// <summary>Lado elevado de um comando do painel: manda pelo pipe já como administrador.</summary>
    public static int Command(string cmd, string? arg)
    {
        if (cmd == "restart") return Restart();
        var r = AgentClient.SendAsync(new AgentRequest(cmd, 0, arg), 5000).GetAwaiter().GetResult();
        return r.Ok ? 0 : 1;
    }

    /// <summary>Modo console: o mesmo serviço sem o gerenciador de serviços (para testes).</summary>
    public static int RunConsole()
    {
        using var core = new AgentCore();
        core.Start();
        // Sem console (o executável é de janela): roda até o processo ser encerrado.
        new ManualResetEvent(false).WaitOne();
        return 0;
    }
}
