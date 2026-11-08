using System.Diagnostics;
using System.Security.Cryptography;
using Nbt.Core;

// As verificações abaixo procuram trechos das mensagens em português.
L.Set("pt");

// Uso manual: Nbt.Smoke find  — só a descoberta, como no botão "Procurar na rede".
if (args.Length == 1 && args[0] == "find")
{
    foreach (var r in await Discovery.FindAsync(TimeSpan.FromSeconds(1.5))) Console.WriteLine(r);
    Console.WriteLine("fim");
    return 0;
}

// Uso manual: Nbt.Smoke send <host> <código> <pasta>  — envia para um receptor já aberto (ex.: o app).
if (args.Length == 4 && args[0] == "send")
{
    var sc = Manifest.Scan([args[3]], Manifest.DefaultExcludes, default);
    var st = new TransferStats();
    var sw0 = Stopwatch.StartNew();
    var res0 = await new Sender { Host = args[1], Code = args[2], SenderName = "PC-CLIENTE-TESTE", Log = Console.WriteLine }.SendAsync(sc, st, default);
    Console.WriteLine($"{res0.FilesOk} ok, {res0.FilesFailed} falhas, {Format.Speed(st.BytesDone / sw0.Elapsed.TotalSeconds)}");
    return 0;
}

// Teste de ponta a ponta em loopback: gera dados, envia, confere byte a byte, reenvia (deve pular tudo).
var root = Path.Combine(Path.GetTempPath(), "nbt_smoke_" + Guid.NewGuid().ToString("N")[..8]);
var src = Path.Combine(root, "origem");
var dst = Path.Combine(root, "destino");
long bigSize = args.Length > 0 ? long.Parse(args[0]) * 1024 * 1024 : 512L * 1024 * 1024;
int failures = 0;
void Check(bool cond, string what) { Console.WriteLine((cond ? "  OK   " : "  FALHA ") + what); if (!cond) failures++; }

Directory.CreateDirectory(Path.Combine(src, "sub", "fundo", "vazia"));
for (int i = 0; i < 2000; i++)
    File.WriteAllBytes(Path.Combine(src, "sub", $"pequeno_{i}.bin"), RandomNumberGenerator.GetBytes(Random.Shared.Next(0, 64 * 1024)));
File.WriteAllBytes(Path.Combine(src, "sub", "fundo", "vazio.txt"), []);
File.WriteAllText(Path.Combine(src, "acentuação ç.txt"), "olá");
File.WriteAllText(Path.Combine(src, "lixo.tmp"), "não deve ir");
using (var fs = File.Create(Path.Combine(src, "grande.bin")))
{
    var buf = new byte[4 * 1024 * 1024];
    for (long w = 0; w < bigSize; w += buf.Length) { RandomNumberGenerator.Fill(buf); fs.Write(buf, 0, (int)Math.Min(buf.Length, bigSize - w)); }
    fs.Write([1, 2, 3]); // tamanho não múltiplo do bloco
}
var lockedPath = Path.Combine(src, "bloqueado.dat");
File.WriteAllText(lockedPath, "segredo");
var locker = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

const int port = 50615;
var receiver = new Receiver { DestinationRoot = dst, Port = port, Name = "RECEPTOR-TESTE" };
receiver.Log += m => Console.WriteLine("  [receptor] " + m);
receiver.Start();

Console.WriteLine("Descoberta:");
var found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5));
Console.WriteLine("  encontrados: " + string.Join(", ", found));

Console.WriteLine("Código errado:");
try
{
    await new Sender { Host = "127.0.0.1", Port = port, Code = "000000" == receiver.Code ? "111111" : "000000" }
        .SendAsync(Manifest.Scan([src], Manifest.DefaultExcludes, default), new TransferStats(), default);
    Check(false, "deveria recusar");
}
catch (TransferException ex) { Check(ex.Message.Contains("Código"), "recusado: " + ex.Message); }

async Task<(TransferResult, TransferStats, double)> Run()
{
    var scan = Manifest.Scan([src], Manifest.DefaultExcludes, default);
    var stats = new TransferStats();
    var sw = Stopwatch.StartNew();
    var res = await new Sender { Host = "127.0.0.1", Port = port, Code = receiver.Code, SenderName = "PC-CLIENTE", Connections = 4, Log = m => Console.WriteLine("  [remetente] " + m) }
        .SendAsync(scan, stats, default);
    return (res, stats, sw.Elapsed.TotalSeconds);
}

