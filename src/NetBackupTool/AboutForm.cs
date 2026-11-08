using Nbt.Core;

namespace NetBackupTool;

public sealed class AboutForm : Form
{
    public AboutForm(Icon icon)
    {
        Text = L.T("menu.about");
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        var pic = new PictureBox
        {
            Image = new Icon(icon, 64, 64).ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 16, 0),
        };
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        text.Controls.Add(new Label { Text = "Network Backup Tool", AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold) });
        text.Controls.Add(new Label { Text = L.T("about.text", Updater.Current.ToString(3)), AutoSize = true, MaximumSize = new Size(380, 0) });
        var link = new LinkLabel { Text = Updater.ProjectUrl.Replace("https://", ""), AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
        link.LinkClicked += (_, _) => Updater.OpenUrl(Updater.ProjectUrl);
        text.Controls.Add(link);
        var ok = new Button { Text = L.T("about.ok"), AutoSize = true, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right, Margin = new Padding(0, 16, 0, 0) };

        grid.Controls.Add(pic, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(ok, 1, 1);
        Controls.Add(grid);
        AcceptButton = CancelButton = ok;
    }
}
