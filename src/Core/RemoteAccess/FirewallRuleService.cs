using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Core.RemoteAccess;

public sealed record FirewallOperationResult(bool Success, string Message);

public sealed class FirewallRuleService
{
    private const string LanRuleName = "CouchDesk LAN";
    private const string PublicRuleName = "CouchDesk Public";
    private const string LegacyLanRuleName = "RemoteDesktopLAN LAN";
    private const string LegacyPublicRuleName = "RemoteDesktopLAN Public";

    public Task<FirewallOperationResult> EnsureLanRuleAsync(int port) =>
        ReplaceRuleAsync(LanRuleName, port, "LocalSubnet");

    public Task<FirewallOperationResult> EnsurePublicRuleAsync(int port) =>
        ReplaceRuleAsync(PublicRuleName, port, "Any");

    public Task<FirewallOperationResult> RemovePublicRuleAsync() =>
        RunElevatedPowerShellAsync(
            $"Get-NetFirewallRule -DisplayName '{PublicRuleName}','{LegacyPublicRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop");

    private static Task<FirewallOperationResult> ReplaceRuleAsync(
        string displayName,
        int port,
        string remoteAddress)
    {
        string command =
            $"Get-NetFirewallRule -DisplayName '{displayName}','{LegacyLanRuleName}','{LegacyPublicRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -DisplayName '{displayName}' -Direction Inbound -Action Allow " +
            $"-Protocol TCP -LocalPort {port} -RemoteAddress {remoteAddress} -Profile Any -ErrorAction Stop | Out-Null";
        return RunElevatedPowerShellAsync(command);
    }

    private static async Task<FirewallOperationResult> RunElevatedPowerShellAsync(string command)
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, "Windows could not start the elevated firewall helper.");

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new(true, "Firewall rule updated.")
                : new(false, $"The firewall helper exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "Administrator approval for the firewall change was cancelled.");
        }
        catch (Exception ex)
        {
            return new(false, $"The firewall rule could not be changed: {ex.Message}");
        }
    }
}