Console.WriteLine("Primeira cópia:");
var (r1, s1, t1) = await Run();
Console.WriteLine($"  {Format.Bytes(s1.BytesDone)} em {t1:0.0}s = {Format.Speed(s1.BytesDone / t1)}");
bool admin = ShadowCopies.IsElevated;
Console.WriteLine(admin ? "  (administrador: arquivo bloqueado deve vir da Shadow Copy)" : "  (sem admin: arquivo bloqueado deve falhar)");
Check(r1.FilesOk == (admin ? 2004 : 2003), $"arquivos ok = {r1.FilesOk}");
if (admin)
    Check(r1.FilesFailed == 0, "arquivo bloqueado copiado via Shadow Copy");
else
    Check(r1.FilesFailed == 1 && r1.Errors.Any(e => e.Contains("bloqueado.dat") && e.Contains("administrador")), "arquivo bloqueado reportado com a dica de admin");

var mirror = Path.Combine(dst, "PC-CLIENTE", Manifest.MapToRelative(src));
bool same = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
    .Where(f => !f.EndsWith(".tmp") && !f.EndsWith("bloqueado.dat"))
    .All(f =>
    {
        var d = Path.Combine(mirror, Path.GetRelativePath(src, f));
        return File.Exists(d) && Sha(f).SequenceEqual(Sha(d))
            && Math.Abs((File.GetLastWriteTimeUtc(f) - File.GetLastWriteTimeUtc(d)).TotalSeconds) < 1;
    });
Check(same, "conteúdo e data idênticos");
Check(Directory.Exists(Path.Combine(mirror, "sub", "fundo", "vazia")), "pasta vazia criada");
Check(!File.Exists(Path.Combine(mirror, "lixo.tmp")), "*.tmp excluído");
if (admin)
    Check(File.ReadAllText(Path.Combine(mirror, "bloqueado.dat")) == "segredo", "conteúdo do bloqueado correto");
else
    Check(!File.Exists(Path.Combine(mirror, "bloqueado.dat")), "sem arquivo parcial do bloqueado");
Check(Directory.EnumerateFiles(Path.Combine(dst, "PC-CLIENTE"), "_NBT_relatorio_*").Any(), "relatório gravado");

Console.WriteLine("Segunda cópia (deve pular):");
locker.Dispose();
var (r2, s2, _) = await Run();
Check(s2.SkippedFiles == (admin ? 2004 : 2003) && r2.FilesOk == (admin ? 0 : 1), $"pulados = {s2.SkippedFiles}, copiados = {r2.FilesOk}");

Console.WriteLine("Caminho malicioso:");
Check(Throws(() => Manifest.ResolveSafe(dst, @"..\..\Windows\x.dll")), "'..' recusado");
Check(Throws(() => Manifest.ResolveSafe(dst, @"C:\Windows\x.dll")) || !Manifest.ResolveSafe(dst, @"C:\Windows\x.dll").StartsWith(@"C:\Windows"), "absoluto contido no destino");

Console.WriteLine("Traduções:");
{
    var problems = L.Problems().ToList();
    foreach (var pr in problems) Console.WriteLine("    " + pr);
    Check(problems.Count == 0, "todos os textos existem em pt/en/es com os mesmos marcadores");
    foreach (var lang in L.Languages)
    {
        L.Set(lang);
        _ = L.T("send.done", "1s", 1, 2, 3, "1 MB/s"); // não pode lançar FormatException
    }
    L.Set("en");
    Check(L.T("send.badCode").StartsWith("Wrong code"), "inglês ativo");
    L.Set("pt");
}

Console.WriteLine("Código fixo:");
{
    var fixedRx = new Receiver { DestinationRoot = dst, Port = port + 1, FixedCode = true };
    fixedRx.SetCode("424242");
    fixedRx.Start();
    var tiny = Manifest.Scan([Path.Combine(src, "acentuação ç.txt")], "", default);
    async Task<bool> Try(string code)
    {
        try { await new Sender { Host = "127.0.0.1", Port = port + 1, Code = code }.SendAsync(tiny, new TransferStats(), default); return true; }
        catch (TransferException) { return false; }
    }
    for (int i = 0; i < Protocol.MaxFailedAuth; i++) await Try("000000");
    Check(fixedRx.Code == "424242", "código fixo não muda após tentativas erradas");
    Check(!await Try("424242"), "código certo recusado durante o bloqueio");
    fixedRx.Stop();
}

receiver.Stop();
try { Directory.Delete(root, true); } catch { }
if (admin)
{
    var ledger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBackupTool", "shadows.txt");
    Check(!File.Exists(ledger) || File.ReadAllText(ledger).Trim().Length == 0, "Shadow Copy apagada ao fim");
}
Console.WriteLine(failures == 0 ? "TUDO OK" : $"{failures} FALHA(S)");
return failures == 0 ? 0 : 1;

static byte[] Sha(string path) { using var s = File.OpenRead(path); return SHA256.HashData(s); }
static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
