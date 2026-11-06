// AppSwitch installer: per-user install (no admin), AppSwitch.exe is embedded as resource.
// Installed copy of this exe = Uninstall.exe (runs uninstall when started as "Uninstall.exe" or with /uninstall).
// Compiled with the .NET Framework csc (C# 5).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class Setup : Form
{
    static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name);
    static readonly string AppExe = Path.Combine(InstallDir, "AppSwitch.exe");
    static readonly string UninstallExe = Path.Combine(InstallDir, "Uninstall.exe");
    static readonly string StartMenuLnk = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AppSwitch.lnk");
    static readonly string DesktopLnk = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AppSwitch.lnk");

    readonly CheckBox desktop = new CheckBox { Text = "Desktop-Verknüpfung erstellen", Checked = true, AutoSize = true };
    readonly CheckBox autostart = new CheckBox { Text = "Mit Windows starten", Checked = true, AutoSize = true };
    readonly CheckBox launch = new CheckBox { Text = "AppSwitch nach der Installation starten", Checked = true, AutoSize = true };

    Setup()
    {
        Text = "AppSwitch " + AppInfo.Version + " installieren";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 230); Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "AppSwitch", Font = new Font("Segoe UI", 16f, FontStyle.Bold), Location = new Point(16, 12), AutoSize = true });
        Controls.Add(new Label { Text = "Wechselt per Hotkey sofort zu einer App.\nZiel: " + InstallDir,
                                 Location = new Point(18, 50), Size = new Size(410, 44) });
        desktop.Location = new Point(20, 100); autostart.Location = new Point(20, 125); launch.Location = new Point(20, 150);
        Controls.Add(desktop); Controls.Add(autostart); Controls.Add(launch);

        var install = new Button { Text = "Installieren", Location = new Point(230, 186), Size = new Size(100, 32) };
        var cancel = new Button { Text = "Abbrechen", Location = new Point(334, 186), Size = new Size(94, 32), DialogResult = DialogResult.Cancel };
        install.Click += (s, e) => Install();
        Controls.Add(install); Controls.Add(cancel); AcceptButton = install; CancelButton = cancel;
    }

    static void StopRunningApp()
    {
        foreach (var p in Process.GetProcessesByName("AppSwitch"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    static void CreateShortcut(string lnk, string target, string args)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    void Install()
    {
        try
        {
            StopRunningApp();
            Directory.CreateDirectory(InstallDir);

            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppSwitch.exe"))
            using (var f = File.Create(AppExe)) res.CopyTo(f);
            File.Copy(Application.ExecutablePath, UninstallExe, true);

            CreateShortcut(StartMenuLnk, AppExe, "");
            if (desktop.Checked) CreateShortcut(DesktopLnk, AppExe, "");

            using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
            {
                if (autostart.Checked) k.SetValue(AppInfo.Name, "\"" + AppExe + "\" --minimized");
                else k.DeleteValue(AppInfo.Name, false);
            }

            using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Publisher);
                k.SetValue("DisplayIcon", AppExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallExe + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(AppExe).Length / 1024 + 1), RegistryValueKind.DWord);
            }

            if (launch.Checked) Process.Start(AppExe);
            MessageBox.Show(this, "AppSwitch wurde installiert.", "AppSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Installation fehlgeschlagen:\n" + ex.Message, "AppSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void Uninstall()
    {
        if (MessageBox.Show("AppSwitch wirklich deinstallieren?", "AppSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        StopRunningApp();

        using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, true))
            if (k != null) k.DeleteValue(AppInfo.Name, false);
        Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false);
        foreach (var lnk in new[] { StartMenuLnk, DesktopLnk })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }

        string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        if (Directory.Exists(cfg) &&
            MessageBox.Show("Auch deine Hotkey-Einstellungen löschen?", "AppSwitch", MessageBoxButtons.YesNo) == DialogResult.Yes)
            try { Directory.Delete(cfg, true); } catch { }

        try { File.Delete(AppExe); } catch { }

        // Uninstall.exe can't delete itself while running -> let cmd remove the folder after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show("AppSwitch wurde entfernt.", "AppSwitch");
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool uninstall = Array.Exists(args, a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
            || Path.GetFileName(Application.ExecutablePath).Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
        if (uninstall) { Uninstall(); return; }
        Application.Run(new Setup());
    }
}
