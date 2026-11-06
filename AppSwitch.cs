// AppSwitch - global hotkey -> bring a specific app to the foreground instantly.
// Build: build.bat   Config: keybinds.ini (next to the exe, edited via the GUI)
// Note: compiled with the .NET Framework csc (C# 5) -> no string interpolation, no ?. etc.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
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
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int SW_RESTORE = 9, SW_SHOW = 5, SW_MINIMIZE = 6;
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
}

class WinInfo
{
    public IntPtr Handle; public string Title, ExeName, ExePath;
    public override string ToString() { return ExeName + "  —  " + Title; }
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

    static bool IsAppWindow(IntPtr h)
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
            result.Add(new WinInfo { Handle = h, Title = sb.ToString(), ExePath = path, ExeName = Path.GetFileName(path) });
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

    public static void Launch(string cmd)
    {
        try
        {
            // supports "path\app.exe --args" ; quoted paths too
            string file = cmd, args = "";
            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); file = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
            else
            {
                int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) { file = cmd.Substring(0, exe + 4); args = cmd.Substring(exe + 4).Trim(); }
            }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Start fehlgeschlagen:\n" + cmd + "\n\n" + ex.Message, "AppSwitch"); }
    }
}

// Textbox that records a key combination when focused.
class HotkeyBox : TextBox
{
    public uint Mods, Vk;

    public HotkeyBox()
    {
        ReadOnly = true; BackColor = SystemColors.Window; ShortcutsEnabled = false;
        Text = "Hier klicken und Tasten drücken…";
    }

    protected override bool IsInputKey(Keys k) { return true; }
    protected override bool ProcessCmdKey(ref Message msg, Keys k) { return false; } // let Tab/Enter/Alt reach KeyDown

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true; e.SuppressKeyPress = true;
        var key = e.KeyCode;
        uint m = 0;
        if (e.Control) m |= Native.MOD_CONTROL;
        if (e.Alt) m |= Native.MOD_ALT;
        if (e.Shift) m |= Native.MOD_SHIFT;
        if (Native.WinDown()) m |= Native.MOD_WIN;

        bool modifierOnly = key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey ||
                            key == Keys.LWin || key == Keys.RWin || key == Keys.LControlKey || key == Keys.RControlKey ||
                            key == Keys.LShiftKey || key == Keys.RShiftKey || key == Keys.LMenu || key == Keys.RMenu;
        if (modifierOnly) { Text = Hotkeys.Format(m, 0) + "+…"; return; }

        Mods = m; Vk = (uint)key;
        Text = Hotkeys.Format(Mods, Vk);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (Vk != 0) Text = Hotkeys.Format(Mods, Vk);
    }

    public void Set(uint mods, uint vk) { Mods = mods; Vk = vk; Text = vk == 0 ? "Hier klicken und Tasten drücken…" : Hotkeys.Format(mods, vk); }
}

class EditDialog : Form
{
    readonly HotkeyBox hotkey = new HotkeyBox();
    readonly ComboBox apps = new ComboBox();
    readonly TextBox launch = new TextBox();
    public Binding Result;

    public EditDialog(Binding existing)
    {
        Text = existing == null ? "Hotkey hinzufügen" : "Hotkey bearbeiten";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(520, 210);
        Font = new Font("Segoe UI", 9.5f); ShowInTaskbar = false;

        Controls.Add(new Label { Text = "Tastenkombination:", Location = new Point(12, 15), AutoSize = true });
        hotkey.SetBounds(150, 12, 355, 26); Controls.Add(hotkey);

        Controls.Add(new Label { Text = "App (offenes Fenster\noder exe-Name):", Location = new Point(12, 50), AutoSize = true });
        apps.SetBounds(150, 52, 270, 26); apps.DropDownStyle = ComboBoxStyle.DropDown;
        apps.SelectedIndexChanged += (s, e) =>
        {
            var w = apps.SelectedItem as WinInfo;
            if (w != null) launch.Text = w.ExePath;
        };
        Controls.Add(apps);
        var refresh = new Button { Text = "Aktualisieren", Location = new Point(425, 51), Size = new Size(80, 28) };
        refresh.Click += (s, e) => FillApps();
        Controls.Add(refresh);

        Controls.Add(new Label { Text = "Starten, falls zu\n(optional):", Location = new Point(12, 95), AutoSize = true });
        launch.SetBounds(150, 97, 270, 26); Controls.Add(launch);
        var browse = new Button { Text = "Durchsuchen", Location = new Point(425, 96), Size = new Size(80, 28) };
        browse.Click += (s, e) =>
        {
            using (var ofd = new OpenFileDialog { Filter = "Programme (*.exe)|*.exe" })
                if (ofd.ShowDialog(this) == DialogResult.OK) launch.Text = ofd.FileName;
        };
        Controls.Add(browse);

        var ok = new Button { Text = "Speichern", Location = new Point(320, 165), Size = new Size(90, 32) };
        var cancel = new Button { Text = "Abbrechen", Location = new Point(415, 165), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };
        ok.Click += (s, e) => Save();
        Controls.Add(ok); Controls.Add(cancel); CancelButton = cancel;

        FillApps();
        if (existing != null)
        {
            hotkey.Set(existing.Mods, existing.Vk);
            apps.Text = existing.ExeName;
            launch.Text = existing.LaunchPath ?? "";
        }
        Shown += (s, e) => hotkey.Focus();
    }

