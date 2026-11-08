using System.IO.Hashing;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace Nbt.Core;

public sealed class TransferException(string message) : Exception(message);

public sealed class Sender
{
    private readonly record struct Chunk(int File, long Offset, int Length);

    private sealed class FileSendState
    {
        public SafeFileHandle? Handle;
        public int RemainingChunks;
        public bool Failed;
        public bool AbortSent;
    }

    public required string Host { get; init; }
    public int Port { get; init; } = Protocol.DefaultPort;
    public required string Code { get; init; }
    public string SenderName { get; init; } = Environment.MachineName;
    public int Connections { get; init; } = 4;
    public Action<string> Log { get; init; } = _ => { };
    /// <summary>Ler arquivos bloqueados por outros programas a partir de uma Shadow Copy (exige admin).</summary>
    public bool UseShadowCopy { get; init; } = true;

    public async Task<TransferResult> SendAsync(ScanResult scan, TransferStats stats, CancellationToken ct)
    {
        using var control = new TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6) { };
        control.Client.DualMode = true;
        Protocol.Tune(control.Client);
        await control.ConnectAsync(Host, Port, ct);
        await using var reg = ct.Register(() => control.Dispose());

        var cstream = new BufferedStream(control.GetStream(), 64 * 1024);
        using var w = new BinaryWriter(cstream);
        using var r = new BinaryReader(cstream);

        w.Write(Protocol.ConnControl);
        w.Flush();
        var nonce = r.ReadBytesExact(16);
        w.Write(Protocol.ComputeAuth(Code, nonce));
        w.Write(SenderName);
        w.Flush();

        var status = r.ReadByte();
        if (status == Protocol.StatusBadCode) throw new TransferException(L.T("send.badCode"));
        if (status != Protocol.StatusOk) throw new TransferException(L.T("send.refused"));
        var token = r.ReadBytesExact(16);
        var receiverName = r.ReadString();
        Log(L.T("send.connected", receiverName, scan.Entries.Count));

        Manifest.Write(w, scan.Entries);
        w.Flush();

        status = r.ReadByte();
        if (status != Protocol.StatusOk) throw new TransferException(L.T("send.refusedReason", r.ReadString()));
        var bitset = r.ReadBytesExact(r.ReadInt32());

