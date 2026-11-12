using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Nbt.Core;

namespace NetBackupTool;

public sealed record ReleaseInfo(Version Version, string Tag, string HtmlUrl, string? AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// Atualização pelas releases do GitHub, só quando o usuário pede. No Windows e no Linux baixa o
/// executável ao lado do atual, confere tamanho e SHA-256 publicados e o .exe novo conclui a troca.
/// No macOS (pacote .app) abre a página de download.
/// </summary>
public static class Updater
{
    public const string ProjectUrl = "https://github.com/leddzeppellin/network-backup-tool";
    public const string FinishArg = "--concluir-atualizacao";

    // NBT_UPDATE_API permite apontar para um servidor de teste (usado para testar a troca do executável).
    private static readonly string LatestApi = Environment.GetEnvironmentVariable("NBT_UPDATE_API")
        ?? "https://api.github.com/repos/leddzeppellin/network-backup-tool/releases/latest";

    /// <summary>Nome do arquivo deste sistema na release.</summary>
    public static string AssetName =>
        OperatingSystem.IsAndroid() ? "NetBackupTool-android.apk"
        : OperatingSystem.IsWindows() ? (IsInstalled ? SetupAsset : "NetBackupTool.exe")
        : OperatingSystem.IsMacOS() ? $"NetBackupTool-macos-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.zip"
        : $"NetBackupTool-linux-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";

    public const string SetupAsset = "NetBackupTool-Setup.exe";

    /// <summary>Versão instalada (Inno Setup deixa o desinstalador ao lado do executável).</summary>
    public static bool IsInstalled =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(Path.GetDirectoryName(Platform.Exe)!, "unins000.exe"));

    /// <summary>
    /// Versão instalada: roda o instalador novo em modo silencioso (pede administrador). Ele fecha o
    /// app, para o serviço, troca os arquivos, religa o serviço e reabre o app.
    /// </summary>
    public static void RunSetup(string setupFile)
    {
        var psi = new ProcessStartInfo(setupFile) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>No macOS o programa é um pacote .app; a troca é feita baixando o pacote novo.</summary>
    public static bool CanSelfUpdate => !OperatingSystem.IsMacOS() && !OperatingSystem.IsAndroid();

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
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Inicia o executável novo para concluir a troca; quem chama encerra o programa logo em seguida.
    /// O executável em uso não é renomeado: o .NET de arquivo único ainda carrega partes dele sob demanda.
    /// </summary>
    public static void StartFinish(string newFile, bool receiving)
    {
        var psi = new ProcessStartInfo(newFile) { UseShellExecute = false };
        psi.ArgumentList.Add(FinishArg);
        psi.ArgumentList.Add(Platform.Exe);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (receiving) psi.ArgumentList.Add("receber");
        Process.Start(psi);
    }

    /// <summary>
    /// Executado pelo executável novo (também quando a versão 1.x atualiza para esta): espera o antigo
    /// fechar, copia-se por cima dele e abre a nova versão no caminho original.
    /// </summary>
    public static Exception? Finish(string target, int oldPid, string[] extraArgs)
    {
        try
        {
            try
            {
                using var old = Process.GetProcessById(oldPid);
                old.WaitForExit(30_000);
            }
            catch (ArgumentException) { /* já fechou */ }

            Exception? last = null;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    File.Copy(Platform.Exe, target, overwrite: true);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(500); // antivírus ou o processo antigo ainda soltando o arquivo
                }
            }
            if (last != null) throw last;

            var psi = new ProcessStartInfo(target) { UseShellExecute = false };
            psi.ArgumentList.Add("--reiniciar");
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Apaga o executável temporário da última atualização (e o ".old" da versão 1.2.0).</summary>
    public static void CleanupOld()
    {
        var exe = Platform.Exe;
        Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (File.Exists(exe + ".new")) File.Delete(exe + ".new");
                    if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                    return;
                }
                catch { await Task.Delay(1000); }
            }
        });
    }
}
