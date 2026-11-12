using System.Net.Sockets;
using Avalonia.Threading;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Quem recebe de verdade: o próprio app (LocalEngine) ou o serviço do Windows da versão instalada
/// (ServiceEngine). A tela Receber é a mesma para os dois. Os eventos chegam na thread da interface.
/// </summary>
public interface IReceiveEngine
{
    bool IsService { get; }
    bool IsRunning { get; }
    string Code { get; }
    IReadOnlyList<SessionSnapshot> Active { get; }

    event Action<string>? Log;
    event Action? StateChanged;
    event Action<SessionSnapshot>? SessionStarted;
    event Action<SessionSnapshot, SessionEnd>? SessionEnded;

    /// <summary>Devolve null se começou, ou a mensagem de erro.</summary>
    Task<string?> StartAsync(string destination, string? fixedCode);
    Task<string?> StopAsync();
    Task<string?> NewCodeAsync();
    Task<string?> SetDestinationAsync(string path);
    void SetFixed(bool on);
}

public sealed class LocalEngine : IReceiveEngine
{
    private const long LogFileMinSize = 10L * 1024 * 1024;
    private Receiver? _receiver;

    public bool IsService => false;
    public bool IsRunning => _receiver != null;
    public string Code => _receiver?.Code ?? "";
    public IReadOnlyList<SessionSnapshot> Active =>
        _receiver?.ActiveSessions.Select(s => s.Snapshot()).ToList() ?? [];

    public event Action<string>? Log;
    public event Action? StateChanged;
    public event Action<SessionSnapshot>? SessionStarted;
    public event Action<SessionSnapshot, SessionEnd>? SessionEnded;

    public Task<string?> StartAsync(string destination, string? fixedCode)
    {
        var receiver = new Receiver { DestinationRoot = destination, FixedCode = fixedCode != null, Name = Platform.DeviceName };
        if (fixedCode != null) receiver.SetCode(fixedCode);
        receiver.Log += m => Dispatcher.UIThread.Post(() => Log?.Invoke(m));
        receiver.CodeChanged += () => Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
        receiver.SessionStarted += s => Dispatcher.UIThread.Post(() => SessionStarted?.Invoke(s.Snapshot()));
        receiver.FileReceived += (_, path, size) =>
        {
            // Arquivos grandes aparecem um a um na atividade; os pequenos só no total, para não inundar.
            if (size >= LogFileMinSize) Dispatcher.UIThread.Post(() => Log?.Invoke(L.T("recv.fileDone", path, Format.Bytes(size))));
        };
        receiver.SessionEnded += (s, r) => Dispatcher.UIThread.Post(() => SessionEnded?.Invoke(s.Snapshot(), s.EndOf(r)));
        try
        {
            receiver.Start();
        }
        catch (SocketException ex)
        {
            return Task.FromResult<string?>(L.T("recv.portError", Protocol.DefaultPort, ex.Message));
        }
        _receiver = receiver;
        StateChanged?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> StopAsync()
    {
        _receiver?.Stop();
        _receiver = null;
        StateChanged?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> NewCodeAsync()
    {
        _receiver?.SetCode(Protocol.NewCode());
        StateChanged?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public Task<string?> SetDestinationAsync(string path)
    {
        if (_receiver != null) _receiver.DestinationRoot = path;
        return Task.FromResult<string?>(null);
    }

    public void SetFixed(bool on)
    {
        if (_receiver != null) _receiver.FixedCode = on;
    }
}

/// <summary>Painel do serviço: pergunta o estado a cada meio segundo e manda os comandos.</summary>
public sealed class ServiceEngine : IReceiveEngine
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private long _seq;
    private bool _first = true;
    private bool _polling;
    private AgentStatus? _status;

    public bool IsService => true;
    public bool IsRunning => _status?.Running == true;
    public bool Reachable => _status != null;
    public string Code => _status?.Code ?? "";
    public string Destination => _status?.Destination ?? "";
    public IReadOnlyList<SessionSnapshot> Active => _status?.Active ?? [];

    public event Action<string>? Log;
    public event Action? StateChanged;
    public event Action<SessionSnapshot>? SessionStarted;
    public event Action<SessionSnapshot, SessionEnd>? SessionEnded;

    public ServiceEngine()
    {
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var r = await AgentClient.SendAsync(new AgentRequest("status", _seq));
            var before = (_status?.Running, _status?.Code, _status?.Destination, _status != null);
            _status = r.Ok ? r.Status : null;
            if (_status != null)
            {
                foreach (var e in _status.Events)
                {
                    // Na primeira consulta mostra o histórico recente da atividade, sem repetir avisos antigos.
                    switch (e.Kind)
                    {
                        case "log" when e.Text != null: Log?.Invoke(e.Text); break;
                        case "start" when e.Session != null && !_first: SessionStarted?.Invoke(e.Session); break;
                        case "end" when e.Session != null && e.End != null: SessionEnded?.Invoke(e.Session, e.End); break;
                    }
                }
                _seq = _status.Seq;
                _first = false;
            }
            var after = (_status?.Running, _status?.Code, _status?.Destination, _status != null);
            if (before != after) StateChanged?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task<string?> Run(string cmd, string? arg = null)
    {
        var error = await AgentClient.CommandAsync(cmd, arg);
        await PollAsync();
        return error;
    }

    public Task<string?> StartAsync(string destination, string? fixedCode) => Run("start");
    public Task<string?> StopAsync() => Run("stop");
    public Task<string?> NewCodeAsync() => Run("newcode");
    public Task<string?> SetDestinationAsync(string path) => Run("dest", path);
    public Task<string?> RestartServiceAsync() => Run("restart");
    public void SetFixed(bool on) { }
}
