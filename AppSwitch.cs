// AppSwitch - global hotkey -> bring a specific app to the foreground instantly.
// Lives only in the system tray (no taskbar button). Optionally hides a second app while switching.
// Build: build.bat   Config: %APPDATA%\AppSwitch\keybinds.ini (edited via the GUI)
// Note: compiled with the .NET Framework csc (C# 5) -> no string interpolation, no ?. etc.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string idList);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public const int WM_HOTKEY = 0x0312, WM_CLOSE = 0x0010;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int SW_HIDE = 0, SW_RESTORE = 9, SW_SHOW = 5, SW_MINIMIZE = 6;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    public const uint GW_OWNER = 4;

    public static bool WinDown()
    {
        return (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
    }
}

class Binding
{
    public uint Mods, Vk;
    public string ExeName;     // e.g. Discord.exe
    public string LaunchPath;  // optional: start this if no window found
    public string HideExe;     // optional: hide this app's windows while switching
    public bool CloseCurrent;  // optional: close the window that was in front when switching
    public bool Registered;

    public string HotkeyText { get { return Hotkeys.Format(Mods, Vk); } }
}

static class Hotkeys
{
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        if (vk != 0) parts.Add(((Keys)vk).ToString());
        return string.Join("+", parts);
    }

    public static bool Parse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var p in text.Split('+'))
        {
            var t = p.Trim();
            switch (t.ToLowerInvariant())
            {
                case "ctrl": case "control": case "strg": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                default:
                    if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
                    Keys k;
                    if (!Enum.TryParse(t, true, out k)) return false;
                    vk = (uint)k; break;
            }
        }
        return vk != 0;
    }

    // Display label for one key name (config keeps the Keys enum names).
    public static string Label(string part)
    {
        if (part.Length == 2 && part[0] == 'D' && char.IsDigit(part[1])) return part.Substring(1);
        switch (part)
        {
            case "Return": return "Enter";
            case "Next": return "PgDn";
            case "Prior": return "PgUp";
            case "Capital": return "Caps";
            case "Back": return "Backspace";
            case "Oemcomma": return ",";
            case "OemPeriod": return ".";
            case "OemMinus": return "-";
            case "Oemplus": return "+";
            default: return part;
        }
    }
}

class WinInfo
{
    public IntPtr Handle; public uint Pid; public string Title, ExeName, ExePath;
}

