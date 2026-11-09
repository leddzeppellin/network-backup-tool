using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>Caixa de mensagem simples (o Avalonia não tem uma pronta).</summary>
public sealed class Dialogs : Window
{
    public bool Result { get; private set; }

    private Dialogs(string text, string title, bool yesNo, bool error)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = MainWindow.AppIcon;
        MinWidth = 360;
        MaxWidth = 560;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (yesNo)
        {
            var yes = new Button { Content = L.T("dlg.yes"), Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
            var no = new Button { Content = L.T("dlg.no"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
            yes.Click += (_, _) => { Result = true; Close(); };
            no.Click += (_, _) => Close();
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = L.T("dlg.ok"), Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => { Result = true; Close(); };
            buttons.Children.Add(ok);
        }

        var icon = new PathIcon
        {
            Data = Res.Get<Geometry>(error ? "IconShield" : "IconInfo"),
            Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top,
            Foreground = error ? Brushes.IndianRed : Res.Get<IBrush>("AccentText"),
        };
        Content = new Border
        {
            Padding = new Thickness(24, 20),
            Child = new StackPanel
            {
                Spacing = 20,
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            icon,
                            new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 2, 0, 0), MaxWidth = 460 },
                        },
                    },
                    buttons,
                },
            },
        };
    }

    public static Dialogs Create(string text, string title, bool yesNo = false, bool error = false) => new(text, title, yesNo, error);

    /// <summary>Mostra e espera a resposta. Se a janela dona estiver escondida, mostra solta.</summary>
    public static async Task<bool> Show(Window? owner, string text, bool yesNo = false, bool error = false, string? title = null)
    {
        var dlg = new Dialogs(text, title ?? "Network Backup Tool", yesNo, error);
        if (owner is { IsVisible: true })
        {
            await dlg.ShowDialog(owner);
        }
        else
        {
            var done = new TaskCompletionSource();
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.Closed += (_, _) => done.TrySetResult();
            dlg.Show();
            await done.Task;
        }
        return dlg.Result;
    }
}

/// <summary>Aviso discreto no canto da tela, usado quando a janela está escondida (modo persistente).</summary>
public sealed class Toast : Window
{
    private static Toast? _current;

    private Toast(string title, string text, Action? onClick)
    {
        Title = "Network Backup Tool";
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Content = new Border
        {
            Classes = { "card" },
            Width = 340,
            Margin = new Thickness(0),
            Child = new DockPanel
            {
                Children =
                {
                    new Image { Source = MainWindow.AppBitmap, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) },
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                        },
                    },
                },
            },
        };
        PointerPressed += (_, _) => { Close(); onClick?.Invoke(); };
        Opened += (_, _) =>
        {
            var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
            var scale = Screens.Primary?.Scaling ?? 1;
            Position = new PixelPoint(
                area.Right - (int)((Bounds.Width + 16) * scale),
                area.Bottom - (int)((Bounds.Height + 16) * scale));
        };
        DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(6));
    }

    public static void Show(string title, string text, Action? onClick = null)
    {
        _current?.Close();
        _current = new Toast(title, text, onClick);
        _current.Show();
    }
}

