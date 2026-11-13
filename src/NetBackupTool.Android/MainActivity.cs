using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Provider;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nbt.Core;
using NetBackupTool;
using AndroidEnv = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;

namespace NetBackupTool.Droid;

[Activity(
    Label = "Network Backup Tool",
    Theme = "@style/NbtTheme",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<App>, IAppHost
{
    private TaskCompletionSource<bool>? _storageWait;
    private MainView? _view;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.Settings = NetBackupTool.Settings.Load();
        L.Set(App.Settings.Language);
        Platform.DeviceName = DeviceName();
#if PLAYSTORE
        Platform.UpdatesByStore = true;
#endif
        Platform.StorageRoot = AndroidEnv.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
        Platform.EnsureStorageAccess = EnsureStorageAccess;
        Platform.AcquireMulticast = AcquireMulticast;
        Platform.OpenUrlHook = url => StartActivity(new Intent(Intent.ActionView, AndroidUri.Parse(url)));
        Platform.OpenFolderHook = OpenFolder;
        Platform.OpenFileHook = OpenFile;
        Platform.SaveSecret = (k, v) => AndroidSecrets.Save(this, k, v);
        Platform.LoadSecret = k => AndroidSecrets.Load(this, k);
        Platform.CacheDir = CacheDir?.AbsolutePath;
        App.Startup = (_, lifetime) =>
        {
            if (lifetime is ISingleViewApplicationLifetime single)
                single.MainView = _view = new MainView(this, App.Options);
        };
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }

    /// <summary>"Samsung SM-A546E" em vez do "localhost" que o .NET devolve no Android.</summary>
    private static string DeviceName()
    {
        var maker = Build.Manufacturer ?? "";
        var model = Build.Model ?? "Android";
        if (maker.Length > 0) maker = char.ToUpperInvariant(maker[0]) + maker[1..];
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}".Trim();
    }

    // ---------------------------------------------------------------- permissão de arquivos

    private async Task<bool> EnsureStorageAccess()
    {
        if (HasStorageAccess()) return true;
        if (_view != null) await _view.Ask(L.T("android.needStorage"));
        _storageWait = new TaskCompletionSource<bool>();
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Android 11+: tela "Acesso a todos os arquivos" deste app; o resultado chega no OnResume.
            try
            {
                StartActivity(new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, AndroidUri.Parse("package:" + PackageName)));
            }
            catch (ActivityNotFoundException)
            {
                StartActivity(new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission));
            }
        }
        else
        {
            RequestPermissions([Android.Manifest.Permission.ReadExternalStorage, Android.Manifest.Permission.WriteExternalStorage], 1);
        }
        return await _storageWait.Task;
    }

    private bool HasStorageAccess() =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
            ? AndroidEnv.IsExternalStorageManager
            : CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    protected override void OnResume()
    {
        base.OnResume();
        _storageWait?.TrySetResult(HasStorageAccess());
        _storageWait = null;
        Dispatcher.UIThread.Post(Platform.RaiseResumed);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _storageWait?.TrySetResult(HasStorageAccess());
        _storageWait = null;
    }

    // ---------------------------------------------------------------- rede

    /// <summary>O Wi-Fi do Android descarta broadcast para economizar bateria; isto libera enquanto recebe.</summary>
    private IDisposable AcquireMulticast()
    {
        var wifi = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
        var lockObj = wifi?.CreateMulticastLock("NetBackupTool");
        lockObj?.SetReferenceCounted(false);
        lockObj?.Acquire();
        return new Release(() =>
        {
            if (lockObj?.IsHeld == true) lockObj.Release();
        });
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private void OpenFolder(string path)
    {
        // Abre o gerenciador de arquivos na pasta (melhor esforço: nem todo aparelho aceita).
        try
        {
            var root = Platform.StorageRoot ?? "/storage/emulated/0";
            var rel = path.StartsWith(root) ? path[root.Length..].TrimStart('/') : "";
            var uri = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + rel);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, DocumentsContract.Document.MimeTypeDir);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(intent);
        }
        catch (Exception)
        {
            Android.Widget.Toast.MakeText(this, path, Android.Widget.ToastLength.Long)?.Show();
        }
    }

    /// <summary>Abre uma cópia local no app escolhido pelo usuário, que pode salvar por cima dela.</summary>
    private void OpenFile(string path)
    {
        try
        {
            var file = new Java.IO.File(path);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, "io.github.leddzeppellin.netbackuptool.files", file);
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) ?? "*/*";
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mime);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Android.Widget.Toast.MakeText(this, L.T("sta.noApp"), Android.Widget.ToastLength.Long)?.Show();
        }
        catch (Exception ex)
        {
            Android.Widget.Toast.MakeText(this, ex.Message, Android.Widget.ToastLength.Long)?.Show();
        }
    }

    // ---------------------------------------------------------------- IAppHost

    public bool IsDesktop => false;

    public void Notify(string title, string text) =>
        RunOnUiThread(() => Android.Widget.Toast.MakeText(this, $"{title}: {text}", Android.Widget.ToastLength.Long)?.Show());

    public void ShowWindow() { }

    public void SetTrayVisible(bool visible) { }

    /// <summary>Com a tela apagada o Android corta a rede: durante uma cópia a tela fica ligada.</summary>
    public void KeepAwake(bool on) => RunOnUiThread(() =>
    {
        if (on) Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        else Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
    });

    public void Restart()
    {
        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (intent == null) return;
        intent.AddFlags(ActivityFlags.ClearTask | ActivityFlags.NewTask);
        StartActivity(intent);
        Java.Lang.JavaSystem.Exit(0);
    }

    public void ExitApp()
    {
        FinishAndRemoveTask();
        Java.Lang.JavaSystem.Exit(0);
    }

    public void ExitForUpdate(string newFile) { }
}
