using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Nbt.Core;
using NetBackupTool.Agent;

namespace NetBackupTool;

/// <summary>Aba Atividade: quem criou, alterou, renomeou ou apagou cada arquivo da pasta da empresa.</summary>
public partial class ServerView
{
    private const int ShowMax = 500;
    private List<ActivityEntry> _activity = new();

    private async Task LoadActivity()
    {
        if (!HasServer) return;
        var days = ActivityPeriod.SelectedIndex switch { 1 => 30, 2 => 365, _ => 7 };
        ActivityRefresh.IsEnabled = false;
        var r = await AgentClient.SendAsync(new AgentRequest("activity", 0, days.ToString()), 5000);
        ActivityRefresh.IsEnabled = true;
        _activity = r.Ok && r.Data != null
            ? System.Text.Json.JsonSerializer.Deserialize<List<ActivityEntry>>(r.Data) ?? new()
            : new();
        if (!r.Ok) ProblemText.Text = IsRemote ? L.T("rem.lost", r.Error ?? "?") : L.T("srv.noService");
        ProblemText.IsVisible = !r.Ok;
        RenderActivity();
    }

    private static string ActionText(string action) => action switch
    {
        "created" => L.T("act.created"),
        "changed" => L.T("act.changed"),
        "deleted" => L.T("act.deleted"),
        "renamed" => L.T("act.renamed"),
        _ => action,
    };

    private string PersonName(string login)
    {
        if (login.Length == 0) return L.T("act.someone");
        var u = _saved.Users.FirstOrDefault(x => x.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        return u is { FullName.Length: > 0 } ? u.FullName : login;
    }

    private void RenderActivity()
    {
        ActivityList.Children.Clear();
        var q = (ActivitySearch.Text ?? "").Trim();
        var rows = _activity.Where(e => q.Length == 0 ||
                e.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                (e.OldPath?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                e.User.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                PersonName(e.User).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ActivityEmpty.IsVisible = rows.Count == 0;
        ActivityEmpty.Text = !_draft.Activity ? L.T("act.off") : _activity.Count == 0 ? L.T("act.empty") : L.T("act.noMatch");
        ActivityMore.IsVisible = rows.Count > ShowMax;
        ActivityMore.Text = L.T("act.more", ShowMax, rows.Count);

        string? lastDay = null;
        foreach (var e in rows.Take(ShowMax))
        {
            // Separador por dia: "Hoje", "Ontem" ou a data.
            var day = e.Time.Date == DateTime.Today ? L.T("act.today")
                : e.Time.Date == DateTime.Today.AddDays(-1) ? L.T("act.yesterday")
                : e.Time.ToString("D");
            if (day != lastDay)
            {
                ActivityList.Children.Add(new TextBlock { Text = day, Classes = { "caption" }, Margin = new(0, lastDay == null ? 0 : 10, 0, 2) });
                lastDay = day;
            }

            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines!.Add(new Run(PersonName(e.User)) { FontWeight = FontWeight.SemiBold });
            text.Inlines.Add(new Run($" {ActionText(e.Action)} "));
            if (e.Action == "renamed" && e.OldPath != null)
            {
                text.Inlines.Add(new Run(e.OldPath) { Foreground = Brushes.Gray });
                text.Inlines.Add(new Run(" → "));
            }
            text.Inlines.Add(new Run(e.Path));

            var row = new DockPanel();
            var time = new TextBlock { Text = e.Time.ToString("HH:mm"), Classes = { "caption" }, Width = 48, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
            row.Children.Add(text);
            ActivityList.Children.Add(row);
        }
    }
}
