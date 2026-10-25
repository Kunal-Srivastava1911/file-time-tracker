using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Tray;

/// <summary>
/// The whole user interface: a status panel and one Stop button that ends tracking and writes the
/// workbook to Dropbox.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    /// <summary>How many closed intervals to keep on screen. Enough to see it working, not a log.</summary>
    private const int RecentLimit = 8;

    private readonly TrackerSession _session;
    private readonly TrackerLog _log;

    private readonly Label _state = new();
    private readonly Label _elapsed = new();
    private readonly Label _captured = new();
    private readonly Label _current = new();
    private readonly ListBox _recent = new();
    private readonly Button _stop = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _ticker = new();

    private bool _stopping;
    private bool _finished;

    public MainForm(TrackerSession session, TrackerLog log)
    {
        _session = session;
        _log = log;

        BuildUi();

        _session.IntervalClosed += OnIntervalClosed;

        _ticker.Interval = 1000;
        _ticker.Tick += (_, _) => RefreshStatus();
        _ticker.Start();
    }

    private void BuildUi()
    {
        SuspendLayout();

        // Every coordinate below is in 96-DPI units; this is what makes Windows scale them up on a
        // 125%/150%/4K display instead of shrinking the form and clipping the Stop button.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "File-Time Tracker";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 330);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        Icon = SystemIcons.Application;

        _state.Text = "●  Live tracking enabled";
        _state.ForeColor = Color.FromArgb(0, 128, 55);
        _state.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _state.AutoSize = true;
        _state.Location = new Point(16, 14);

        var elapsedCaption = Caption("Elapsed", 16, 54);
        _elapsed.Text = "00:00:00";
        _elapsed.Font = new Font("Consolas", 14F);
        _elapsed.AutoSize = true;
        _elapsed.Location = new Point(16, 70);

        var capturedCaption = Caption("Captured", 180, 54);
        _captured.Text = "0 intervals";
        _captured.Font = new Font("Segoe UI", 11F);
        _captured.AutoSize = true;
        _captured.Location = new Point(180, 72);

        var currentCaption = Caption("In focus now", 16, 108);
        _current.Text = "—";
        _current.AutoEllipsis = true;
        _current.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _current.Location = new Point(16, 124);
        _current.Size = new Size(398, 20);

        var recentCaption = Caption("Captured so far", 16, 152);
        _recent.Location = new Point(16, 168);
        _recent.Size = new Size(398, 94);
        _recent.Font = new Font("Consolas", 8.5F);
        _recent.BorderStyle = BorderStyle.FixedSingle;
        _recent.IntegralHeight = false;
        // Display only: selecting a row would imply an action that does not exist.
        _recent.SelectionMode = SelectionMode.None;

        _stop.Text = "Stop && Save to Dropbox";
        _stop.Location = new Point(16, 276);
        _stop.Size = new Size(398, 38);
        _stop.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _stop.BackColor = Color.FromArgb(196, 43, 28);
        _stop.ForeColor = Color.White;
        _stop.FlatStyle = FlatStyle.Flat;
        _stop.FlatAppearance.BorderSize = 0;
        _stop.Cursor = Cursors.Hand;
        _stop.Click += async (_, _) => await StopAsync().ConfigureAwait(true);

        Controls.AddRange(new Control[]
        {
            _state, elapsedCaption, _elapsed, capturedCaption, _captured,
            currentCaption, _current, recentCaption, _recent, _stop,
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => Restore());
        menu.Items.Add("Stop && save to Dropbox", null, async (_, _) => await StopAsync().ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Discard and exit", null, (_, _) => DiscardAndExit());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "File-Time Tracker — tracking";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Restore();

        Shown += (_, _) => _tray.ShowBalloonTip(
            3000, "File-Time Tracker", "Live tracking is enabled.", ToolTipIcon.Info);

        ResumeLayout(performLayout: true);
    }

    private static Label Caption(string text, int x, int y) => new()
    {
        Text = text.ToUpperInvariant(),
        ForeColor = Color.FromArgb(110, 110, 110),
        Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
        AutoSize = true,
        Location = new Point(x, y),
    };

    private void RefreshStatus()
    {
        if (_stopping)
        {
            return;
        }

        var elapsed = _session.Elapsed;
        _elapsed.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        var count = _session.ClosedIntervals;
        _captured.Text = count == 1 ? "1 interval" : $"{count} intervals";

        // Null means nothing is being counted: locked, no foreground window, or excluded (§3).
        _current.Text = _session.CurrentDocument ?? "— nothing counted right now";
    }

    /// <summary>Raised on the sampling thread, so marshal before touching controls.</summary>
    private void OnIntervalClosed(IntervalRecord record)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => AddRecent(record));
        }
        catch (ObjectDisposedException)
        {
            // Closed between the guard and the marshal; nothing to show.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void AddRecent(IntervalRecord record)
    {
        var minutes = Math.Max(1, (int)Math.Round(record.TotalSec / 60.0));
        var file = record.File.Length > 34 ? record.File[..33] + "…" : record.File;

        _recent.Items.Insert(0, $"{record.StartUtc.ToLocalTime():HH:mm}  {file,-34} {minutes,3}m");

        while (_recent.Items.Count > RecentLimit)
        {
            _recent.Items.RemoveAt(_recent.Items.Count - 1);
        }
    }

    private async Task StopAsync()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _ticker.Stop();

        _stop.Enabled = false;
        _stop.Text = "Saving to Dropbox…";
        _stop.BackColor = Color.FromArgb(120, 120, 120);
        _state.Text = "●  Stopping";
        _state.ForeColor = Color.FromArgb(150, 110, 0);
        _current.Text = "Flushing the last interval and building the workbook…";
        Cursor = Cursors.WaitCursor;

        var result = await _session.StopAndReportAsync().ConfigureAwait(true);

        Cursor = Cursors.Default;
        _finished = true;
        _tray.Visible = false;

        if (result.Succeeded)
        {
            // Nothing on screen: stopping is meant to be the end of it, and the spreadsheet is in
            // Dropbox whether or not anyone is told so. The log keeps the detail for the one time
            // somebody needs to know where a session went.
            _log.Info(
                $"Session saved silently to '{result.SessionLocation}' " +
                $"({result.Intervals} capture(s), {result.RangeText}).");
        }
        else
        {
            MessageBox.Show(
                this,
                $"Tracking stopped, and nothing was lost — your intervals are still buffered locally and " +
                $"will be included next time.\r\n\r\nThe workbook could not be written:\r\n{result.Error}",
                "File-Time Tracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        Close();
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void DiscardAndExit()
    {
        var answer = MessageBox.Show(
            this,
            "Stop tracking without writing the report?\r\n\r\n" +
            "What has been captured stays in the local buffer and will be included the next time " +
            "you stop normally. Nothing is deleted.",
            "File-Time Tracker",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _finished = true;
        _tray.Visible = false;
        Close();
    }

    /// <summary>
    /// Closing the window hides it rather than ending the session. Stopping is a decision the user
    /// makes with the Stop button, not a side effect of tidying their desktop.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished && !_stopping && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();

            _tray.ShowBalloonTip(
                3000,
                "Still tracking",
                "File-Time Tracker is running in the notification area. Use Stop to finish and save.",
                ToolTipIcon.Info);

            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.IntervalClosed -= OnIntervalClosed;
            _ticker.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