static class WinApi
{
    public static string ProcessPathOf(uint pid)
    {
        IntPtr h = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    public static bool IsAppWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return false;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    // All visible app windows, in Z-order (most recently used first).
    public static List<WinInfo> List()
    {
        var result = new List<WinInfo>();
        var cache = new Dictionary<uint, string>();
        Native.EnumWindows((h, l) =>
        {
            if (!IsAppWindow(h)) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            string path;
            if (!cache.TryGetValue(pid, out path)) { path = ProcessPathOf(pid); cache[pid] = path; }
            if (path == null) return true;
            var sb = new StringBuilder(512); Native.GetWindowText(h, sb, sb.Capacity);
            result.Add(new WinInfo { Handle = h, Pid = pid, Title = sb.ToString(), ExePath = path, ExeName = Path.GetFileName(path) });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindFor(string exeName)
    {
        foreach (var w in List())
            if (w.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return w.Handle;
        return IntPtr.Zero;
    }

    // Beats Windows' foreground lock, also when a fullscreen game has focus.
    public static void ForceForeground(IntPtr wnd)
    {
        if (Native.IsIconic(wnd)) Native.ShowWindow(wnd, Native.SW_RESTORE);
        else Native.ShowWindow(wnd, Native.SW_SHOW);

        IntPtr fg = Native.GetForegroundWindow();
        uint dummy;
        uint fgThread = Native.GetWindowThreadProcessId(fg, out dummy);
        uint myThread = Native.GetCurrentThreadId();

        // fake Alt tap: makes Windows allow SetForegroundWindow
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);

        bool attached = fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(myThread, fgThread, true);
        Native.BringWindowToTop(wnd);
        bool ok = Native.SetForegroundWindow(wnd);
        if (attached) Native.AttachThreadInput(myThread, fgThread, false);

        if (!ok || Native.GetForegroundWindow() != wnd) Native.SwitchToThisWindow(wnd, true);
    }

    // "path\app.exe --args" or "\"path\" args" -> file + args
    public static void SplitCommand(string cmd, out string file, out string args)
    {
        file = cmd; args = "";
        if (cmd.StartsWith("\""))
        {
            int q = cmd.IndexOf('"', 1);
            if (q > 0) { file = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
            return;
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) { file = cmd.Substring(0, exe + 4); args = cmd.Substring(exe + 4).Trim(); }
    }

    public static void Launch(string cmd)
    {
        try
        {
            string file, args; SplitCommand(cmd, out file, out args);
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Start fehlgeschlagen:\n" + cmd + "\n\n" + ex.Message, "AppSwitch"); }
    }
}

// Windows hidden via "Dabei verstecken". Persisted so a crash/kill never leaves apps invisible forever.
static class HiddenWindows
{
    static readonly Dictionary<string, List<IntPtr>> byExe = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
    static string file;

    public static void Init(string dir)
    {
        file = Path.Combine(dir, "hidden.txt");
        // restore leftovers from a previous run that was killed
        try
        {
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                long v;
                if (!long.TryParse(line.Trim(), out v)) continue;
                var h = new IntPtr(v);
                if (Native.IsWindow(h) && !Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_SHOW);
            }
            File.Delete(file);
        }
        catch { }
    }

    public static bool Any { get { return byExe.Count > 0; } }

    public static void Hide(string exe)
    {
        List<IntPtr> list;
        if (!byExe.TryGetValue(exe, out list)) { list = new List<IntPtr>(); }
        foreach (var w in WinApi.List())
        {
            if (!w.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
            Native.ShowWindow(w.Handle, Native.SW_HIDE);
            if (!list.Contains(w.Handle)) list.Add(w.Handle);
        }
        if (list.Count > 0) byExe[exe] = list;
        Save();
    }

    // Returns the first restored window (or zero).
    public static IntPtr Restore(string exe)
    {
        List<IntPtr> list;
        if (!byExe.TryGetValue(exe, out list)) return IntPtr.Zero;
        byExe.Remove(exe);
        IntPtr first = IntPtr.Zero;
        foreach (var h in list)
        {
            if (!Native.IsWindow(h)) continue;
            Native.ShowWindow(h, Native.SW_SHOW);
            if (first == IntPtr.Zero) first = h;
        }
        Save();
        return first;
    }

    public static void RestoreAll()
    {
        foreach (var exe in new List<string>(byExe.Keys)) Restore(exe);
    }

    static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var list in byExe.Values)
                foreach (var h in list) sb.AppendLine(h.ToInt64().ToString());
            if (sb.Length == 0) { if (File.Exists(file)) File.Delete(file); }
            else File.WriteAllText(file, sb.ToString());
        }
        catch { }
    }
}

// ───────────────────────────── Theme ─────────────────────────────

static class Ui
{
    public static float Scale = 1f;
    public static int S(int px) { return (int)Math.Round(px * Scale); }

    public static readonly Color Bg = Color.FromArgb(22, 22, 27);
    public static readonly Color Surface = Color.FromArgb(32, 32, 39);
    public static readonly Color SurfaceHover = Color.FromArgb(41, 41, 50);
    public static readonly Color InputBg = Color.FromArgb(30, 30, 37);
    public static readonly Color Border = Color.FromArgb(52, 52, 63);
    public static readonly Color Fg = Color.FromArgb(236, 236, 242);
    public static readonly Color Muted = Color.FromArgb(146, 146, 162);
    public static readonly Color Accent = Color.FromArgb(110, 140, 255);
    public static readonly Color AccentHover = Color.FromArgb(132, 158, 255);
    public static readonly Color AccentDown = Color.FromArgb(92, 120, 230);
    public static readonly Color Danger = Color.FromArgb(255, 110, 110);
    public static readonly Color Key = Color.FromArgb(50, 50, 62);
    public static readonly Color KeyBorder = Color.FromArgb(74, 74, 90);
    public static readonly Color KeyShadow = Color.FromArgb(14, 14, 18);

    public static Font Body, Small, Semibold, Title, Heading, KeyFont, Icons, IconsBig;

    public const string GlyphAdd = "", GlyphEdit = "", GlyphDelete = "", GlyphKeyboard = "",
                        GlyphPower = "", GlyphFolder = "", GlyphApps = "", GlyphHide = "";

    public static void Init()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        Body = new Font("Segoe UI", 9.75f);
        Small = new Font("Segoe UI", 8.75f);
        Semibold = new Font("Segoe UI Semibold", 9.75f);
        Title = new Font("Segoe UI Semibold", 15f);
        Heading = new Font("Segoe UI Semibold", 13f);
        KeyFont = new Font("Segoe UI Semibold", 9f);
        string iconFace = "Segoe Fluent Icons";
        using (var test = new Font(iconFace, 10f)) if (test.Name != iconFace) iconFace = "Segoe MDL2 Assets";
        Icons = new Font(iconFace, 10f);
        IconsBig = new Font(iconFace, 30f);
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Rectangle r, int rad, Color c)
    {
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, Rectangle r, int rad, Color c, float width)
    {
        float o = width / 2f;
        using (var p = Round(new RectangleF(r.X + o, r.Y + o, r.Width - width, r.Height - width), rad))
        using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra)
    {
        TextRenderer.DrawText(g, s, f, r, c, Line | extra);
    }

    public static int Measure(IDeviceContext g, string s, Font f)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (g == null) return TextRenderer.MeasureText(s, f, Size.Empty, flags).Width;
        return TextRenderer.MeasureText(g, s, f, Size.Empty, flags).Width;
    }

    // Keycaps for "Ctrl+Alt+D". Returns total width; draws only if draw = true.
    public static int DrawKeys(Graphics g, string hotkey, int x, int cy, bool draw)
    {
        int h = S(26), gap = S(5), start = x;
        foreach (var raw in hotkey.Split('+'))
        {
            string t = Hotkeys.Label(raw);
            int w = Math.Max(h, Measure(g, t, KeyFont) + S(16));
            if (draw)
            {
                var r = new Rectangle(x, cy - h / 2, w, h);
                var shadow = r; shadow.Offset(0, S(2));
                Fill(g, shadow, S(6), KeyShadow);
                Fill(g, r, S(6), Key);
                Stroke(g, r, S(6), KeyBorder, 1f);
                Text(g, t, KeyFont, r, Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            x += w + gap;
        }
        return x - gap - start;
    }

    public static void DrawAppIcon(Graphics g, Bitmap icon, string name, Rectangle r)
    {
        if (icon != null) { g.DrawImage(icon, r); return; }
        Fill(g, r, S(7), Key);
        string letter = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
        Text(g, letter, Semibold, r, Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // Dark title bar + caption color (Windows 11), harmless elsewhere.
    public static void StyleWindow(IntPtr h)
    {
        try
        {
            int on = 1; Native.DwmSetWindowAttribute(h, 20, ref on, 4);
            int col = Bg.R | (Bg.G << 8) | (Bg.B << 16); Native.DwmSetWindowAttribute(h, 35, ref col, 4);
        }
        catch { }
    }

    public static void DarkScrollbars(Control c)
    {
        try { Native.SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
    }

    public static string NiceName(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "";
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
    }
}

static class IconCache
{
    static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

    public static Bitmap For(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        Bitmap bmp;
        if (cache.TryGetValue(path, out bmp)) return bmp;
        try { if (File.Exists(path)) using (var ic = Icon.ExtractAssociatedIcon(path)) bmp = ic.ToBitmap(); }
        catch { bmp = null; }
        cache[path] = bmp;
        return bmp;
    }
}

// ───────────────────────────── Controls ─────────────────────────────

class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Ui.Bg; }
}

enum ButtonKind { Primary, Secondary, Ghost }

class FlatButton : Control, IButtonControl
{
    public ButtonKind Kind; public string Glyph;
    bool hover, down;
    public DialogResult DialogResult { get; set; }

    public FlatButton(string text, ButtonKind kind, string glyph = null)
    {
        Text = text; Kind = kind; Glyph = glyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand; Font = Ui.Semibold; Height = Ui.S(36); TabStop = false;
    }

    public void FitWidth()
    {
        int w = Ui.Measure(null, Text, Font) + Ui.S(32);
        if (Glyph != null) w += Ui.S(24);
        Width = w;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var f = FindForm();
        if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        var r = new Rectangle(0, 0, Width, Height);
        Color fg;
        switch (Kind)
        {
            case ButtonKind.Primary:
                Ui.Fill(g, r, Ui.S(8), down ? Ui.AccentDown : hover ? Ui.AccentHover : Ui.Accent);
                fg = Color.White; break;
            case ButtonKind.Secondary:
                Ui.Fill(g, r, Ui.S(8), down ? Ui.Surface : hover ? Ui.SurfaceHover : Ui.Surface);
                Ui.Stroke(g, r, Ui.S(8), Ui.Border, 1f);
                fg = Ui.Fg; break;
            default:
                if (hover) Ui.Fill(g, r, Ui.S(8), Ui.Surface);
                fg = hover ? Ui.Fg : Ui.Muted; break;
        }
        int tw = Ui.Measure(g, Text, Font);
        int gw = Glyph != null ? Ui.S(24) : 0;
        int x = (Width - tw - gw) / 2;
        if (Glyph != null)
            Ui.Text(g, Glyph, Ui.Icons, new Rectangle(x, 0, Ui.S(16), Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        Ui.Text(g, Text, Font, new Rectangle(x + gw, 0, tw + 2, Height), fg, TextFormatFlags.VerticalCenter);
    }
}

class ToggleSwitch : Control
{
    bool on, hover;
    public event EventHandler CheckedChanged;

    public bool Checked
    {
        get { return on; }
        set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
    }

    public ToggleSwitch(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand; Font = Ui.Body;
        Size = new Size(Ui.S(52) + Ui.Measure(null, text, Font) + Ui.S(8), Ui.S(28));
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        int cy = Height / 2;
        var track = new Rectangle(0, cy - Ui.S(10), Ui.S(40), Ui.S(20));
        int knob = Ui.S(12);
        if (on)
        {
            Ui.Fill(g, track, Ui.S(10), hover ? Ui.AccentHover : Ui.Accent);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, track.Right - Ui.S(4) - knob, cy - knob / 2, knob, knob);
        }
        else
        {
            Ui.Fill(g, track, Ui.S(10), Ui.InputBg);
            Ui.Stroke(g, track, Ui.S(10), hover ? Ui.Muted : Ui.KeyBorder, 1f);
            using (var b = new SolidBrush(Ui.Muted))
                g.FillEllipse(b, track.X + Ui.S(4), cy - knob / 2, knob, knob);
        }
        Ui.Text(g, Text, Font, new Rectangle(Ui.S(52), 0, Width - Ui.S(52), Height), Ui.Fg, TextFormatFlags.VerticalCenter);
    }
}

// Rounded input frame around a borderless TextBox.
class Field : Panel
{
    readonly Control inner; bool focused;

    public Field(Control inner)
    {
        this.inner = inner; DoubleBuffered = true; ResizeRedraw = true; BackColor = Ui.InputBg;
        Controls.Add(inner);
        inner.Enter += (s, e) => { focused = true; Invalidate(); };
        inner.Leave += (s, e) => { focused = false; Invalidate(); };
        Click += (s, e) => inner.Focus();
        Cursor = Cursors.IBeam;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Ui.S(12);
        inner.SetBounds(pad, (Height - inner.Height) / 2, Width - pad * 2, inner.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        var r = new Rectangle(0, 0, Width, Height);
        Ui.Fill(g, r, Ui.S(8), Ui.InputBg);
        Ui.Stroke(g, r, Ui.S(8), focused ? Ui.Accent : Ui.Border, focused ? 1.5f : 1f);
    }

    public static TextBox MakeInput(string cue)
    {
        var t = new TextBox { BorderStyle = BorderStyle.None, BackColor = Ui.InputBg, ForeColor = Ui.Fg, Font = Ui.Body };
        t.HandleCreated += (s, e) => Native.SendMessage(t.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), cue);
        return t;
    }
}

// Records a key combination when focused.
class HotkeyBox : Control
{
    public uint Mods, Vk;
    uint pending;
    bool focused;

    public HotkeyBox()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = true; Cursor = Cursors.Hand; Font = Ui.Body;
    }

    // plain Escape still closes the dialog, everything else is recorded
    protected override bool IsInputKey(Keys k) { return (k & Keys.KeyCode) != Keys.Escape; }
    protected override bool ProcessCmdKey(ref Message msg, Keys k)
    {
        return (k & Keys.KeyCode) == Keys.Escape ? base.ProcessCmdKey(ref msg, k) : false;
    }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { focused = true; pending = 0; Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { focused = false; pending = 0; Invalidate(); base.OnLostFocus(e); }

    static uint ModsOf(KeyEventArgs e)
    {
        uint m = 0;
        if (e.Control) m |= Native.MOD_CONTROL;
        if (e.Alt) m |= Native.MOD_ALT;
        if (e.Shift) m |= Native.MOD_SHIFT;
        if (Native.WinDown()) m |= Native.MOD_WIN;
        return m;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true; e.SuppressKeyPress = true;
        var key = e.KeyCode;
        uint m = ModsOf(e);
        bool modifierOnly = key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey ||
                            key == Keys.LWin || key == Keys.RWin || key == Keys.LControlKey || key == Keys.RControlKey ||
                            key == Keys.LShiftKey || key == Keys.RShiftKey || key == Keys.LMenu || key == Keys.RMenu;
        if (modifierOnly) { pending = m; Invalidate(); return; }
        Mods = m; Vk = (uint)key; pending = 0;
        Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        pending = e.Modifiers == Keys.None && !Native.WinDown() ? 0 : ModsOf(e);
        if (Vk != 0) pending = 0;
        Invalidate();
    }

    public void Set(uint mods, uint vk) { Mods = mods; Vk = vk; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        var r = new Rectangle(0, 0, Width, Height);
        Ui.Fill(g, r, Ui.S(10), Ui.InputBg);
        Ui.Stroke(g, r, Ui.S(10), focused ? Ui.Accent : Ui.Border, focused ? 1.5f : 1f);
        int cy = Height / 2 - Ui.S(1), x = Ui.S(14);

        if (pending != 0 && focused)
        {
            int w = Ui.DrawKeys(g, Hotkeys.Format(pending, 0), x, cy, true);
            Ui.Text(g, "+  …", Ui.Semibold, new Rectangle(x + w + Ui.S(8), 0, Ui.S(60), Height), Ui.Muted, TextFormatFlags.VerticalCenter);
        }
        else if (Vk != 0)
        {
            Ui.DrawKeys(g, Hotkeys.Format(Mods, Vk), x, cy, true);
            if (focused)
                Ui.Text(g, "Neue Kombination drücken zum Ändern", Ui.Small, new Rectangle(0, 0, Width - Ui.S(14), Height), Ui.Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
        else
        {
            Ui.Text(g, Ui.GlyphKeyboard, Ui.Icons, new Rectangle(x, 0, Ui.S(18), Height), focused ? Ui.Accent : Ui.Muted, TextFormatFlags.VerticalCenter);
            Ui.Text(g, focused ? "Jetzt Tasten drücken…" : "Klicken und Tastenkombination drücken", Ui.Body,
                    new Rectangle(x + Ui.S(28), 0, Width - x - Ui.S(40), Height), focused ? Ui.Fg : Ui.Muted, TextFormatFlags.VerticalCenter);
        }
    }
}

class DarkMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Ui.Surface); }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using (var p = new Pen(Ui.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Ui.Prepare(e.Graphics);
        Ui.Fill(e.Graphics, new Rectangle(Ui.S(4), 1, e.Item.Width - Ui.S(8), e.Item.Height - 2), Ui.S(5), Ui.SurfaceHover);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Fg : Ui.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, Ui.S(8), y, e.Item.Width - Ui.S(8), y);
    }

    public static ContextMenuStrip Menu()
    {
        return new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Ui.Body, BackColor = Ui.Surface, ForeColor = Ui.Fg };
    }

    public static ToolStripMenuItem Item(string text, Image img, EventHandler click)
    {
        var it = new ToolStripMenuItem(text, img, click);
        it.Padding = new Padding(Ui.S(4), Ui.S(5), Ui.S(4), Ui.S(5));
        return it;
    }
}

// One hotkey as a card in the main list.
class BindingRow : Control
{
    public readonly Binding B;
    readonly Bitmap icon;
    public int KeyCol;
    bool hover, hoverDel, hoverEdit;
    Rectangle delRect, editRect;
    public event EventHandler EditRequested, DeleteRequested;

    public BindingRow(Binding b, Bitmap icon)
    {
        B = b; this.icon = icon;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = hoverDel = hoverEdit = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool d = delRect.Contains(e.Location), ed = editRect.Contains(e.Location);
        if (d != hoverDel || ed != hoverEdit) { hoverDel = d; hoverEdit = ed; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var h = delRect.Contains(e.Location) ? DeleteRequested : EditRequested;
        if (h != null) h(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
        var card = new Rectangle(0, 0, Width, Height);
        Ui.Fill(g, card, Ui.S(12), hover ? Ui.SurfaceHover : Ui.Surface);
        Ui.Stroke(g, card, Ui.S(12), Ui.Border, 1f);

        int cy = Height / 2, x = Ui.S(18);
        Ui.DrawKeys(g, B.HotkeyText, x, cy - Ui.S(1), true);

        int ax = x + KeyCol + Ui.S(24), isz = Ui.S(30);
        Ui.DrawAppIcon(g, icon, Ui.NiceName(B.ExeName), new Rectangle(ax, cy - isz / 2, isz, isz));
        int tx = ax + isz + Ui.S(12);
        int right = Width - Ui.S(14);

        int bs = Ui.S(32);
        delRect = new Rectangle(right - bs, cy - bs / 2, bs, bs);
        editRect = new Rectangle(delRect.X - bs - Ui.S(4), delRect.Y, bs, bs);
        if (hover)
        {
            if (hoverEdit) Ui.Fill(g, editRect, Ui.S(7), Ui.Border);
            if (hoverDel) Ui.Fill(g, delRect, Ui.S(7), Color.FromArgb(70, Ui.Danger));
            var c = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            Ui.Text(g, Ui.GlyphEdit, Ui.Icons, editRect, hoverEdit ? Ui.Fg : Ui.Muted, c);
            Ui.Text(g, Ui.GlyphDelete, Ui.Icons, delRect, hoverDel ? Ui.Danger : Ui.Muted, c);
            right = editRect.X - Ui.S(10);
        }

        if (!B.Registered)
        {
            const string t = "Belegt";
            int pw = Ui.Measure(g, t, Ui.Small) + Ui.S(20), ph = Ui.S(22);
            var pill = new Rectangle(right - pw, cy - ph / 2, pw, ph);
            Ui.Fill(g, pill, ph / 2, Color.FromArgb(48, Ui.Danger));
            Ui.Text(g, t, Ui.Small, pill, Ui.Danger, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            right = pill.X - Ui.S(10);
        }

        int tw = Math.Max(0, right - tx);
        Ui.Text(g, Ui.NiceName(B.ExeName), Ui.Semibold, new Rectangle(tx, cy - Ui.S(20), tw, Ui.S(20)), Ui.Fg, TextFormatFlags.Bottom);
        var subs = new List<string>();
        if (B.HideExe != null) subs.Add("Versteckt dabei " + Ui.NiceName(B.HideExe));
        if (B.CloseCurrent) subs.Add("Schließt aktuelles Fenster");
        if (subs.Count == 0) subs.Add(B.LaunchPath != null ? "Startet " + Path.GetFileName(B.LaunchPath.Trim('"')) : "Wechselt nur, wenn geöffnet");
        else if (B.LaunchPath != null) subs.Add("startet, falls zu");
        string sub = string.Join("  ·  ", subs);
        Ui.Text(g, sub, Ui.Small, new Rectangle(tx, cy + Ui.S(2), tw, Ui.S(18)), Ui.Muted, TextFormatFlags.Top);
    }
}

// ───────────────────────────── Edit dialog ─────────────────────────────

class EditDialog : Form
{
    readonly HotkeyBox hotkey = new HotkeyBox();
    readonly TextBox app = Field.MakeInput("z. B. Discord.exe");
    readonly TextBox launch = Field.MakeInput("Pfad zur .exe – leer = nur wechseln");
    readonly TextBox hide = Field.MakeInput("z. B. Spiel.exe – leer = nichts verstecken");
    readonly ToggleSwitch closeCurrent = new ToggleSwitch("Aktuelles Fenster beim Wechseln schließen");
    readonly Label error = new Label();
    readonly string heading;
    public Binding Result;

    public EditDialog(Binding existing)
    {
        heading = existing == null ? "Hotkey hinzufügen" : "Hotkey bearbeiten";
        Text = heading;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
        Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Fg;
        int S500 = Ui.S(520), pad = Ui.S(24), W = S500 - pad * 2, bw = Ui.S(150), fh = Ui.S(38), gap = Ui.S(8);
        ClientSize = new Size(S500, Ui.S(532));

        int y = Ui.S(70);
        AddLabel("Tastenkombination", null, pad, y); y += Ui.S(24);
        hotkey.SetBounds(pad, y, W, Ui.S(54)); Controls.Add(hotkey); y += Ui.S(54) + Ui.S(18);

        AddLabel("App, zu der gewechselt wird", null, pad, y); y += Ui.S(24);
        AddField(app, pad, y, W - bw - gap, fh);
        var pick = new FlatButton("Offene Apps", ButtonKind.Secondary, Ui.GlyphApps);
        pick.SetBounds(pad + W - bw, y, bw, fh); Controls.Add(pick);
        pick.Click += (s, e) => ShowPicker(pick, w => { app.Text = w.ExeName; launch.Text = w.ExePath; });
        y += fh + Ui.S(18);

        AddLabel("Starten, falls nicht geöffnet", "optional", pad, y); y += Ui.S(24);
        AddField(launch, pad, y, W - bw - gap, fh);
        var browse = new FlatButton("Durchsuchen", ButtonKind.Secondary, Ui.GlyphFolder);
        browse.SetBounds(pad + W - bw, y, bw, fh); Controls.Add(browse);
        browse.Click += (s, e) =>
        {
            using (var ofd = new OpenFileDialog { Filter = "Programme (*.exe)|*.exe" })
                if (ofd.ShowDialog(this) == DialogResult.OK) launch.Text = ofd.FileName;
        };
        y += fh + Ui.S(18);

        AddLabel("Dabei verstecken", "optional · verschwindet aus der Taskleiste, läuft weiter", pad, y); y += Ui.S(24);
        AddField(hide, pad, y, W - bw - gap, fh);
        var pickHide = new FlatButton("Offene Apps", ButtonKind.Secondary, Ui.GlyphApps);
        pickHide.SetBounds(pad + W - bw, y, bw, fh); Controls.Add(pickHide);
        pickHide.Click += (s, e) => ShowPicker(pickHide, w => hide.Text = w.ExeName);
        y += fh + Ui.S(12);

        closeCurrent.Location = new Point(pad, y); Controls.Add(closeCurrent);
        y += closeCurrent.Height + Ui.S(6);

        error.SetBounds(pad, y, W, Ui.S(22)); error.ForeColor = Ui.Danger; error.Font = Ui.Small; error.BackColor = Ui.Bg;
        Controls.Add(error);

        int by = ClientSize.Height - pad - fh, btn = Ui.S(120);
        var save = new FlatButton("Speichern", ButtonKind.Primary);
        save.SetBounds(S500 - pad - btn, by, btn, fh);
        var cancel = new FlatButton("Abbrechen", ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
        cancel.SetBounds(save.Left - gap - btn, by, btn, fh);
        save.Click += (s, e) => Save();
        Controls.Add(save); Controls.Add(cancel);
        AcceptButton = save; CancelButton = cancel;

        if (existing != null)
        {
            hotkey.Set(existing.Mods, existing.Vk);
            app.Text = existing.ExeName;
            launch.Text = existing.LaunchPath ?? "";
            hide.Text = existing.HideExe ?? "";
            closeCurrent.Checked = existing.CloseCurrent;
        }
        Shown += (s, e) => hotkey.Focus();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.StyleWindow(Handle); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; Ui.Prepare(g);
        Ui.Text(g, heading, Ui.Heading, new Rectangle(Ui.S(24), Ui.S(22), ClientSize.Width - Ui.S(48), Ui.S(30)), Ui.Fg, TextFormatFlags.VerticalCenter);
    }

    void AddLabel(string text, string hint, int x, int y)
    {
        var l = new Label { Text = text, Font = Ui.Semibold, ForeColor = Ui.Fg, BackColor = Ui.Bg, AutoSize = true, Location = new Point(x, y) };
        Controls.Add(l);
        if (hint != null)
            Controls.Add(new Label { Text = hint, Font = Ui.Small, ForeColor = Ui.Muted, BackColor = Ui.Bg, AutoSize = true,
                                     Location = new Point(x + l.PreferredWidth + Ui.S(6), y + Ui.S(2)) });
    }

    void AddField(TextBox box, int x, int y, int w, int h)
    {
        var f = new Field(box); f.SetBounds(x, y, w, h); Controls.Add(f);
    }

    void ShowPicker(Control anchor, Action<WinInfo> pick)
    {
        var menu = DarkMenuRenderer.Menu();
        menu.ImageScalingSize = new Size(Ui.S(20), Ui.S(20));
        uint me = (uint)Process.GetCurrentProcess().Id;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in WinApi.List())
        {
            if (w.Pid == me || !seen.Add(w.ExeName + "|" + w.Title)) continue;
            string title = w.Title.Length > 46 ? w.Title.Substring(0, 45) + "…" : w.Title;
            var icon = IconCache.For(w.ExePath);
            var ww = w;
            menu.Items.Add(DarkMenuRenderer.Item(Ui.NiceName(w.ExeName) + "   ·   " + title,
                icon != null ? new Bitmap(icon) : null, (s, e) => { pick(ww); error.Text = ""; }));
        }
        if (menu.Items.Count == 0)
        {
            var none = DarkMenuRenderer.Item("Keine offenen Fenster gefunden", null, null);
            none.Enabled = false; menu.Items.Add(none);
        }
        menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height + Ui.S(4)));
    }

    static string CleanExe(string text)
    {
        string exe = text.Trim().Trim('"');
        if (exe.Length == 0) return null;
        try { exe = Path.GetFileName(exe); } catch { }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        return exe;
    }

    void Save()
    {
        if (hotkey.Vk == 0) { error.Text = "Bitte zuerst eine Tastenkombination drücken."; hotkey.Focus(); return; }
        string exe = CleanExe(app.Text);
        if (exe == null) { error.Text = "Bitte eine App auswählen oder den exe-Namen eintragen."; app.Focus(); return; }
        string hideExe = CleanExe(hide.Text);
        if (hideExe != null && hideExe.Equals(exe, StringComparison.OrdinalIgnoreCase))
        { error.Text = "Die versteckte App muss eine andere sein als die Ziel-App."; hide.Focus(); return; }
        if (hotkey.Mods == 0 && !(hotkey.Vk >= (uint)Keys.F13 && hotkey.Vk <= (uint)Keys.F24))
        {
            if (MessageBox.Show(this, "Hotkey ohne Ctrl/Alt/Shift/Win blockiert diese Taste überall. Trotzdem?", "AppSwitch",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        }
        Result = new Binding
        {
            Mods = hotkey.Mods, Vk = hotkey.Vk, ExeName = exe, HideExe = hideExe, CloseCurrent = closeCurrent.Checked,
            LaunchPath = launch.Text.Trim().Length > 0 ? launch.Text.Trim() : null
        };
        DialogResult = DialogResult.OK;
    }
}

// ───────────────────────────── Main window + tray ─────────────────────────────

class MainForm : Form
{
    const string ShowEventName = "AppSwitch_ShowWindow";
    readonly string configPath = ConfigPath();
    readonly BufferedPanel header = new BufferedPanel(), body = new BufferedPanel(), footer = new BufferedPanel();
    readonly FlatButton addBtn = new FlatButton("Hotkey hinzufügen", ButtonKind.Primary, Ui.GlyphAdd);
    readonly FlatButton quitBtn = new FlatButton("Beenden", ButtonKind.Ghost, Ui.GlyphPower);
    readonly ToggleSwitch toggle = new ToggleSwitch("Nochmal drücken = minimieren");
    readonly ToggleSwitch autostart = new ToggleSwitch("Mit Windows starten");
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem restoreItem;
    readonly ToolTip tip = new ToolTip();
    readonly List<BindingRow> rows = new List<BindingRow>();
    readonly EventWaitHandle showEvent;
    List<Binding> bindings = new List<Binding>();
    bool reallyExit, placed, hintShown;

    static string ConfigDir()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Config lives in %APPDATA%\AppSwitch (install folder may be read-only). Old config next to exe is migrated.
    static string ConfigPath()
    {
        string path = Path.Combine(ConfigDir(), "keybinds.ini");
        string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keybinds.ini");
        if (!File.Exists(path) && File.Exists(old)) { try { File.Copy(old, path); } catch { } }
        return path;
    }

    static string AutostartCommand() { return "\"" + Application.ExecutablePath + "\" --minimized"; }

    static bool AutostartEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey))
            return k != null && k.GetValue(AppInfo.Name) != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
        {
            if (on) k.SetValue(AppInfo.Name, AutostartCommand());
            else k.DeleteValue(AppInfo.Name, false);
        }
    }

    public MainForm()
    {
        Text = "AppSwitch"; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false; MinimizeBox = false; KeyPreview = true;
        Font = Ui.Body; BackColor = Ui.Bg; ForeColor = Ui.Fg; StartPosition = FormStartPosition.Manual;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = SystemIcons.Application; }
        ClientSize = new Size(Ui.S(620), Ui.S(540));
        MinimumSize = SizeFromClientSize(new Size(Ui.S(500), Ui.S(380)));

        HiddenWindows.Init(ConfigDir());

        // header
        header.Dock = DockStyle.Top; header.Height = Ui.S(92);
        header.Paint += PaintHeader;
        addBtn.FitWidth(); addBtn.Height = Ui.S(38);
        addBtn.Click += (s, e) => AddBinding();
        header.Controls.Add(addBtn);
        header.Resize += (s, e) => addBtn.Location = new Point(header.ClientSize.Width - addBtn.Width - Ui.S(24), (header.Height - addBtn.Height) / 2);

        // footer
        footer.Dock = DockStyle.Bottom; footer.Height = Ui.S(96);
        footer.Paint += (s, e) => { using (var p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        toggle.Location = new Point(Ui.S(24), Ui.S(18));
        autostart.Location = new Point(Ui.S(24), Ui.S(52));
        quitBtn.FitWidth();
        quitBtn.Click += (s, e) => Quit();
        footer.Controls.Add(toggle); footer.Controls.Add(autostart); footer.Controls.Add(quitBtn);
        footer.Resize += (s, e) => quitBtn.Location = new Point(footer.ClientSize.Width - quitBtn.Width - Ui.S(20), (footer.Height - quitBtn.Height) / 2);

        // body
        body.Dock = DockStyle.Fill; body.AutoScroll = true; body.AutoScrollMargin = new Size(0, Ui.S(16));
        body.Resize += (s, e) => LayoutRows();
        body.Paint += PaintEmpty;
        body.HandleCreated += (s, e) => Ui.DarkScrollbars(body);

        Controls.Add(body); Controls.Add(footer); Controls.Add(header);

        // tray
        var menu = DarkMenuRenderer.Menu();
        menu.ShowImageMargin = false;
        var open = DarkMenuRenderer.Item("AppSwitch öffnen", null, (s, e) => ShowMain());
        open.Font = Ui.Semibold;
        restoreItem = DarkMenuRenderer.Item("Versteckte Apps zurückholen", null, (s, e) => HiddenWindows.RestoreAll());
        menu.Items.Add(open);
        menu.Items.Add(restoreItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenuRenderer.Item("Beenden", null, (s, e) => Quit()));
        menu.Opening += (s, e) => restoreItem.Visible = HiddenWindows.Any;
        tray.ContextMenuStrip = menu;
        tray.Icon = new Icon(Icon, SystemInformation.SmallIconSize);
        tray.Text = "AppSwitch";
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        tray.Visible = true;

        // settings
        LoadConfig();
        toggle.CheckedChanged += (s, e) => SaveConfig();
        autostart.Checked = AutostartEnabled();
        if (autostart.Checked) SetAutostart(true); // keep path current if exe was moved
        autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);

        RegisterAll(); // also creates the window handle (needed for hotkeys while hidden)

        // a second start of AppSwitch just opens this window
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showEvent, (st, timeout) =>
        {
            try { BeginInvoke((MethodInvoker)ShowMain); } catch { }
        }, null, -1, false);

        // Windows 11 hides new tray icons in the overflow -> pin ours once
        var pinTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        pinTimer.Tick += (s, e) => { pinTimer.Stop(); pinTimer.Dispose(); PromoteTrayIcon(); };
        pinTimer.Start();
    }

    public static void SignalRunningInstance()
    {
        try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
        catch { MessageBox.Show("AppSwitch läuft schon (Symbol unten rechts im Infobereich).", "AppSwitch"); }
    }

    // Sets IsPromoted=1 on our entry in HKCU\Control Panel\NotifyIconSettings, only if the user never chose.
    static void PromoteTrayIcon()
    {
        try
        {
            string exeName = "\\" + Path.GetFileName(Application.ExecutablePath);
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name, true))
                    {
                        if (k == null) continue;
                        var p = k.GetValue("ExecutablePath") as string;
                        if (p == null || !p.EndsWith(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (k.GetValue("IsPromoted") == null) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    }
            }
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.StyleWindow(Handle); }

    public void ShowMain()
    {
        if (!placed)
        {
            var wa = Screen.PrimaryScreen.WorkingArea; // opens next to the tray
            Location = new Point(wa.Right - Width - Ui.S(16), wa.Bottom - Height - Ui.S(16));
            placed = true;
        }
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        TopMost = true; TopMost = false; // lift above other windows even if focus is refused
        Activate();
        WinApi.ForceForeground(Handle);
    }

    void HideToTray()
    {
        Hide();
        if (hintShown) return;
        hintShown = true;
        tray.ShowBalloonTip(2500, "AppSwitch läuft weiter", "Hotkeys bleiben aktiv. Klick aufs Symbol im Infobereich öffnet das Fenster.", ToolTipIcon.None);
    }

    void Quit()
    {
        reallyExit = true;
        HiddenWindows.RestoreAll();
        tray.Visible = false;
        UnregisterAll();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tray.Dispose(); tip.Dispose(); if (showEvent != null) showEvent.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { HideToTray(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // X button -> hide to tray so hotkeys keep working. "Beenden" really quits.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; HideToTray(); return; }
        HiddenWindows.RestoreAll();
        UnregisterAll();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) OnHotkey(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    void OnHotkey(int id)
    {
        if (id < 0 || id >= bindings.Count) return;
        var b = bindings[id];
        IntPtr wnd = WinApi.FindFor(b.ExeName);

        // pressed again while target is in front -> minimize it, bring hidden app back
        if (wnd != IntPtr.Zero && toggle.Checked && Native.GetForegroundWindow() == wnd && !Native.IsIconic(wnd))
        {
            Native.ShowWindow(wnd, Native.SW_MINIMIZE);
            if (b.HideExe != null)
            {
                IntPtr back = HiddenWindows.Restore(b.HideExe);
                if (back != IntPtr.Zero) WinApi.ForceForeground(back);
            }
            return;
        }

        IntPtr prev = Native.GetForegroundWindow();
        if (b.HideExe != null) HiddenWindows.Hide(b.HideExe);
        if (wnd == IntPtr.Zero)
        {
            if (b.LaunchPath == null) return;
            WinApi.Launch(b.LaunchPath);
        }
        else WinApi.ForceForeground(wnd);
        if (b.CloseCurrent) ClosePrevious(prev, wnd);
    }

    // Normal close (like the X button) -> the app can still ask to save. Never desktop/taskbar, target or ourselves.
    static void ClosePrevious(IntPtr prev, IntPtr target)
    {
        if (prev == IntPtr.Zero || prev == target || !WinApi.IsAppWindow(prev)) return;
        uint pid, targetPid = 0;
        Native.GetWindowThreadProcessId(prev, out pid);
        if (target != IntPtr.Zero) Native.GetWindowThreadProcessId(target, out targetPid);
        if (pid == (uint)Process.GetCurrentProcess().Id || pid == targetPid) return;
        Native.PostMessage(prev, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    void RegisterAll()
    {
        UnregisterAll();
        for (int i = 0; i < bindings.Count; i++)
            bindings[i].Registered = Native.RegisterHotKey(Handle, i, bindings[i].Mods | Native.MOD_NOREPEAT, bindings[i].Vk);
        RefreshList();
    }

    void UnregisterAll()
    {
        for (int i = 0; i < 256; i++) Native.UnregisterHotKey(Handle, i);
    }

    static string IconPathFor(Binding b, Dictionary<string, string> running)
    {
        if (b.LaunchPath != null)
        {
            string file, args; WinApi.SplitCommand(b.LaunchPath, out file, out args);
            if (File.Exists(file)) return file;
        }
        string p;
        return running.TryGetValue(b.ExeName, out p) ? p : null;
    }

    void RefreshList()
    {
        body.SuspendLayout();
        foreach (var r in rows) { body.Controls.Remove(r); r.Dispose(); }
        rows.Clear();
        tip.RemoveAll();

        var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in WinApi.List()) if (!running.ContainsKey(w.ExeName)) running[w.ExeName] = w.ExePath;

        int keyCol = Ui.S(100);
        using (var g = CreateGraphics())
            foreach (var b in bindings) keyCol = Math.Max(keyCol, Ui.DrawKeys(g, b.HotkeyText, 0, 0, false));

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i]; int idx = i;
            var row = new BindingRow(b, IconCache.For(IconPathFor(b, running))) { KeyCol = keyCol };
            row.EditRequested += (s, e) => BeginInvoke((MethodInvoker)(() => EditBinding(idx)));
            row.DeleteRequested += (s, e) => BeginInvoke((MethodInvoker)(() => RemoveBinding(idx)));
            if (!b.Registered) tip.SetToolTip(row, "Diese Kombination benutzt schon eine andere App. Bitte eine andere wählen.");
            body.Controls.Add(row); rows.Add(row);
        }
        LayoutRows();
        body.ResumeLayout();
        body.Invalidate();
    }

    void LayoutRows()
    {
        int pad = Ui.S(20), h = Ui.S(68), y = body.AutoScrollPosition.Y + Ui.S(4);
        int w = body.ClientSize.Width - pad * 2;
        foreach (var r in rows) { r.SetBounds(pad, y, w, h); y += h + Ui.S(10); }
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Prepare(g);
        int h = header.Height, isz = Ui.S(36);
        g.DrawIcon(Icon, new Rectangle(Ui.S(24), (h - isz) / 2, isz, isz));
        int tx = Ui.S(24) + isz + Ui.S(14), tw = addBtn.Left - tx - Ui.S(12);
        Ui.Text(g, "AppSwitch", Ui.Title, new Rectangle(tx, h / 2 - Ui.S(29), tw, Ui.S(30)), Ui.Fg, TextFormatFlags.Bottom);
        Ui.Text(g, "Hotkey drücken – App ist sofort vorne.", Ui.Small, new Rectangle(tx, h / 2 + Ui.S(3), tw, Ui.S(20)), Ui.Muted, TextFormatFlags.Top);
    }

    void PaintEmpty(object sender, PaintEventArgs e)
    {
        if (rows.Count > 0) return;
        var g = e.Graphics; Ui.Prepare(g);
        var c = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        int w = body.ClientSize.Width, cy = body.ClientSize.Height / 2 - Ui.S(20);
        var circle = new Rectangle(w / 2 - Ui.S(36), cy - Ui.S(80), Ui.S(72), Ui.S(72));
        using (var b = new SolidBrush(Ui.Surface)) g.FillEllipse(b, circle);
        Ui.Text(g, Ui.GlyphKeyboard, Ui.IconsBig, circle, Ui.Accent, c);
        Ui.Text(g, "Noch keine Hotkeys", Ui.Heading, new Rectangle(0, cy + Ui.S(4), w, Ui.S(28)), Ui.Fg, c);
        TextRenderer.DrawText(g, "Lege eine App auf eine Tastenkombination –\nz. B. Ctrl+Alt+D für Discord.", Ui.Body,
            new Rectangle(Ui.S(30), cy + Ui.S(38), w - Ui.S(60), Ui.S(44)), Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    void AddBinding()
    {
        UnregisterAll(); // so the recorder can see existing combos
        using (var d = new EditDialog(null))
            if (d.ShowDialog(this) == DialogResult.OK) { bindings.Add(d.Result); SaveConfig(); }
        RegisterAll();
    }

    void EditBinding(int i)
    {
        if (i < 0 || i >= bindings.Count) return;
        UnregisterAll();
        using (var d = new EditDialog(bindings[i]))
            if (d.ShowDialog(this) == DialogResult.OK) { bindings[i] = d.Result; SaveConfig(); }
        RegisterAll();
    }

    void RemoveBinding(int i)
    {
        if (i < 0 || i >= bindings.Count) return;
        var b = bindings[i];
        if (b.HideExe != null) HiddenWindows.Restore(b.HideExe);
        bindings.RemoveAt(i);
        SaveConfig(); RegisterAll();
    }

    // line format: Hotkey = App.exe | launch path | hide.exe | close   (last three optional)
    void LoadConfig()
    {
        toggle.Checked = true;
        if (!File.Exists(configPath)) return;
        foreach (var raw in File.ReadAllLines(configPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string left = line.Substring(0, eq).Trim(), right = line.Substring(eq + 1).Trim();
            if (left.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            { toggle.Checked = !right.Equals("false", StringComparison.OrdinalIgnoreCase); continue; }

            var b = new Binding();
            if (!Hotkeys.Parse(left, out b.Mods, out b.Vk)) continue;
            var parts = right.Split(new[] { '|' }, 4);
            b.ExeName = parts[0].Trim();
            if (!b.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b.ExeName += ".exe";
            if (parts.Length > 1 && parts[1].Trim().Length > 0) b.LaunchPath = Environment.ExpandEnvironmentVariables(parts[1].Trim());
            if (parts.Length > 2 && parts[2].Trim().Length > 0)
            {
                b.HideExe = parts[2].Trim();
                if (!b.HideExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b.HideExe += ".exe";
            }
            if (parts.Length > 3) b.CloseCurrent = parts[3].Trim().Equals("close", StringComparison.OrdinalIgnoreCase);
            bindings.Add(b);
        }
    }

    void SaveConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AppSwitch config - wird von der App verwaltet");
        sb.AppendLine("# Hotkey = App.exe | Startpfad | Versteckte.exe | close");
        sb.AppendLine("toggle = " + (toggle.Checked ? "true" : "false"));
        foreach (var b in bindings)
        {
            string line = b.HotkeyText + " = " + b.ExeName;
            if (b.LaunchPath != null || b.HideExe != null || b.CloseCurrent) line += " | " + (b.LaunchPath ?? "");
            if (b.HideExe != null || b.CloseCurrent) line += " | " + (b.HideExe ?? "");
            if (b.CloseCurrent) line += " | close";
            sb.AppendLine(line);
        }
        File.WriteAllText(configPath, sb.ToString());
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (new Mutex(true, "AppSwitch_SingleInstance", out created))
        {
            if (!created) { SignalRunningInstance(); return; }
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.Init();
            bool minimized = Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            using (var form = new MainForm())
            {
                if (!minimized) form.ShowMain();
                Application.Run();
            }
        }
    }
}
