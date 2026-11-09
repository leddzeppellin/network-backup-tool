using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace NetBackupTool;

public partial class App : Application
{
    public static Settings Settings { get; set; } = new();
    public static StartOptions Options { get; set; } = new();

    /// <summary>Cada plataforma (desktop, Android) monta a interface aqui.</summary>
    public static Action<Application, IApplicationLifetime?>? Startup { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(Settings.Theme);
        Startup?.Invoke(this, ApplicationLifetime);
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme) =>
        Current!.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
