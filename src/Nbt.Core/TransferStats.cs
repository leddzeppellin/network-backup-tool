namespace Nbt.Core;

/// <summary>Contadores lidos pela interface num timer; escritos pelas threads de transferência.</summary>
public sealed class TransferStats
{
    private long _bytesDone;
    private int _filesDone;
    private int _filesFailed;

    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public int SkippedFiles { get; set; }
    public long SkippedBytes { get; set; }
    public volatile string CurrentFile = "";

    public long BytesDone => Interlocked.Read(ref _bytesDone);
    public int FilesDone => Volatile.Read(ref _filesDone);
    public int FilesFailed => Volatile.Read(ref _filesFailed);

    public void AddBytes(long n) => Interlocked.Add(ref _bytesDone, n);
    public void FileDone() => Interlocked.Increment(ref _filesDone);
    public void FileFailed() => Interlocked.Increment(ref _filesFailed);
}

public sealed record TransferResult(int FilesOk, int FilesFailed, IReadOnlyList<string> Errors);
