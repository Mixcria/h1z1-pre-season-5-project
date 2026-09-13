using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Cranberry.Launcher;

internal static class LauncherTheme
{
    internal static readonly Color Background = Color.FromArgb(21, 21, 21);
    internal static readonly Color Surface = Color.FromArgb(29, 29, 29);
    internal static readonly Color Field = Color.FromArgb(37, 37, 37);
    internal static readonly Color Border = Color.FromArgb(57, 57, 55);
    internal static readonly Color Text = Color.FromArgb(239, 237, 231);
    internal static readonly Color Muted = Color.FromArgb(164, 164, 158);
    internal static readonly Color Accent = Color.FromArgb(196, 48, 36);
    internal static readonly Color Online = Color.FromArgb(148, 173, 113);
    private static readonly PrivateFontCollection Fonts = new();
    private static readonly IntPtr FontData;
    internal static readonly Image Artwork;

    static LauncherTheme()
    {
        using var artwork = Resource("kotk-keyart.jpg");
        Artwork = new Bitmap(artwork);
        using var font = Resource("Oswald-Regular.ttf");
        byte[] bytes = new byte[checked((int)font.Length)];
        font.ReadExactly(bytes);
        // Keep the backing memory alive for the lifetime of the process, for GDI and GDI+.
        FontData = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, FontData, bytes.Length);
        Fonts.AddMemoryFont(FontData, bytes.Length);
        uint count = 0;
        AddFontMemResourceEx(FontData, (uint)bytes.Length, IntPtr.Zero, ref count);
    }

    internal static Stream Resource(string name) => typeof(LauncherTheme).Assembly
        .GetManifestResourceStream("Cranberry.Launcher.Assets." + name)
        ?? typeof(LauncherTheme).Assembly.GetManifestResourceStream("Cranberry.Launcher.Notices." + name)
        ?? throw new InvalidDataException("Missing launcher resource: " + name);
    internal static Font Heading(float size) => new(Fonts.Families[0], size, FontStyle.Regular);
    internal static Font Body(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    internal static Icon Icon() { using var stream = Resource("launcher.ico"); return new Icon(stream); }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint fonts);
}

internal sealed class LauncherButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Navigation { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Quiet { get; set; }
    private bool _hover, _pressed;

    internal LauncherButton(string text)
    {
        Text = text; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = LauncherTheme.Surface; ForeColor = LauncherTheme.Text;
        Font = LauncherTheme.Body(9); Cursor = Cursors.Hand;
        Size = new(112, 36); Margin = new(0, 0, 8, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = Primary ? LauncherTheme.Accent : Quiet || Navigation ? Parent?.BackColor ?? BackColor : LauncherTheme.Field;
        if (!Enabled) background = LauncherTheme.Surface;
        else if (_pressed) background = Primary ? Color.FromArgb(157, 35, 27) : Color.FromArgb(48, 48, 46);
        else if (_hover) background = Primary ? Color.FromArgb(220, 57, 42) : Color.FromArgb(43, 43, 41);
        e.Graphics.Clear(background);
        if (!Quiet && !Navigation)
        {
            using var pen = new Pen(Primary && Enabled ? Color.FromArgb(226, 70, 55) : LauncherTheme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        if (Navigation)
        {
            using var rule = new Pen(LauncherTheme.Border);
            e.Graphics.DrawLine(rule, 0, Height - 1, Width, Height - 1);
        }
        if (Navigation && Selected)
        {
            using var brush = new SolidBrush(LauncherTheme.Accent);
            int inset = (int)(14 * DeviceDpi / 96f);
            e.Graphics.FillRectangle(brush, inset, Height - 3, Width - inset * 2, 3);
        }
        var color = !Enabled ? Color.FromArgb(111, 111, 106) : Navigation && !Selected && !_hover ? LauncherTheme.Muted : ForeColor;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), LauncherTheme.Text, background);
    }
}

internal sealed class ArtworkPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool SideCrop { get; init; }
    internal ArtworkPanel()
    {
        DoubleBuffered = true; BackColor = LauncherTheme.Background;
        AccessibleName = "King of the Kill crown and sunset artwork";
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (Width < 1 || Height < 1) return;
        var art = LauncherTheme.Artwork;
        var source = SideCrop ? new RectangleF(art.Width * .43f, 0, art.Width * .57f, art.Height)
            : new RectangleF(0, 0, art.Width, art.Height);
        float scale = Math.Max(Width / source.Width, Height / source.Height);
        float width = source.Width * scale, height = source.Height * scale;
        float x = (Width - width) / 2;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Bias toward the top so the raised crown is retained in wider windows.
        e.Graphics.DrawImage(art, new RectangleF(x, (Height - height) * .16f, width, height), source, GraphicsUnit.Pixel);
        int fadeHeight = Math.Min(Height, (int)(105 * DeviceDpi / 96f));
        var fadeArea = new Rectangle(0, Height - fadeHeight, Width, fadeHeight);
        using var fade = new LinearGradientBrush(fadeArea, Color.FromArgb(0, LauncherTheme.Background), LauncherTheme.Background, 90);
        fade.WrapMode = WrapMode.TileFlipXY;
        e.Graphics.FillRectangle(fade, fadeArea);
        if (SideCrop)
        {
            using var shade = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
            e.Graphics.FillRectangle(shade, ClientRectangle);
        }
    }
}