    void FillApps()
    {
        string keep = apps.Text;
        apps.Items.Clear();
        var seen = new HashSet<IntPtr>();
        foreach (var w in WinApi.List())
        {
            if (w.Handle == Handle || w.Handle == (Owner != null ? Owner.Handle : IntPtr.Zero)) continue;
            if (seen.Add(w.Handle)) apps.Items.Add(w);
        }
        apps.Text = keep;
    }

    void Save()
    {
        if (hotkey.Vk == 0) { MessageBox.Show(this, "Bitte zuerst eine Tastenkombination drücken.", "AppSwitch"); return; }
        if (hotkey.Mods == 0 && !(hotkey.Vk >= (uint)Keys.F13 && hotkey.Vk <= (uint)Keys.F24))
        {
            if (MessageBox.Show(this, "Hotkey ohne Ctrl/Alt/Shift/Win blockiert diese Taste überall. Trotzdem?", "AppSwitch",
                MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        }
        var w = apps.SelectedItem as WinInfo;
        string exe = w != null && apps.Text == w.ToString() ? w.ExeName : apps.Text.Trim();
        int dash = exe.IndexOf("  —  ");
        if (dash > 0) exe = exe.Substring(0, dash);
        if (exe.Length == 0) { MessageBox.Show(this, "Bitte eine App auswählen.", "AppSwitch"); return; }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";

        Result = new Binding { Mods = hotkey.Mods, Vk = hotkey.Vk, ExeName = exe,
                               LaunchPath = launch.Text.Trim().Length > 0 ? launch.Text.Trim() : null };
        DialogResult = DialogResult.OK;
    }
}

class MainForm : Form
{
    readonly string configPath = ConfigPath();
    readonly ListView list = new ListView();
    readonly CheckBox toggle = new CheckBox();
    readonly CheckBox autostart = new CheckBox();
    List<Binding> bindings = new List<Binding>();
    bool reallyExit;

    // Config lives in %APPDATA%\AppSwitch (install folder may be read-only). Old config next to exe is migrated.
    static string ConfigPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "keybinds.ini");
        string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keybinds.ini");
        if (!File.Exists(path) && File.Exists(old)) { try { File.Copy(old, path); } catch { } }
        return path;
    }

    static string AutostartCommand() { return "\"" + Application.ExecutablePath + "\" --minimized"; }

