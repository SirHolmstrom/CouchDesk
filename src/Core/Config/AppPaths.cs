using Core.Branding;

namespace Core.Config;

/// <summary>
/// Central place for on-disk locations, all under the current user's profile
/// (%LOCALAPPDATA%\CouchDesk). Existing RemoteDesktopLAN installations continue
/// using their original folder so passwords and certificates are not lost.
/// admin rights are needed and secrets stay tied to this Windows account.
/// </summary>
public static class AppPaths
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string PreferredRoot = Path.Combine(LocalAppData, ProductInfo.DataFolderName);
    private static readonly string LegacyRoot = Path.Combine(LocalAppData, ProductInfo.LegacyDataFolderName);
    private static readonly string Root = ResolveRoot();

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string CertPfx => Path.Combine(Root, "certs", "server.pfx");
    public static string CertPwd => Path.Combine(Root, "certs", "server.pwd"); // DPAPI-protected
    public static string AuditLog => Path.Combine(Root, "logs", "audit.log");
    public static string ConfigFolder => Root;

    /// <summary>Where files uploaded from clients are saved.</summary>
    public static string Inbox
    {
        get
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string preferred = Path.Combine(downloads, ProductInfo.InboxFolderName);
            string legacy = Path.Combine(downloads, ProductInfo.LegacyName);
            return Directory.Exists(preferred) || !Directory.Exists(legacy) ? preferred : legacy;
        }
    }

    private static string ResolveRoot()
    {
        if (File.Exists(Path.Combine(PreferredRoot, "config.json"))) return PreferredRoot;
        if (File.Exists(Path.Combine(LegacyRoot, "config.json"))) return LegacyRoot;
        if (Directory.Exists(PreferredRoot)) return PreferredRoot;
        if (Directory.Exists(LegacyRoot)) return LegacyRoot;
        return PreferredRoot;
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "certs"));
        Directory.CreateDirectory(Path.Combine(Root, "logs"));
        Directory.CreateDirectory(Inbox);
        // TODO (hardening): tighten ACLs on Root to the current user only via
        // DirectoryInfo + DirectorySecurity so other accounts can't read the cert/config.
    }
}
