using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool;

public sealed record ReleaseInfo(Version Version, string Tag, string HtmlUrl, string? AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// Atualização pelas releases do GitHub. Só acontece quando o usuário pede (menu Ajuda):
/// consulta a última release, baixa o .exe ao lado do atual, confere tamanho e SHA-256
/// publicados, troca os arquivos e reabre.
/// </summary>
public static class Updater
{
    public const string ProjectUrl = "https://github.com/leddzeppellin/network-backup-tool";
    private const string LatestApi = "https://api.github.com/repos/leddzeppellin/network-backup-tool/releases/latest";
    private const string AssetName = "NetBackupTool.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetBackupTool", Current.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(LatestApi, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? Normalize(v) : new Version(0, 0, 0);
        string? url = null, sha = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            url = a.GetProperty("browser_download_url").GetString();
            size = a.GetProperty("size").GetInt64();
            // O GitHub publica "digest": "sha256:<hex>" para cada arquivo da release.
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                sha = digest["sha256:".Length..];
        }
        return new ReleaseInfo(version, tag, root.GetProperty("html_url").GetString() ?? ProjectUrl + "/releases", url, size, sha);
    }

    /// <summary>Baixa para <paramref name="dest"/> e confere tamanho e hash; apaga o arquivo se não conferir.</summary>
    public static async Task DownloadAsync(ReleaseInfo release, string dest, IProgress<int> progress, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[256 * 1024];
                int n, last = -1;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    total += n;
                    int pct = release.AssetSize > 0 ? (int)(100 * total / release.AssetSize) : 0;
                    if (pct != last) progress.Report(last = pct);
                }
            }
            var hex = Convert.ToHexString(sha.GetHashAndReset());
            if ((release.AssetSize > 0 && total != release.AssetSize) ||
                (release.Sha256 != null && !hex.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(L.T("upd.badHash"));
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Troca o .exe em uso pelo novo (o Windows permite renomear um executável aberto) e
    /// inicia a nova versão; quem chama encerra o programa logo em seguida.
    /// </summary>
    public static void Replace(string newFile)
    {
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        try { File.Delete(old); } catch { }
        File.Move(exe, old);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(old, exe); // volta como estava
            throw;
        }
    }

    /// <summary>Apaga o .exe antigo deixado pela última atualização.</summary>
    public static void CleanupOld()
    {
        try { File.Delete(Environment.ProcessPath + ".old"); } catch { }
        try { File.Delete(Environment.ProcessPath + ".new"); } catch { }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
