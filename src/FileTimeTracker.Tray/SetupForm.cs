using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace FileTimeTracker.Tray;

/// <summary>
/// Shown once, on first run. Asks for a name and confirms where the report goes — everything else
/// is detected or defaulted, so there is nothing to configure.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetupForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new();
    private readonly Label _hint = new();

    public SetupForm(string suggestedName, string? suggestedFolder)
    {
        SuspendLayout();

        // 96-DPI design units; see the matching note in MainForm.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Set up File-Time Tracker";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 250);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        Icon = SystemIcons.Application;

        var heading = new Label
        {
            Text = "One-time setup",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16),
        };

        var blurb = new Label
        {
            Text = "Your spreadsheet is labelled with this name and filed in a folder of its own.",
            ForeColor = Color.FromArgb(90, 90, 90),
            Location = new Point(16, 46),
            Size = new Size(428, 18),
        };

        var nameCaption = Label("YOUR NAME", 16, 78);
        _name.Text = suggestedName;
        _name.Location = new Point(16, 94);
        _name.Size = new Size(428, 23);

        var folderCaption = Label("SAVE SPREADSHEETS TO", 16, 126);
        _folder.Text = suggestedFolder ?? string.Empty;
        _folder.Location = new Point(16, 142);
        _folder.Size = new Size(342, 23);

        var browse = new Button
        {
            Text = "Browse…",
            Location = new Point(366, 141),
            Size = new Size(78, 25),
        };
        browse.Click += (_, _) => Browse();

        _hint.Location = new Point(16, 172);
        _hint.Size = new Size(428, 30);
        _hint.ForeColor = suggestedFolder is null ? Color.FromArgb(170, 60, 0) : Color.FromArgb(0, 110, 50);
        _hint.Text = suggestedFolder is null
            ? "Dropbox was not detected. Pick any folder — if it is inside Dropbox, spreadsheets sync automatically."
            : "Dropbox detected. Only spreadsheets go here, one folder per person. Nothing else is written to Dropbox.";

        var start = new Button
        {
            Text = "Start tracking",
            Location = new Point(286, 208),
            Size = new Size(158, 30),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 110, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
        };
        start.FlatAppearance.BorderSize = 0;
        start.Click += OnStart;

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(196, 208),
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange(new Control[]
        {
            heading, blurb, nameCaption, _name, folderCaption, _folder, browse, _hint, start, cancel,
        });

        AcceptButton = start;
        CancelButton = cancel;

        ResumeLayout(performLayout: true);
    }

    public string DisplayName => _name.Text.Trim();

    /// <summary>The folder finished spreadsheets are filed in, one subfolder per person.</summary>
    public string PublishRoot => _folder.Text.Trim();

    private static Label Label(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(110, 110, 110),
        Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
        AutoSize = true,
        Location = new Point(x, y),
    };

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where should spreadsheets be saved?",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (Directory.Exists(_folder.Text))
        {
            dialog.SelectedPath = _folder.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dialog.SelectedPath;
        }
    }

    private void OnStart(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            Warn("Please enter a name so the report can be labelled.");
            _name.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(PublishRoot))
        {
            Warn("Please choose a folder for the spreadsheets.");
            _folder.Focus();
            return;
        }

        // Create it here rather than at first upload: a path that cannot be created is a setup
        // problem, and saying so now is far better than failing when the user clicks Stop.
        try
        {
            Directory.CreateDirectory(PublishRoot);
        }
        catch (Exception ex)
        {
            Warn($"That folder cannot be used:\r\n\r\n{ex.Message}");
            DialogResult = DialogResult.None;
            _folder.Focus();
            return;
        }
    }

    private void Warn(string message)
    {
        DialogResult = DialogResult.None;
        MessageBox.Show(this, message, "Set up File-Time Tracker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
