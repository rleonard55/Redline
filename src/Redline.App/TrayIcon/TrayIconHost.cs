using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Redline.App.TrayIcon;

/// <summary>System tray presence: the app has no main window.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;

    /// <param name="togglePause">Asked to flip the paused state; the app answers via <see cref="SetPaused"/>.</param>
    public TrayIconHost(Action showSettings, Action showDiagnostics, Action togglePause, Action exit)
    {
        _activeIcon = CreateIcon(Color.FromArgb(0xD1, 0x24, 0x24));
        _pausedIcon = CreateIcon(Color.Gray);

        _pauseItem = new Forms.ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) => togglePause();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => showSettings());
        menu.Items.Add("Diagnostics", null, (_, _) => showDiagnostics());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _activeIcon,
            Text = "Redline",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => showDiagnostics();
    }

    /// <summary>Draws a squiggle on a rounded tile so no .ico asset is needed yet (Phase 5 adds branding).</summary>
    private static Icon CreateIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.White);
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(color, 4f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new PointF[] { new(5, 18), new(10, 12), new(16, 20), new(22, 12), new(27, 18) });
        }

        // Icon.FromHandle doesn't own the HICON; clone so we can destroy the original immediately.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>Reflects the paused state (from settings) in the menu check and the icon.</summary>
    public void SetPaused(bool paused)
    {
        _pauseItem.Checked = paused;
        _icon.Icon = paused ? _pausedIcon : _activeIcon;
        _icon.Text = paused ? "Redline (paused)" : _activeText;
    }

    /// <summary>Shows the suggestion hotkey in the tray tooltip (max 63 chars).</summary>
    public void SetHotkeyHint(string? hotkey)
    {
        _activeText = hotkey is null ? "Redline" : $"Redline — {hotkey} for suggestions";
        if (!_pauseItem.Checked) _icon.Text = _activeText;
    }

    private string _activeText = "Redline";

    /// <summary>Shows a transient tray notification.</summary>
    public void Notify(string message, bool isError = false) =>
        _icon.ShowBalloonTip(4000, "Redline", message, isError ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