    static bool AutostartEnabled()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppInfo.RunKey))
            return k != null && k.GetValue(AppInfo.Name) != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
        {
            if (on) k.SetValue(AppInfo.Name, AutostartCommand());
            else k.DeleteValue(AppInfo.Name, false);
        }
    }

    public MainForm(bool startMinimized)
    {
        Text = "AppSwitch"; ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { Icon = SystemIcons.Application; }
        if (startMinimized) WindowState = FormWindowState.Minimized;
        ClientSize = new Size(620, 350); MinimumSize = new Size(500, 260);
        Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterScreen;

        list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = false; list.HideSelection = false;
        list.Columns.Add("Hotkey", 140); list.Columns.Add("App", 160); list.Columns.Add("Starten, falls zu", 220); list.Columns.Add("Status", 80);
        list.SetBounds(12, 12, 596, 260); list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.DoubleClick += (s, e) => EditSelected();
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        Controls.Add(list);

        var bottom = AnchorStyles.Bottom | AnchorStyles.Left;
        AddButton("Hinzufügen", 12, bottom, (s, e) => Add());
        AddButton("Bearbeiten", 112, bottom, (s, e) => EditSelected());
        AddButton("Entfernen", 212, bottom, (s, e) => RemoveSelected());
        AddButton("Beenden", 518, AnchorStyles.Bottom | AnchorStyles.Right, (s, e) => { reallyExit = true; Close(); });

        toggle.Text = "Nochmal drücken = minimieren"; toggle.AutoSize = true; toggle.Location = new Point(312, 281); toggle.Anchor = bottom;
        Controls.Add(toggle);

        autostart.Text = "Mit Windows starten"; autostart.AutoSize = true; autostart.Location = new Point(312, 305); autostart.Anchor = bottom;
        autostart.Checked = AutostartEnabled();
        if (autostart.Checked) SetAutostart(true); // keep path current if exe was moved
        autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
        Controls.Add(autostart);

        LoadConfig();
        toggle.CheckedChanged += (s, e) => SaveConfig();
        RegisterAll();
    }

    void AddButton(string text, int x, AnchorStyles anchor, EventHandler click)
    {
        var b = new Button { Text = text, Location = new Point(x, 285), Size = new Size(92, 32), Anchor = anchor };
        b.Click += click; Controls.Add(b);
    }

    // X button -> minimize to taskbar so hotkeys keep working. "Beenden" really quits.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; WindowState = FormWindowState.Minimized; return; }
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
        if (wnd == IntPtr.Zero) { if (b.LaunchPath != null) WinApi.Launch(b.LaunchPath); return; }
        if (toggle.Checked && Native.GetForegroundWindow() == wnd && !Native.IsIconic(wnd))
        { Native.ShowWindow(wnd, Native.SW_MINIMIZE); return; }
        WinApi.ForceForeground(wnd);
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

    void RefreshList()
    {
        list.Items.Clear();
        foreach (var b in bindings)
        {
            var it = new ListViewItem(new[] { b.HotkeyText, b.ExeName, b.LaunchPath ?? "", b.Registered ? "aktiv" : "BELEGT" });
            if (!b.Registered) it.ForeColor = Color.Red;
            list.Items.Add(it);
        }
    }

    void Add()
    {
        UnregisterAll(); // so the recorder can see existing combos
        using (var d = new EditDialog(null))
            if (d.ShowDialog(this) == DialogResult.OK) { bindings.Add(d.Result); SaveConfig(); }
        RegisterAll();
    }

    void EditSelected()
    {
        if (list.SelectedIndices.Count == 0) return;
        int i = list.SelectedIndices[0];
        UnregisterAll();
        using (var d = new EditDialog(bindings[i]))
            if (d.ShowDialog(this) == DialogResult.OK) { bindings[i] = d.Result; SaveConfig(); }
        RegisterAll();
    }

    void RemoveSelected()
    {
        if (list.SelectedIndices.Count == 0) return;
        bindings.RemoveAt(list.SelectedIndices[0]);
        SaveConfig(); RegisterAll();
    }

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
            var parts = right.Split(new[] { '|' }, 2);
            b.ExeName = parts[0].Trim();
            if (!b.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b.ExeName += ".exe";
            if (parts.Length > 1 && parts[1].Trim().Length > 0) b.LaunchPath = Environment.ExpandEnvironmentVariables(parts[1].Trim());
            bindings.Add(b);
        }
    }

    void SaveConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AppSwitch config - wird von der App verwaltet");
        sb.AppendLine("toggle = " + (toggle.Checked ? "true" : "false"));
        foreach (var b in bindings)
            sb.AppendLine(b.HotkeyText + " = " + b.ExeName + (b.LaunchPath != null ? " | " + b.LaunchPath : ""));
        File.WriteAllText(configPath, sb.ToString());
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (new System.Threading.Mutex(true, "AppSwitch_SingleInstance", out created))
        {
            if (!created) { MessageBox.Show("AppSwitch läuft schon (Taskleiste).", "AppSwitch"); return; }
            Application.EnableVisualStyles();
            bool minimized = Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(minimized));
        }
    }
}
