using System.Net;
using SMBLibrary;
using SMBLibrary.Client;
using Nbt.Core;
using FileAttributes = SMBLibrary.FileAttributes;

namespace NetBackupTool.Agent;

public sealed record SmbEntry(string Name, bool IsFolder, long Size, DateTime Modified);

/// <summary>
/// Acesso às pastas da empresa pelo próprio app (Android, que não monta compartilhamentos do Windows).
/// Uma conexão SMB2/3 por vez; caminhos relativos à raiz do compartilhamento, separados por "\".
/// </summary>
public sealed class SmbBrowser : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SMB2Client? _client;
    private ISMBFileStore? _store;
    private readonly string _host, _share, _login, _password;

    public SmbBrowser(string host, string share, string login, string password)
    {
        _host = host;
        _share = share;
        _login = login;
        _password = password;
    }

    /// <summary>Conecta e entra no compartilhamento. Devolve null se deu certo, ou a mensagem para a pessoa.</summary>
    public Task<string?> ConnectAsync() => Run(() => Open());

    private string? Open()
    {
        Close();
        IPAddress? ip;
        if (!IPAddress.TryParse(_host, out ip))
        {
            try { ip = Dns.GetHostAddresses(_host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
            catch { ip = null; }
        }
        if (ip == null) return L.T("sta.noServer", _host);

        var client = new SMB2Client();
        if (!client.Connect(ip, SMBTransportType.DirectTCPTransport)) return L.T("sta.noServer", _host);
        // Conta local do servidor: domínio vazio (o servidor usa o próprio nome).
        var status = client.Login("", _login, _password);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            // Diagnóstico (logcat no Android): o código exato que o servidor devolveu.
            Console.Error.WriteLine($"NBT SMB login {_login}@{ip}: {status}");
            client.Disconnect();
            return status switch
            {
                NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_WRONG_PASSWORD => L.T("sta.badLogin"),
                NTStatus.STATUS_ACCOUNT_DISABLED or NTStatus.STATUS_ACCOUNT_LOCKED_OUT or NTStatus.STATUS_PASSWORD_EXPIRED
                    or NTStatus.STATUS_PASSWORD_MUST_CHANGE or NTStatus.STATUS_ACCOUNT_RESTRICTION => L.T("sta.accountProblem"),
                _ => L.T("sta.connectFailed", status.ToString()),
            };
        }
        var store = client.TreeConnect(_share, out status);
        if (status != NTStatus.STATUS_SUCCESS || store == null)
        {
            client.Logoff();
            client.Disconnect();
            return status == NTStatus.STATUS_BAD_NETWORK_NAME ? L.T("sta.noShare", _share, _host) : L.T("sta.denied");
        }
        _client = client;
        _store = store;
        return null;
    }

    private void Close()
    {
        try { _store?.Disconnect(); } catch { }
        try { _client?.Logoff(); } catch { }
        try { _client?.Disconnect(); } catch { }
        _store = null;
        _client = null;
    }

    public void Dispose()
    {
        _gate.Wait();
        try { Close(); } finally { _gate.Release(); }
    }

    /// <summary>Executa em segundo plano, um pedido por vez, religando se a conexão caiu.</summary>
    private Task<T> Run<T>(Func<T> f) => Task.Run(async () =>
    {
        await _gate.WaitAsync();
        try { return f(); }
        finally { _gate.Release(); }
    });

    private ISMBFileStore Store()
    {
        if (_store == null || _client is not { IsConnected: true })
        {
            var error = Open();
            if (error != null) throw new IOException(error);
        }
        return _store!;
    }

    private static void Check(NTStatus status)
    {
        if (status == NTStatus.STATUS_SUCCESS) return;
        throw new IOException(status switch
        {
            NTStatus.STATUS_ACCESS_DENIED => L.T("sta.denied"),
            NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => L.T("sta.notFound"),
            NTStatus.STATUS_SHARING_VIOLATION => L.T("sta.inUse"),
            NTStatus.STATUS_OBJECT_NAME_COLLISION => L.T("sta.exists"),
            NTStatus.STATUS_DISK_FULL => L.T("sta.diskFull"),
            _ => status.ToString(),
        });
    }

    // ---------------------------------------------------------------- pastas

    public Task<List<SmbEntry>> ListAsync(string path) => Run(() =>
    {
        var store = Store();
        Check(store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null));
        try
        {
            var status = store.QueryDirectory(out var items, handle, "*", FileInformationClass.FileDirectoryInformation);
            if (status != NTStatus.STATUS_NO_MORE_FILES) Check(status);
            return (items ?? new())
                .OfType<FileDirectoryInformation>()
                .Where(i => i.FileName != "." && i.FileName != ".." && !i.FileAttributes.HasFlag(FileAttributes.Hidden))
                .Select(i => new SmbEntry(i.FileName, i.FileAttributes.HasFlag(FileAttributes.Directory), i.EndOfFile, i.LastWriteTime.ToLocalTime()))
                .OrderByDescending(e => e.IsFolder)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally { store.CloseFile(handle); }
    });

    public Task CreateFolderAsync(string path) => Run(() =>
    {
        var store = Store();
        Check(store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_WRITE, FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null));
        store.CloseFile(handle);
        return true;
    });

    // ---------------------------------------------------------------- arquivos

    /// <summary>Baixa para um arquivo local. Devolve a data de alteração do arquivo no servidor.</summary>
    public Task<DateTime> DownloadAsync(string path, string localFile, IProgress<long>? progress = null) => Run(() =>
    {
        var store = Store();
        Check(store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            using var output = File.Create(localFile);
            long offset = 0;
            int chunk = (int)Math.Min(_client!.MaxReadSize, 1024 * 1024);
            while (true)
            {
                var status = store.ReadFile(out var data, handle, offset, chunk);
                if (status == NTStatus.STATUS_END_OF_FILE || (status == NTStatus.STATUS_SUCCESS && (data == null || data.Length == 0))) break;
                Check(status);
                output.Write(data!, 0, data!.Length);
                offset += data.Length;
                progress?.Report(offset);
            }
            store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation);
            return info is FileBasicInformation b ? b.LastWriteTime.Time ?? DateTime.Now : DateTime.Now;
        }
        finally { store.CloseFile(handle); }
    });

    /// <summary>Envia um arquivo; substitui se já existir.</summary>
    public Task UploadAsync(Stream input, string path, IProgress<long>? progress = null) => Run(() =>
    {
        var store = Store();
        Check(store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null));
        try
        {
            var buffer = new byte[(int)Math.Min(_client!.MaxWriteSize, 1024 * 1024)];
            long offset = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                var data = n == buffer.Length ? buffer : buffer[..n];
                Check(store.WriteFile(out var written, handle, offset, data));
                offset += written;
                progress?.Report(offset);
            }
        }
        finally { store.CloseFile(handle); }
        return true;
    });

    /// <summary>Data de alteração no servidor (para saber se alguém mudou o arquivo enquanto estava aberto aqui).</summary>
    public Task<DateTime?> ModifiedAsync(string path) => Run<DateTime?>(() =>
    {
        var store = Store();
        var status = store.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS) return null;
        try
        {
            store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation);
            return info is FileBasicInformation b ? b.LastWriteTime.Time : null;
        }
        finally { store.CloseFile(handle); }
    });

    public static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;
}
