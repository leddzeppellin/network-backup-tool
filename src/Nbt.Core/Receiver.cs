using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nbt.Core;

public sealed class ReceiveSession
{
    internal sealed class FileRecvState
    {
        public required string Path;
        public required long Size;
        public required long LastWriteUtcTicks;
        public required string RelativePath;
        public long Remaining;
        public SafeFileHandle? Handle;
        public bool Finished;
        public bool Failed;
    }

    internal byte[] Token { get; } = RandomNumberGenerator.GetBytes(16);
    internal FileRecvState?[] Files = [];
    internal readonly ConcurrentQueue<string> Errors = new();
    internal int OkCount;
    internal int FailCount;

    public required string SenderName { get; init; }
    public required string RemoteAddress { get; init; }
    public required string BaseDir { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public TransferStats Stats { get; } = new();

    internal void AddError(string msg) => Errors.Enqueue(msg);
}

public sealed class Receiver : IDisposable
{
    private TcpListener? _listener;
    private UdpClient? _discovery;
    private CancellationTokenSource? _cts;
    private int _failedAuth;
    private readonly ConcurrentDictionary<ReceiveSession, byte> _sessions = new();

    public required string DestinationRoot { get; set; }
    public string Code { get; private set; } = Protocol.NewCode();
    public int Port { get; init; } = Protocol.DefaultPort;
    public string Name { get; init; } = Environment.MachineName;

    public event Action<string>? Log;
    public event Action? CodeChanged;
    public event Action<ReceiveSession>? SessionStarted;
    public event Action<ReceiveSession, TransferResult>? SessionEnded;

    public IReadOnlyCollection<ReceiveSession> ActiveSessions => _sessions.Keys.ToList();

    public void SetCode(string code)
    {
        Code = code;
        _failedAuth = 0;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.IPv6Any, Port);
        _listener.Server.DualMode = true;
        _listener.Start(64);
        _ = AcceptLoop(_cts.Token);

        try
        {
            _discovery = new UdpClient();
            _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            _ = DiscoveryLoop(_cts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Descoberta automática indisponível ({ex.Message}). Use o IP manualmente.");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _discovery?.Dispose();
        _listener = null;
        _discovery = null;
    }

    public void Dispose() => Stop();

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        var reply = Encoding.UTF8.GetBytes($"{Discovery.Reply}{Name}|{Port}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _discovery!.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(res.Buffer) == Discovery.Request)
                    await _discovery.SendAsync(reply, res.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* ex.: ICMP de porta inalcançável; ignora */ }
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleConnection(client), ct);
        }
    }

    private void HandleConnection(TcpClient client)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        var remoteText = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4().ToString() : remote?.ToString() ?? "?";
        try
        {
            using (client)
            {
                Protocol.Tune(client.Client);
                var stream = new BufferedStream(client.GetStream(), 256 * 1024);
                using var w = new BinaryWriter(stream);
                using var r = new BinaryReader(stream);
                var kind = r.ReadByte();
                if (kind == Protocol.ConnControl) HandleControl(r, w, remoteText);
                else if (kind == Protocol.ConnData) HandleData(r, w);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            // Conexão caiu; a sessão (se houver) é encerrada pelo canal de controle.
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Erro na conexão de {remoteText}: {ex.Message}");
        }
    }

    private void HandleControl(BinaryReader r, BinaryWriter w, string remote)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        w.Write(nonce);
        w.Flush();
        var mac = r.ReadBytesExact(32);
        var senderName = r.ReadString();

        if (!CryptographicOperations.FixedTimeEquals(mac, Protocol.ComputeAuth(Code, nonce)))
        {
            w.Write(Protocol.StatusBadCode);
            w.Flush();
            Log?.Invoke($"Tentativa com código errado de {senderName} ({remote}).");
            if (Interlocked.Increment(ref _failedAuth) >= Protocol.MaxFailedAuth)
            {
                SetCode(Protocol.NewCode());
                Log?.Invoke("Muitas tentativas erradas: o código foi trocado.");
                CodeChanged?.Invoke();
            }
            return;
        }

