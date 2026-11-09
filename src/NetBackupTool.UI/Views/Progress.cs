using System.Diagnostics;
using Avalonia.Controls;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>Velocidade média dos últimos ~3 s, para o número não ficar pulando.</summary>
public sealed class SpeedMeter
{
    private readonly Queue<(double T, long Bytes)> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Reset() => _samples.Clear();

    public double Sample(long bytes)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_samples.Count > 0 && bytes < _samples.Last().Bytes) _samples.Clear();
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && now - _samples.Peek().T > 3) _samples.Dequeue();
        var first = _samples.Peek();
        return now - first.T > 0.2 ? (bytes - first.Bytes) / (now - first.T) : 0;
    }
}

public static class ProgressText
{
    /// <summary>"12 de 300 arquivos — 1,2 GB de 4 GB — 110 MB/s — faltam 30s" e o valor da barra (0–1000).</summary>
    public static (string Text, int Value) Describe(TransferStats s, SpeedMeter meter)
    {
        long done = s.BytesDone, total = s.TotalBytes;
        int value = total > 0 ? (int)Math.Min(1000, 1000 * done / total)
            : s.TotalFiles > 0 && s.FilesDone + s.FilesFailed >= s.TotalFiles ? 1000 : 0;
        var speed = meter.Sample(done);
        var eta = speed > 1 && total > done ? L.T("progress.eta", Format.Duration(TimeSpan.FromSeconds((total - done) / speed))) : "";
        var text = L.T("progress", s.FilesDone + s.FilesFailed, s.TotalFiles, Format.Bytes(done), Format.Bytes(total), Format.Speed(speed)) + eta +
                   (s.FilesFailed > 0 ? L.T("progress.failed", s.FilesFailed) : "");
        return (text, value);
    }

    public static void Append(TextBox log, string line)
    {
        var current = log.Text ?? "";
        if (current.Length > 1_000_000) current = "";
        log.Text = current + $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}";
        log.CaretIndex = log.Text.Length;
    }
}
