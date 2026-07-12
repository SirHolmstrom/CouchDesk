using System.Text.Json;

namespace Core.Config;

public enum RemoteAccessMode
{
    LanOnly,
    Automatic,
    ManualOnly,
    Disabled
}

/// <summary>
/// Persisted settings. The password field stores ONLY an Argon2id hash string —
/// never a plaintext or reversible password.
/// </summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 8443;

    /// <summary>
    /// Interface to bind to. Defaults to loopback-safe "0.0.0.0" only until setup
    /// completes; setup pins this to the detected LAN IP. Avoid leaving it on a
    /// public/routable NIC.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Argon2id hash string, or null when first-run setup is required.</summary>
    public string? PasswordHash { get; set; }

    public int SessionTimeoutMinutes { get; set; } = 120;
    public bool RemoteAccessEnabled { get; set; } = false;
    public bool ClipboardSyncEnabled { get; set; } = false;
    public RemoteAccessMode AccessMode { get; set; } = RemoteAccessMode.LanOnly;
    public int ExternalPort { get; set; } = 38443;
    public int FpsLimit { get; set; } = 30;
    public int JpegQuality { get; set; } = 75;

    /// <summary>
    /// When true (default), streaming uses GPU capture (DXGI Desktop Duplication) + hardware
    /// H.264 instead of the JPEG-tile path; with VideoLowLatency it's the per-frame WebCodecs
    /// path (single-digit-ms). Toggle it off from the tray to force JPEG tiles. If no hardware
    /// encoder is available the server falls back to JPEG automatically.
    /// </summary>
    public bool UseHardwareVideo { get; set; } = true;

    /// <summary>Target H.264 bitrate (kbps) for the hardware-video path.</summary>
    public int VideoBitrateKbps { get; set; } = 8000;

    /// <summary>
    /// When true (default), the hardware-video path emits one H.264 frame at a time and the
    /// browser decodes with WebCodecs — lowest latency. When false, it streams fragmented
    /// MP4 played via MediaSource (higher latency, but works on browsers without WebCodecs).
    /// </summary>
    public bool VideoLowLatency { get; set; } = true;
    public int GuestInviteDefaultMinutes { get; set; } = 60;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public bool ShowTaskbarButton { get; set; } = false;
    public bool ShowViewerOverlay { get; set; } = true;
    public bool TryAutomaticPortForwarding { get; set; } = true;
    public string? HostDisplayName { get; set; }
    public string? LastRemoteUrl { get; set; }
    public string? LastRouterUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(PasswordHash);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigFile)) return new AppConfig();
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile))
                   ?? new AppConfig();
            if (config.SessionTimeoutMinutes < 60) config.SessionTimeoutMinutes = 120;
            return config;
        }
        catch
        {
            // Corrupt config shouldn't brick startup; fall back to defaults (forces re-setup).
            return new AppConfig();
        }
    }

    public void Save() =>
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, JsonOptions));
}