        var session = new ReceiveSession
        {
            SenderName = senderName,
            RemoteAddress = remote,
            BaseDir = Path.Combine(DestinationRoot, Manifest.SanitizeName(senderName)),
        };
        w.Write(Protocol.StatusOk);
        w.Write(session.Token);
        w.Write(Name);
        w.Flush();

        var entries = Manifest.Read(r);
        byte[] bitset;
        try
        {
            bitset = Plan(session, entries);
        }
        catch (Exception ex)
        {
            w.Write(Protocol.StatusRejected);
            w.Write(ex.Message);
            w.Flush();
            Log?.Invoke($"Recusado {senderName}: {ex.Message}");
            return;
        }

        _sessions[session] = 0;
        Log?.Invoke($"Recebendo de {senderName} ({remote}) em {session.BaseDir}");
        SessionStarted?.Invoke(session);

        bool done = false;
        try
        {
            w.Write(Protocol.StatusOk);
            w.Write(bitset.Length);
            w.Write(bitset);
            w.Flush();

            // Fica aqui enquanto os dados correm pelas outras conexões.
            done = r.ReadByte() == Protocol.CmdDone;
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            session.AddError("Conexão com o remetente caiu antes do fim.");
        }

        var result = Finish(session);
        _sessions.TryRemove(session, out _);
        if (done)
        {
            w.Write(result.FilesOk);
            w.Write(result.FilesFailed);
            w.Write(result.Errors.Count);
            foreach (var e in result.Errors) w.Write(e);
            w.Flush();
        }
        Log?.Invoke($"Fim da sessão de {senderName}: {result.FilesOk:N0} ok, {result.FilesFailed:N0} com falha.");
        SessionEnded?.Invoke(session, result);
    }

    private static byte[] Plan(ReceiveSession session, List<FileEntry> entries)
    {
        var bitset = new byte[(entries.Count + 7) / 8];
        var files = new ReceiveSession.FileRecvState?[entries.Count];
        long needBytes = 0, reclaim = 0;
        int needFiles = 0;
        Directory.CreateDirectory(session.BaseDir);

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var path = Manifest.ResolveSafe(session.BaseDir, e.RelativePath);
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                // Mesmo tamanho e mesma data (tolerância de 2 s por causa de FAT/exFAT) → já copiado.
                if (fi.Length == e.Size && Math.Abs((fi.LastWriteTimeUtc.Ticks - e.LastWriteUtcTicks) / TimeSpan.TicksPerSecond) <= 2)
                {
                    session.Stats.SkippedFiles++;
                    session.Stats.SkippedBytes += e.Size;
                    continue;
                }
                reclaim += fi.Length;
            }
            bitset[i >> 3] |= (byte)(1 << (i & 7));
            files[i] = new ReceiveSession.FileRecvState
            {
                Path = path, Size = e.Size, Remaining = e.Size, LastWriteUtcTicks = e.LastWriteUtcTicks, RelativePath = e.RelativePath,
            };
            needBytes += e.Size;
            needFiles++;
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(session.BaseDir))!);
        if (drive.IsReady && needBytes - reclaim > drive.AvailableFreeSpace)
            throw new IOException($"Espaço insuficiente no destino: precisa de {Format.Bytes(needBytes - reclaim)}, livre {Format.Bytes(drive.AvailableFreeSpace)}.");

        session.Files = files;
        session.Stats.TotalBytes = needBytes;
        session.Stats.TotalFiles = needFiles;
        return bitset;
    }

    private void HandleData(BinaryReader r, BinaryWriter w)
    {
        var token = r.ReadBytesExact(16);
        var session = _sessions.Keys.FirstOrDefault(s => CryptographicOperations.FixedTimeEquals(s.Token, token));
        if (session == null)
        {
            w.Write(Protocol.StatusRejected);
            w.Flush();
            return;
        }
        w.Write(Protocol.StatusOk);
        w.Flush();

        var buffer = new byte[Protocol.ChunkSize];
        var files = session.Files;
        while (true)
        {
            int idx = r.ReadInt32();
            if (idx == Protocol.EndOfStream) break;
            long offset = r.ReadInt64();
            int len = r.ReadInt32();
            if ((uint)idx >= (uint)files.Length || files[idx] is not { } st)
                throw new InvalidDataException("Índice de arquivo inválido.");

            if (len == Protocol.AbortLength)
            {
                var reason = r.ReadString();
                lock (st) FailFile(session, st, $"{st.RelativePath}: não foi possível ler na origem ({reason})");
                continue;
            }
            if (len < 0 || len > Protocol.ChunkSize || offset < 0 || offset + len > st.Size)
                throw new InvalidDataException("Bloco inválido.");

            var data = buffer.AsSpan(0, len);
            r.BaseStream.ReadExactly(data);
            ulong hash = r.ReadUInt64();
            bool intact = XxHash3.HashToUInt64(data) == hash;

            lock (st)
            {
                if (st.Finished) continue;
                if (!intact)
                {
                    FailFile(session, st, $"{st.RelativePath}: falha de integridade (dados corrompidos na rede)");
                    continue;
                }
                try
                {
                    if (st.Handle == null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(st.Path)!);
                        session.Stats.CurrentFile = st.RelativePath;
                        st.Handle = File.OpenHandle(st.Path, FileMode.Create, FileAccess.Write, FileShare.Read,
                            FileOptions.None, preallocationSize: st.Size);
                    }
                    if (len > 0) RandomAccess.Write(st.Handle, data, offset);
                    st.Remaining -= len;
                    session.Stats.AddBytes(len);
                    if (st.Remaining == 0)
                    {
                        st.Handle.Dispose();
                        st.Handle = null;
                        st.Finished = true;
                        try { File.SetLastWriteTimeUtc(st.Path, new DateTime(st.LastWriteUtcTicks, DateTimeKind.Utc)); } catch { }
                        Interlocked.Increment(ref session.OkCount);
                        session.Stats.FileDone();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FailFile(session, st, $"{st.RelativePath}: erro ao gravar ({ex.Message})");
                }
            }
        }
        w.Write(Protocol.StatusOk);
        w.Flush();
    }

    /// <summary>Chamar com o lock de <paramref name="st"/>.</summary>
    private static void FailFile(ReceiveSession session, ReceiveSession.FileRecvState st, string message)
    {
        if (st.Finished) return;
        st.Finished = true;
        st.Failed = true;
        st.Handle?.Dispose();
        st.Handle = null;
        try { File.Delete(st.Path); } catch { }
        Interlocked.Increment(ref session.FailCount);
        session.Stats.FileFailed();
        session.AddError(message);
    }

    private TransferResult Finish(ReceiveSession session)
    {
        foreach (var st in session.Files)
        {
            if (st == null) continue;
            lock (st)
            {
                if (!st.Finished) FailFile(session, st, $"{st.RelativePath}: incompleto");
            }
        }
        var errors = session.Errors.ToList();
        var result = new TransferResult(session.OkCount, session.FailCount, errors);
        WriteReport(session, result);
        return result;
    }

    private void WriteReport(ReceiveSession session, TransferResult result)
    {
        try
        {
            var path = Path.Combine(session.BaseDir, $"_NBT_relatorio_{session.Started:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Network Backup Tool - relatório de cópia");
            sb.AppendLine($"Origem:   {session.SenderName} ({session.RemoteAddress})");
            sb.AppendLine($"Início:   {session.Started:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Fim:      {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Copiados: {result.FilesOk:N0} arquivos ({Format.Bytes(session.Stats.BytesDone)})");
            sb.AppendLine($"Pulados (já existiam iguais): {session.Stats.SkippedFiles:N0}");
            sb.AppendLine($"Falhas:   {result.FilesFailed:N0}");
            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Erros:");
                foreach (var e in result.Errors) sb.AppendLine("  " + e);
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Não foi possível gravar o relatório: {ex.Message}");
        }
    }
}
