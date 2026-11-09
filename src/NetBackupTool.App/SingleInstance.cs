using System.IO.Pipes;

namespace NetBackupTool;

/// <summary>
/// Uma instância por usuário: a primeira atende num named pipe (no Linux/macOS vira um socket local);
/// abrir o programa de novo só pede para ela mostrar a janela.
/// </summary>
public static class SingleInstance
{
    // NBT_INSTANCE separa uma cópia de teste da que o usuário está usando.
    private static readonly string PipeName = "NetBackupTool-" + Environment.UserName + Environment.GetEnvironmentVariable("NBT_INSTANCE");

    /// <summary>Se outra instância responder, pede para ela aparecer e devolve true.</summary>
    public static bool SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(300);
            client.WriteByte(1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reabertura (idioma, atualização): espera a instância anterior deixar de responder.</summary>
    public static void WaitForPreviousToExit(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && IsRunning()) Thread.Sleep(300);
    }

    private static bool IsRunning()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(200);
            client.WriteByte(0); // 0 = só verificando, não mostrar
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Action onShow)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    if (server.ReadByte() == 1) onShow();
                }
                catch (IOException) { Thread.Sleep(500); }
                catch { return; }
            }
        }) { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }
}