internal sealed class LauncherProgress : Control
{
    private int _value;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }
    internal LauncherProgress() { DoubleBuffered = true; Height = 3; AccessibleName = "Download progress"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(LauncherTheme.Border);
        using var fill = new SolidBrush(LauncherTheme.Accent);
        e.Graphics.FillRectangle(fill, 0, 0, Width * _value / 100f, Height);
    }
}

internal sealed class LauncherList : ListBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal Func<string, Image?>? AvatarProvider { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal string EmptyText { get; set; } = "Nothing here yet.";
    internal LauncherList()
    {
        Dock = DockStyle.Fill; BackColor = LauncherTheme.Surface; ForeColor = LauncherTheme.Text;
        BorderStyle = BorderStyle.None; IntegralHeight = false; DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 54; Font = LauncherTheme.Body(); Margin = Padding.Empty;
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e); ItemHeight = (int)(54 * DeviceDpi / 96f);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var item = (MainForm.Item)Items[e.Index];
        float scale = DeviceDpi / 96f;
        using var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(48, 48, 45) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        Image? avatar = AvatarProvider?.Invoke(item.Id);
        if (avatar is not null) e.Graphics.DrawImage(avatar, new Rectangle(e.Bounds.X + (int)(8 * scale), e.Bounds.Y + (int)(8 * scale), (int)(36 * scale), (int)(36 * scale)));
        using var dot = new SolidBrush(item.Online ? LauncherTheme.Online : Color.FromArgb(104, 104, 99));
        e.Graphics.FillEllipse(dot, e.Bounds.X + 14 * scale, e.Bounds.Y + 16 * scale, 5 * scale, 5 * scale);
        var title = new Rectangle(e.Bounds.X + (int)((avatar is null ? 30 : 54) * scale), e.Bounds.Y + (int)(6 * scale), e.Bounds.Width - (int)((avatar is null ? 42 : 66) * scale), (int)(23 * scale));
        TextRenderer.DrawText(e.Graphics, item.Text, Font, title, ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var detailFont = LauncherTheme.Body(8.5f);
        title.Y += (int)(23 * scale); title.Height = (int)(19 * scale);
        TextRenderer.DrawText(e.Graphics, item.Detail, detailFont, title, LauncherTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is not (0x000F or 0x0317 or 0x0318) || Items.Count != 0) return;
        using var graphics = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam);
        var bounds = Rectangle.Inflate(ClientRectangle, -18, -18);
        TextRenderer.DrawText(graphics, EmptyText, Font, bounds, LauncherTheme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
