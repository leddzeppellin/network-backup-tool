namespace Nbt.Core;

public static class Format
{
    public static string Bytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : $"{b:0.0#} {u[i]}";
    }

    public static string Speed(double bytesPerSec) =>
        $"{Bytes(bytesPerSec)}/s ({bytesPerSec * 8 / 1_000_000:0} Mbps)";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{t.Seconds}s";
}
