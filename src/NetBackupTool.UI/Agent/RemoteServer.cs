using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool.Agent;

/// <summary>
/// Conexão do painel com o serviço de outro PC (administração remota). Fica aberta enquanto o painel
/// está ligado a esse servidor; se cair, reconecta no próximo pedido com as mesmas credenciais.
/// A senha fica só na memória.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NetworkCredential _credential;
    private TcpClient? _tcp;
    private NegotiateStream? _auth;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public string Host { get; }
    /// <summary>Nome do computador do servidor (vem dele ao conectar).</summary>
    public string MachineName { get; private set; } = "";
    /// <summary>Quem está conectado, como aparece para o servidor.</summary>
    public string UserName => _credential.UserName.Length > 0
        ? (_credential.Domain.Length > 0 ? $@"{_credential.Domain}\{_credential.UserName}" : _credential.UserName)
        : Environment.UserName;

    private RemoteServer(string host, NetworkCredential credential)
    {
        Host = host;
        _credential = credential;
    }

    /// <summary>
    /// Conecta e confere se a conta é administradora do servidor. Usuário vazio usa o login atual do
    /// Windows (no domínio basta isso). "SERVIDOR\conta" ou "conta" para uma conta do servidor.
    /// </summary>
    public static async Task<(RemoteServer? Server, string? Error)> ConnectAsync(string host, string user, string password)
    {
        NetworkCredential cred;
        if (user.Length == 0) cred = CredentialCache.DefaultNetworkCredentials;
        else
        {
            var i = user.IndexOf('\\');
            cred = i > 0 ? new NetworkCredential(user[(i + 1)..], password, user[..i]) : new NetworkCredential(user, password);
        }
        var s = new RemoteServer(host, cred);
        var r = await s.SendAsync(new AgentRequest("hello"), 20000);
        if (!r.Ok)
        {
            s.Dispose();
            return (null, r.Error);
        }
        s.MachineName = r.Data ?? host;
        return (s, null);
    }

    public async Task<AgentResponse> SendAsync(AgentRequest req, int timeoutMs)
    {
        // Pedidos em fila: a conexão leva um pedido e uma resposta de cada vez.
        await _gate.WaitAsync();
        try
        {
            // Uma conexão aberta pode ter caído sem aviso: tenta de novo uma vez com uma nova.
            for (int attempt = 0; ; attempt++)
            {
                bool reused = _writer != null;
                try
                {
                    if (_writer == null) await OpenAsync(timeoutMs);
                    // Aplicar pastas e pessoas pode levar minutos num servidor com muita gente.
                    using var cts = new CancellationTokenSource(AgentClient.ReadCommands.Contains(req.Cmd) || req.Cmd == "hello"
                        ? Math.Max(timeoutMs, 15000) : 300000);
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(req).AsMemory(), cts.Token);
                    var line = await _reader!.ReadLineAsync(cts.Token);
                    if (line == null) throw new IOException(L.T("rem.closed"));
                    var resp = JsonSerializer.Deserialize<AgentResponse>(line) ?? new AgentResponse(false, "?");
                    if (!resp.Ok && resp.Error == L.T("rem.notAdminReply")) Close();
                    return resp;
                }
                catch (AuthenticationException) { Close(); return new AgentResponse(false, L.T("rem.badLogin")); }
                catch (Exception ex)
                {
                    var msg = Describe(ex);
                    Close();
                    if (reused && attempt == 0) continue;
                    return new AgentResponse(false, msg);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task OpenAsync(int timeoutMs)
    {
        var tcp = new TcpClient();
        try
        {
            using (var cts = new CancellationTokenSource(Math.Max(timeoutMs, 5000)))
                await tcp.ConnectAsync(Host, AgentClient.RemotePort, cts.Token);
            var auth = new NegotiateStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            // "host/NOME" deixa o Kerberos funcionar no domínio; por IP, o Windows usa NTLM.
            await auth.AuthenticateAsClientAsync(_credential, $"host/{Host}", ProtectionLevel.EncryptAndSign, TokenImpersonationLevel.Identification)
                .WaitAsync(TimeSpan.FromSeconds(20));
            _tcp = tcp;
            _auth = auth;
            _reader = new StreamReader(auth);
            _writer = new StreamWriter(auth) { AutoFlush = true };
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        SocketException or OperationCanceledException or TimeoutException => L.T("rem.unreachable", Host, AgentClient.RemotePort),
        IOException when _auth == null => L.T("rem.badLogin"), // o servidor fechou durante o login: senha errada
        _ => ex.Message,
    };

    private void Close()
    {
        _reader?.Dispose();
        _writer = null;
        _reader = null;
        try { _auth?.Dispose(); } catch { }
        _auth = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
