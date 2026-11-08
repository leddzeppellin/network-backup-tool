using Avalonia.Controls;
using Nbt.Core;

namespace NetBackupTool;

public partial class SettingsView : UserControl
{
    private static readonly string[] LanguageCodes = ["auto", "pt", "en", "es"];
    private static readonly string[] ThemeCodes = ["system", "light", "dark"];

    public SettingsView() : this(null!) { } // designer

    public SettingsView(MainWindow main)
    {
        InitializeComponent();
        if (main == null) return;
        var s = main.Settings;

        LanguageBox.ItemsSource = new[] { L.T("menu.langAuto"), "Português", "English", "Español" };
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, s.Language));
        LanguageBox.SelectionChanged += async (_, _) =>
        {
            var code = LanguageCodes[Math.Max(0, LanguageBox.SelectedIndex)];
            if (code == s.Language) return;
            s.Language = code;
            s.Save();
            L.Set(code);
            if (main.Busy)
            {
                await Dialogs.Show(main, L.T("busy.wait"));
                return;
            }
            if (await Dialogs.Show(main, L.T("menu.langChanged"), yesNo: true)) main.Restart();
        };

        ThemeBox.ItemsSource = new[] { L.T("set.themeSystem"), L.T("set.themeLight"), L.T("set.themeDark") };
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeCodes, s.Theme));
        ThemeBox.SelectionChanged += (_, _) =>
        {
            s.Theme = ThemeCodes[Math.Max(0, ThemeBox.SelectedIndex)];
            s.Save();
            App.ApplyTheme(s.Theme);
        };

        TraySwitch.IsChecked = s.KeepInTray;
        AutostartSwitch.IsChecked = Platform.IsAutostart();
        FixedSwitch.IsChecked = s.FixedCode.Length == 6;
        TraySwitch.IsCheckedChanged += (_, _) =>
        {
            s.KeepInTray = TraySwitch.IsChecked == true;
            s.Save();
            main.SetTrayVisible(s.KeepInTray);
            if (!s.KeepInTray && AutostartSwitch.IsChecked == true) AutostartSwitch.IsChecked = false;
        };
        AutostartSwitch.IsCheckedChanged += (_, _) =>
        {
            bool on = AutostartSwitch.IsChecked == true;
            try
            {
                Platform.SetAutostart(on);
            }
            catch (Exception ex)
            {
                _ = Dialogs.Show(main, ex.Message, error: true);
            }
            // Iniciar escondido só faz sentido com o ícone perto do relógio.
            if (on && TraySwitch.IsChecked != true) TraySwitch.IsChecked = true;
        };
        FixedSwitch.IsCheckedChanged += (_, _) => main.ReceivePage.FixedCodeChanged(FixedSwitch.IsChecked == true);

        ConnectionsBox.Value = Math.Clamp(s.Connections, 1, 16);
        ConnectionsBox.ValueChanged += (_, _) =>
        {
            s.Connections = (int)(ConnectionsBox.Value ?? 4);
            s.Save();
        };
        ExcludesBox.Text = s.Excludes;
        ExcludesBox.LostFocus += (_, _) =>
        {
            s.Excludes = ExcludesBox.Text ?? "";
            s.Save();
        };
    }
}
