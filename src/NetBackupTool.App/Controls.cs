using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Nbt.Core;

namespace NetBackupTool;

/// <summary>Recursos do App.axaml (cores do tema atual e ícones), acessíveis de qualquer lugar do código.</summary>
public static class Res
{
    public static T Get<T>(string key) where T : class
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is T t
            ? t
            : throw new KeyNotFoundException("Recurso não encontrado: " + key);
    }
}

/// <summary>Texto traduzido no XAML: Text="{l:T nav.send}".</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}

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

/// <summary>Seis caixas para o código: avança sozinho, volta com Backspace e aceita colar o código inteiro.</summary>
public sealed class CodeBox : StackPanel
{
    private readonly TextBox[] _boxes = new TextBox[6];

    public event Action? CodeChanged;

    public CodeBox()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            var box = new TextBox { Classes = { "digit" } };
            box.AddHandler(TextInputEvent, (_, e) =>
            {
                if (e.Text is { Length: > 0 } t && !t.All(char.IsDigit)) e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            box.TextChanged += (_, _) =>
            {
                if (box.Text is { Length: 1 } && idx < 5) _boxes[idx + 1].Focus();
                CodeChanged?.Invoke();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Back && string.IsNullOrEmpty(box.Text) && idx > 0)
                {
                    _boxes[idx - 1].Text = "";
                    _boxes[idx - 1].Focus();
                    e.Handled = true;
                }
            };
            box.PastingFromClipboard += async (_, e) =>
            {
                var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                var text = clip == null ? null : await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clip);
                var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
                if (digits.Length >= 2)
                {
                    e.Handled = true;
                    Code = digits;
                }
            };
            _boxes[i] = box;
            Children.Add(box);
            if (i == 2) Children.Add(new Border { Width = 6 });
        }
    }

    public string Code
    {
        get => string.Concat(_boxes.Select(b => b.Text ?? ""));
        set
        {
            var d = new string((value ?? "").Where(char.IsDigit).Take(6).ToArray());
            for (int i = 0; i < 6; i++) _boxes[i].Text = i < d.Length ? d[i].ToString() : "";
            _boxes[Math.Min(d.Length, 5)].Focus();
            CodeChanged?.Invoke();
        }
    }
}
