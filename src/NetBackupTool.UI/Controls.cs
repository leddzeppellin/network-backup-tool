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

/// <summary>Seis caixas para o código: avança sozinho, volta com Backspace e aceita colar o código inteiro.</summary>
public sealed class CodeBox : StackPanel
{
    private readonly TextBox[] _boxes = new TextBox[6];

    public event Action? CodeChanged;

    public CodeBox()
    {
        Orientation = Orientation.Horizontal;
        Classes.Add("codebox");
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
