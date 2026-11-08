using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nbt.Core;

/// <summary>
/// Protocolo próprio sobre TCP. Uma conexão de controle (autenticação, manifesto,
/// resultado) e N conexões de dados em paralelo, que carregam blocos de arquivos.
///
/// Controle:
///   C→S  byte ConnControl
///   S→C  nonce[16]
///   C→S  hmac[32] = HMAC-SHA256(código, nonce), string nomeRemetente
///   S→C  byte status; se Ok: token[16], string nomeReceptor
///   C→S  manifesto (ver Manifest.Write)
///   S→C  byte status; se Ok: bitset dos arquivos que faltam; senão string motivo
///   ... dados ...
///   C→S  byte CmdDone
///   S→C  int ok, int falhas, int nErros, string[nErros]
///
/// Dados (por conexão):
///   C→S  byte ConnData, token[16]
///   S→C  byte status
///   C→S  quadros: int arquivo, long offset, int tamanho, bytes[tamanho], ulong xxhash3
///        tamanho = -1 → arquivo abortado pelo remetente, seguido de string motivo
///        arquivo = -1 → fim da conexão
/// </summary>
public static class Protocol
{
    public const int DefaultPort = 50505;
    public const int DiscoveryPort = 50506;

    public const byte ConnControl = 1;
    public const byte ConnData = 2;

    public const byte StatusOk = 0;
    public const byte StatusBadCode = 1;
    public const byte StatusBusy = 2;
    public const byte StatusRejected = 3;

    public const byte CmdDone = 1;

    public const int EndOfStream = -1;
    public const int AbortLength = -1;

    /// <summary>Tamanho de bloco: arquivos grandes são fatiados e os blocos vão por várias conexões.</summary>
    public const int ChunkSize = 8 * 1024 * 1024;

    public const int MaxFailedAuth = 5;

    public static byte[] ComputeAuth(string code, byte[] nonce) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()), nonce);

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static void Tune(Socket s)
    {
        s.NoDelay = true;
        // Buffers grandes ajudam a manter o "cano" cheio em 1/2,5/10 Gbps.
        s.SendBufferSize = 4 * 1024 * 1024;
        s.ReceiveBufferSize = 4 * 1024 * 1024;
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    public static byte[] ReadBytesExact(this BinaryReader r, int count)
    {
        var b = new byte[count];
        r.BaseStream.ReadExactly(b);
        return b;
    }
}
