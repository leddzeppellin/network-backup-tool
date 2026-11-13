using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>
/// Administração do servidor a partir de outro PC. A conexão é autenticada pelo próprio Windows
/// (Negotiate: Kerberos no domínio, NTLM fora dele) e criptografada. Só entra quem é administrador
/// deste computador; tudo, até o que só lê, exige isso (o registro de atividade é sensível).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteAdmin : IDisposable
{
    private const string AdminsSid = "S-1-5-32-544";
    private readonly AgentCore _core;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public RemoteAdmin(AgentCore core) => _core = core;

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, AgentClient.RemotePort);
            _listener.Server.DualMode = true;
            _listener.Start();
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _core.Log(L.T("rem.listenFailed", AgentClient.RemotePort, ex.Message));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => Serve(client), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            try
            {
                await using var auth = new NegotiateStream(client.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await auth.AuthenticateAsServerAsync(CredentialCache.DefaultNetworkCredentials,
                        ProtectionLevel.EncryptAndSign, TokenImpersonationLevel.Identification).WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception)
                {
                    // Senha errada ou usuário que não existe: espera um pouco antes de fechar (tentativas em série ficam lentas).
                    _core.Log(L.T("rem.loginFailed", from));
                    await Task.Delay(1500);
                    return;
                }

                using var reader = new StreamReader(auth);
                await using var writer = new StreamWriter(auth) { AutoFlush = true };
                var id = auth.RemoteIdentity as WindowsIdentity;
                var user = auth.RemoteIdentity.Name;
                if (id == null || !IsAdmin(id))
                {
                    _core.Log(L.T("rem.notAdmin", user, from));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentResponse(false, L.T("rem.notAdminReply"))));
                    return;
                }

                // Uma conexão atende vários pedidos seguidos (o painel atualiza a cada poucos segundos).
                bool first = true;
                while (true)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2));
                    if (line == null) break;
                    var req = JsonSerializer.Deserialize<AgentRequest>(line);
                    if (req == null) break;
                    if (first || !AgentClient.ReadCommands.Contains(req.Cmd)) _core.Log(L.T("rem.request", user, from, req.Cmd));
                    first = false;
                    var resp = req.Cmd == "hello"
                        ? new AgentResponse(true, Data: Environment.MachineName)
                        : _core.Process(req, isAdmin: true);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
                }
            }
            catch { /* cliente sumiu ou demorou demais */ }
        }
    }

    /// <summary>
    /// Membro do grupo Administradores deste PC. Pela rede, o Windows entrega às contas locais um
    /// token "filtrado" (UAC), em que Administradores aparece só como negação: também vale.
    /// </summary>
    private static bool IsAdmin(WindowsIdentity id)
    {
        if (id.IsAnonymous || id.IsGuest) return false;
        if (id.Groups?.Any(g => g.Value == AdminsSid) == true) return true;
        return id.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == AdminsSid);
    }
}
