using Avalonia.Controls;
using Nbt.Core;

namespace NetBackupTool;

public partial class AboutView : UserControl
{
    private readonly MainWindow _main;

    public AboutView() : this(null!) { } // designer

    public AboutView(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        VersionText.Text = L.T("about.version", Updater.Current.ToString(3));
        if (main == null) return;
        ProjectButton.Click += (_, _) => Platform.OpenUrl(Updater.ProjectUrl);
        ReportsButton.Click += async (_, _) =>
        {
            if (main.Settings.DestinationRoot.Length == 0) await Dialogs.Show(main, L.T("recv.noFolder"));
            else Platform.OpenFolder(main.Settings.DestinationRoot);
        };
        UpdateButton.Click += async (_, _) => await CheckForUpdates();
    }

    private void Status(string? text)
    {
        UpdateStatus.IsVisible = text != null;
        UpdateStatus.Text = text;
    }

    private async Task CheckForUpdates()
    {
        if (_main.Busy)
        {
            await Dialogs.Show(_main, L.T("busy.wait"));
            return;
        }
        UpdateButton.IsEnabled = false;
        try
        {
            Status(L.T("upd.checking"));
            ReleaseInfo latest;
            try
            {
                latest = await Updater.GetLatestAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Status(null);
                await Dialogs.Show(_main, L.T("upd.failed", ex.Message), error: true, title: L.T("upd.title"));
                return;
            }

            var current = Updater.Current;
            Status(null);
            if (latest.Version <= current)
            {
                await Dialogs.Show(_main, L.T("upd.upToDate", current.ToString(3)), title: L.T("upd.title"));
                return;
            }
            if (!Updater.CanSelfUpdate || latest.AssetUrl == null)
            {
                var text = (latest.AssetUrl == null ? L.T("upd.noAsset", latest.Version.ToString(3)) : L.T("upd.manual", latest.Version.ToString(3), current.ToString(3)))
                           + "\n\n" + L.T("upd.openPage");
                if (await Dialogs.Show(_main, text, yesNo: true, title: L.T("upd.title"))) Platform.OpenUrl(latest.HtmlUrl);
                return;
            }
            if (!await Dialogs.Show(_main, L.T("upd.available", latest.Version.ToString(3), current.ToString(3)), yesNo: true, title: L.T("upd.title")))
                return;

            var newFile = Platform.Exe + ".new";
            UpdateProgress.IsVisible = true;
            try
            {
                var progress = new Progress<int>(p =>
                {
                    UpdateProgress.Value = p;
                    Status(L.T("upd.downloading", latest.Version.ToString(3), p));
                });
                await Updater.DownloadAsync(latest, newFile, progress, CancellationToken.None);
                _main.ExitForUpdate(newFile);
            }
            catch (InvalidDataException ex)
            {
                await Dialogs.Show(_main, ex.Message, error: true, title: L.T("upd.title"));
            }
            catch (Exception ex)
            {
                // Pasta sem permissão de escrita ou rede fora do ar.
                if (await Dialogs.Show(_main, L.T("upd.cantReplace", ex.Message), yesNo: true, error: true, title: L.T("upd.title")))
                    Platform.OpenUrl(latest.HtmlUrl);
            }
            finally
            {
                UpdateProgress.IsVisible = false;
                Status(null);
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }
}
