using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Redline.App.TrayIcon;

/// <summary>
/// Light and dark looks for the tray menu, close to the Windows 11 context menu. WinForms menus are
/// always light by default; <see cref="Apply"/> picks the look from the Windows app mode each time
/// the menu opens, so it follows a change without a restart.
/// </summary>
internal sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    private static readonly TrayMenuRenderer LightRenderer = new(MenuColors.Light);
    private static readonly TrayMenuRenderer DarkRenderer = new(MenuColors.Dark);

    private readonly MenuColors _colors;

    private TrayMenuRenderer(MenuColors colors) : base(new MenuColorTable(colors))
    {
        _colors = colors;
        RoundedEdges = false;
    }

    public static void Apply(Forms.ContextMenuStrip menu, bool dark)
    {
        var renderer = dark ? DarkRenderer : LightRenderer;
        if (!ReferenceEquals(menu.Renderer, renderer))
        {
            menu.Renderer = renderer;
            menu.BackColor = renderer._colors.Background;
        }
    }

    /// <summary>Windows 11 rounds the menu's corners (no-op on Windows 10). Needs the dropdown's handle.</summary>
    public static void RoundCorners(Forms.ToolStripDropDown menu)
    {
        int preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _colors.Text : _colors.DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = RoundedRect(bounds, 4);
        using var brush = new SolidBrush(_colors.Hover);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(_colors.Separator);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        // The stock check is a black glyph on a light box, invisible on a dark menu: draw a plain tick.
        var r = e.ImageRectangle;
        using var pen = new Pen(_colors.Text, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new PointF[]
        {
            new(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
        });
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_colors.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed record MenuColors(Color Background, Color Text, Color DisabledText, Color Hover, Color Separator, Color Border)
    {
        public static readonly MenuColors Light = new(
            Background: Color.FromArgb(0xF9, 0xF9, 0xF9), Text: Color.FromArgb(0x1A, 0x1A, 0x1A),
            DisabledText: Color.FromArgb(0x9E, 0x9E, 0x9E), Hover: Color.FromArgb(0xEA, 0xEA, 0xEA),
            Separator: Color.FromArgb(0xE0, 0xE0, 0xE0), Border: Color.FromArgb(0xD0, 0xD0, 0xD0));

        public static readonly MenuColors Dark = new(
            Background: Color.FromArgb(0x2C, 0x2C, 0x2C), Text: Color.FromArgb(0xF2, 0xF2, 0xF2),
            DisabledText: Color.FromArgb(0x80, 0x80, 0x80), Hover: Color.FromArgb(0x3D, 0x3D, 0x3D),
            Separator: Color.FromArgb(0x45, 0x45, 0x45), Border: Color.FromArgb(0x48, 0x48, 0x48));
    }

    /// <summary>Flat colors for everything the professional renderer still paints itself (image margin, check box).</summary>
    private sealed class MenuColorTable(MenuColors c) : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => c.Background;
        public override Color ImageMarginGradientBegin => c.Background;
        public override Color ImageMarginGradientMiddle => c.Background;
        public override Color ImageMarginGradientEnd => c.Background;
        public override Color MenuBorder => c.Border;
        public override Color MenuItemBorder => c.Hover;
        public override Color MenuItemSelected => c.Hover;
        public override Color SeparatorDark => c.Separator;
        public override Color SeparatorLight => c.Separator;
        public override Color CheckBackground => c.Background;
        public override Color CheckSelectedBackground => c.Hover;
        public override Color CheckPressedBackground => c.Hover;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
