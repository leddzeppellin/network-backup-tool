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
    private DateTime _lockedUntil;
    private readonly ConcurrentDictionary<ReceiveSession, byte> _sessions = new();

    public required string DestinationRoot { get; set; }
    public string Code { get; private set; } = Protocol.NewCode();
    public int Port { get; init; } = Protocol.DefaultPort;
    public string Name { get; init; } = Environment.MachineName;

    public event Action<string>? Log;
    public event Action? CodeChanged;
    public event Action<ReceiveSession>? SessionStarted;
    public event Action<ReceiveSession, TransferResult>? SessionEnded;
    /// <summary>Arquivo gravado por inteiro (sessão, caminho relativo, tamanho).</summary>
    public event Action<ReceiveSession, string, long>? FileReceived;

    public IReadOnlyCollection<ReceiveSession> ActiveSessions => _sessions.Keys.ToList();

    /// <summary>
    /// Código fixo (modo persistente): em vez de trocar o código após tentativas erradas,
    /// bloqueia novas tentativas por alguns minutos.
    /// </summary>
    public bool FixedCode { get; set; }
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

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
            Discovery.IgnoreConnReset(_discovery.Client);
            _ = DiscoveryLoop(_cts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke(L.T("recv.noDiscovery", ex.Message));
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
            Log?.Invoke(L.T("recv.connError", remoteText, ex.Message));
        }
    }

    private void HandleControl(BinaryReader r, BinaryWriter w, string remote)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        w.Write(nonce);
        w.Flush();
        var mac = r.ReadBytesExact(32);
        var senderName = r.ReadString();

        bool locked = DateTime.UtcNow < _lockedUntil;
        if (locked || !CryptographicOperations.FixedTimeEquals(mac, Protocol.ComputeAuth(Code, nonce)))
        {
            w.Write(Protocol.StatusBadCode);
            w.Flush();
            if (locked)
            {
                Log?.Invoke(L.T("recv.locked", senderName, remote));
                return;
            }
            Log?.Invoke(L.T("recv.badCode", senderName, remote));
            if (Interlocked.Increment(ref _failedAuth) >= Protocol.MaxFailedAuth)
            {
                _failedAuth = 0;
                if (FixedCode)
                {
                    _lockedUntil = DateTime.UtcNow + Lockout;
                    Log?.Invoke(L.T("recv.lockout", Lockout.TotalMinutes));
                }
                else
                {
                    SetCode(Protocol.NewCode());
                    Log?.Invoke(L.T("recv.codeChanged"));
                    CodeChanged?.Invoke();
                }
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
            Log?.Invoke(L.T("recv.rejected", senderName, ex.Message));
            return;
        }

        _sessions[session] = 0;
        Log?.Invoke(L.T("recv.receiving", senderName, remote, session.BaseDir));
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
            session.AddError(L.T("recv.dropped"));
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
        Log?.Invoke(L.T("recv.sessionEnd", senderName, result.FilesOk, result.FilesFailed));
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

        if (FreeSpace(session.BaseDir) is { } free && needBytes - reclaim > free)
            throw new IOException(L.T("recv.noSpace", Format.Bytes(needBytes - reclaim), Format.Bytes(free)));

        session.Files = files;
        session.Stats.TotalBytes = needBytes;
        session.Stats.TotalFiles = needFiles;
        return bitset;
    }

    /// <summary>
    /// Espaço livre do disco onde a pasta fica. No Linux/Android/macOS a raiz "/" é outro sistema de
    /// arquivos (no Android, 166 MB livres contra 93 GB em /storage/emulated): vale o ponto de
    /// montagem mais específico que contém o caminho. null se não der para saber.
    /// </summary>
    private static long? FreeSpace(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo? best = null;
        int bestLength = -1;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    var root = d.RootDirectory.FullName;
                    var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                    bool contains = full.StartsWith(prefix, comparison) || string.Equals(full, root, comparison);
                    if (contains && root.Length > bestLength && d.IsReady)
                    {
                        best = d;
                        bestLength = root.Length;
                    }
                }
                catch { /* ponto de montagem sem acesso */ }
            }
            return best?.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
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
                throw new InvalidDataException(L.T("recv.badIndex"));

            if (len == Protocol.AbortLength)
            {
                var reason = r.ReadString();
                lock (st) FailFile(session, st, L.T("recv.sourceRead", st.RelativePath, reason));
                continue;
            }
            if (len < 0 || len > Protocol.ChunkSize || offset < 0 || offset + len > st.Size)
                throw new InvalidDataException(L.T("recv.badBlock"));

            var data = buffer.AsSpan(0, len);
            r.BaseStream.ReadExactly(data);
            ulong hash = r.ReadUInt64();
            bool intact = XxHash3.HashToUInt64(data) == hash;

            lock (st)
            {
                if (st.Finished) continue;
                if (!intact)
                {
                    FailFile(session, st, L.T("recv.integrity", st.RelativePath));
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
                        FileReceived?.Invoke(session, st.RelativePath, st.Size);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FailFile(session, st, L.T("recv.writeError", st.RelativePath, ex.Message));
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
                if (!st.Finished) FailFile(session, st, L.T("recv.incomplete", st.RelativePath));
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
            sb.AppendLine(L.T("report.title"));
            sb.AppendLine(L.T("report.origin", session.SenderName, session.RemoteAddress));
            sb.AppendLine(L.T("report.start", session.Started.ToString("G")));
            sb.AppendLine(L.T("report.end", DateTime.Now.ToString("G")));
            sb.AppendLine(L.T("report.copied", result.FilesOk, Format.Bytes(session.Stats.BytesDone)));
            sb.AppendLine(L.T("report.skipped", session.Stats.SkippedFiles));
            sb.AppendLine(L.T("report.failed", result.FilesFailed));
            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("report.errors"));
                foreach (var e in result.Errors) sb.AppendLine("  " + e);
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log?.Invoke(L.T("recv.reportFail", ex.Message));
        }
    }
}
