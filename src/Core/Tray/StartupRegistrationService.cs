using Microsoft.Win32;
using Core.Branding;

namespace Core.Tray;

public static class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = ProductInfo.Name;
    private const string LegacyValueName = ProductInfo.LegacyName;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            string packagedExecutable = Path.Combine(AppContext.BaseDirectory, ProductInfo.ExecutableName);
            string executable = File.Exists(packagedExecutable)
                ? packagedExecutable
                : Environment.ProcessPath
                    ?? throw new InvalidOperationException("The executable path is unavailable.");
            key.SetValue(ValueName, $"\"{executable}\"");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }
}