        var entries = scan.Entries;
        var states = new FileSendState?[entries.Count];
        var chunks = new List<Chunk>();
        long needBytes = 0;
        int needFiles = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsDirectory) continue;
            bool need = (bitset[i >> 3] & (1 << (i & 7))) != 0;
            if (!need)
            {
                stats.SkippedFiles++;
                stats.SkippedBytes += e.Size;
                continue;
            }
            needFiles++;
            needBytes += e.Size;
            var st = new FileSendState();
            if (e.Size == 0)
            {
                chunks.Add(new Chunk(i, 0, 0));
                st.RemainingChunks = 1;
            }
            else
            {
                for (long off = 0; off < e.Size; off += Protocol.ChunkSize)
                {
                    chunks.Add(new Chunk(i, off, (int)Math.Min(Protocol.ChunkSize, e.Size - off)));
                    st.RemainingChunks++;
                }
            }
            states[i] = st;
        }
        stats.TotalFiles = needFiles;
        stats.TotalBytes = needBytes;
        if (stats.SkippedFiles > 0)
            Log(L.T("send.skipping", stats.SkippedFiles, Format.Bytes(stats.SkippedBytes)));
        Log(L.T("send.transferring", needFiles, Format.Bytes(needBytes), Connections));

        // Criada só se algum arquivo estiver bloqueado; apagada ao fim do envio.
        using var shadows = UseShadowCopy && OperatingSystem.IsWindows() ? new ShadowCopies(Log) : null;
        int next = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Math.Max(1, Connections))
            .Select(_ => Task.Run(() => DataWorker(token, entries, states, chunks, shadows, () => Interlocked.Increment(ref next) - 1, stats, linked.Token), linked.Token))
            .ToArray();
        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            linked.Cancel();
            foreach (var s in states) s?.Handle?.Dispose();
            throw;
        }

        w.Write(Protocol.CmdDone);
        w.Flush();
        int ok = r.ReadInt32();
        int failed = r.ReadInt32();
        int nErr = r.ReadInt32();
        var errors = new List<string>(nErr);
        for (int i = 0; i < nErr; i++) errors.Add(r.ReadString());
        return new TransferResult(ok, failed, errors);
    }

    private async Task DataWorker(byte[] token, List<FileEntry> entries, FileSendState?[] states, List<Chunk> chunks, ShadowCopies? shadows,
        Func<int> take, TransferStats stats, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        client.Client.DualMode = true;
        Protocol.Tune(client.Client);
        await client.ConnectAsync(Host, Port, ct);
        await using var reg = ct.Register(() => client.Dispose());

        var stream = new BufferedStream(client.GetStream(), 256 * 1024);
        using var w = new BinaryWriter(stream);
        using var r = new BinaryReader(stream);
        w.Write(Protocol.ConnData);
        w.Write(token);
        w.Flush();
        if (r.ReadByte() != Protocol.StatusOk) throw new TransferException(L.T("send.dataRefused"));

        var buffer = new byte[Protocol.ChunkSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int idx = take();
            if (idx >= chunks.Count) break;
            var c = chunks[idx];
            var st = states[c.File]!;
            var entry = entries[c.File];

            string? abortReason = null;
            SafeFileHandle? handle = null;
            lock (st)
            {
                if (!st.Failed && st.Handle == null)
                {
                    try
                    {
                        stats.CurrentFile = entry.SourcePath!;
                        st.Handle = OpenSource(entry.SourcePath!);
                    }
                    catch (Exception ex) when (ShadowCopies.IsLockError(ex) && OperatingSystem.IsWindows() && shadows?.Resolve(entry.SourcePath!) is { } shadowPath)
                    {
                        try
                        {
                            st.Handle = OpenSource(shadowPath);
                            Log(L.T("send.fromShadow", entry.SourcePath));
                        }
                        catch (Exception ex2)
                        {
                            st.Failed = true;
                            abortReason = ex2.Message;
                        }
                    }
                    catch (Exception ex)
                    {
                        st.Failed = true;
                        abortReason = ShadowCopies.IsLockError(ex)
                            ? ex.Message + " " + L.T("send.lockedHint")
                            : ex.Message;
                    }
                }
                if (st.Failed && !st.AbortSent)
                {
                    st.AbortSent = true;
                    abortReason ??= L.T("send.readFailed");
                }
                else if (st.Failed)
                {
                    abortReason = null; // outro worker já avisou o receptor
                }
                handle = st.Failed ? null : st.Handle;
            }

            if (handle != null)
            {
                try
                {
                    int got = 0;
                    while (got < c.Length)
                    {
                        int n = RandomAccess.Read(handle, buffer.AsSpan(got, c.Length - got), c.Offset + got);
                        if (n == 0) throw new IOException(L.T("send.fileShrank"));
                        got += n;
                    }
                }
                catch (Exception ex)
                {
                    lock (st)
                    {
                        st.Failed = true;
                        if (!st.AbortSent) { st.AbortSent = true; abortReason = ex.Message; }
                    }
                    handle = null;
                }
            }

            if (abortReason != null)
            {
                Log(L.T("send.readError", entry.SourcePath, abortReason));
                w.Write(c.File);
                w.Write(0L);
                w.Write(Protocol.AbortLength);
                w.Write(abortReason);
            }
            else if (handle != null)
            {
                var data = buffer.AsSpan(0, c.Length);
                w.Write(c.File);
                w.Write(c.Offset);
                w.Write(c.Length);
                w.Write(data);
                w.Write(XxHash3.HashToUInt64(data));
                stats.AddBytes(c.Length);
            }

            lock (st)
            {
                if (--st.RemainingChunks == 0)
                {
                    st.Handle?.Dispose();
                    st.Handle = null;
                    if (st.Failed) stats.FileFailed(); else stats.FileDone();
                }
            }
        }

        w.Write(Protocol.EndOfStream);
        w.Flush();
        // O receptor só responde depois de gravar tudo o que chegou por esta conexão.
        if (r.ReadByte() != Protocol.StatusOk) throw new TransferException(L.T("send.noEndAck"));
    }

    // FileShare.ReadWrite|Delete: consegue ler arquivos que outros programas mantêm abertos sem exclusividade.
    private static SafeFileHandle OpenSource(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
}
