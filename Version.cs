// Shared by AppSwitch.exe and Setup.exe. Change version/publisher here only.
using System.Reflection;

[assembly: AssemblyProduct("AppSwitch")]
[assembly: AssemblyCompany(AppInfo.Publisher)]
[assembly: AssemblyCopyright("(c) 2026 " + AppInfo.Publisher)]
[assembly: AssemblyVersion(AppInfo.Version)]
[assembly: AssemblyFileVersion(AppInfo.Version)]

static class AppInfo
{
    public const string Name = "AppSwitch";
    public const string Version = "1.1.0.0";
    public const string Publisher = "AppSwitch";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppSwitch";
}
