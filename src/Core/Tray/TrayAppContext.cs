using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Core.Config;
using Core.Branding;
using Core.Hosting;
using Core.RemoteAccess;
using Core.Security;
using Core.Streaming;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

public sealed class TrayAppContext : WinForms.ApplicationContext
{
    private static readonly MethodInfo? ShowContextMenuMethod =
        typeof(WinForms.NotifyIcon).GetMethod(
            "ShowContextMenu",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly AppConfig m_Config;
    private readonly RemoteDesktopHost m_Host;
    private readonly RemoteAccessController m_Remote;
    private readonly NetworkInfoService m_Network;
    private readonly FirewallRuleService m_Firewall;
    private readonly TrayStatusIcons m_StatusIcons = new();
    private readonly WinForms.NotifyIcon m_NotifyIcon;
    private readonly WinForms.ContextMenuStrip m_Menu = new();
    private readonly TrayMenuHostForm m_MenuHost = new();
    private readonly WinForms.Timer m_StartupNotificationTimer = new() { Interval = 700 };
    private readonly WinForms.Timer m_ActivityRefreshTimer = new() { Interval = 500 };
    private readonly WinForms.Timer m_OverlayPreviewTimer = new() { Interval = 5000 };
    private readonly Dictionary<string, DateTime> m_LastJoinNotificationByClient = new();

    private readonly WinForms.ToolStripMenuItem m_HostNameStatus = new();
    private readonly WinForms.ToolStripMenuItem m_LanStatus = new();
    private readonly WinForms.ToolStripMenuItem m_RemoteStatus = new();
    private readonly WinForms.ToolStripMenuItem m_ActiveViewers = new("Active Viewers...");
    private readonly WinForms.ToolStripMenuItem m_GuestAccess = new("Guest Access");
    private readonly WinForms.ToolStripMenuItem m_CopyRemoteUrl = new("Copy Remote URL");
    private readonly WinForms.ToolStripMenuItem m_OpenRemote = new("Open Remote Access");
    private readonly WinForms.ToolStripMenuItem m_CloseRemote = new("Close Remote Access");
    private readonly WinForms.ToolStripMenuItem m_LanMode = new("LAN Only");
    private readonly WinForms.ToolStripMenuItem m_AutomaticMode = new("Automatic Remote Access");
    private readonly WinForms.ToolStripMenuItem m_ManualMode = new("Manual Remote Access");
    private readonly WinForms.ToolStripMenuItem m_OpenRouter = new("Open Router Page");
    private readonly WinForms.ToolStripMenuItem m_CopyForwarding = new("Copy Port Forwarding Values");
    private readonly WinForms.ToolStripMenuItem m_StartWithWindows = new("Start with Windows");
    private readonly WinForms.ToolStripMenuItem m_StartMinimized = new("Start Minimized");
    private readonly WinForms.ToolStripMenuItem m_ShowTaskbarButton = new("Show Taskbar Button");
    private readonly WinForms.ToolStripMenuItem m_HardwareVideo = new("Hardware video (H.264, low latency)");
    private readonly WinForms.ToolStripMenuItem m_ViewerOverlayEnabled = new("Enabled");
    private readonly Dictionary<int, WinForms.ToolStripMenuItem> m_FpsItems = new();
    private readonly Dictionary<int, WinForms.ToolStripMenuItem> m_QualityItems = new();
    private TaskbarStatusForm? m_TaskbarStatusForm;
    private AccessCodeManagerForm? m_AccessCodeManager;
    private ActiveViewersForm? m_ActiveViewersForm;
    private CouchDeskSettingsForm? m_SettingsForm;
    private ViewerOverlayForm? m_ViewerOverlay;
    private bool m_OverlayPreviewVisible;
    private bool m_Quitting;

    public TrayAppContext(
        AppConfig config,
        RemoteDesktopHost host,
        RemoteAccessController remote,
        NetworkInfoService network,
        FirewallRuleService firewall)
    {
        m_Config = config;
        m_Host = host;
        m_Remote = remote;
        m_Network = network;
        m_Firewall = firewall;

        BuildMenu();
        m_Host.Sessions.ClientJoined += OnClientJoined;
        m_Menu.Opening += (_, _) => UpdateMenu();
        m_MenuHost.Show();
        m_NotifyIcon = new WinForms.NotifyIcon
        {
            Icon = GetTrayIcon(),
            Text = ProductInfo.Name,
            ContextMenuStrip = m_Menu,
            Visible = true
        };
        m_NotifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == WinForms.MouseButtons.Left)
            {
                if (m_TaskbarStatusForm is not null)
                    m_TaskbarStatusForm.RestoreAndActivate();
                else
                    OpenUrl(m_Host.LanUrl);
            }
        };
        m_NotifyIcon.BalloonTipClicked += (_, _) => OpenUrl(m_Host.LanUrl);
        m_StartupNotificationTimer.Tick += (_, _) =>
        {
            m_StartupNotificationTimer.Stop();
            ShowStartupNotification();
        };
        m_ActivityRefreshTimer.Tick += (_, _) => RefreshActivityStatus();
        m_OverlayPreviewTimer.Tick += (_, _) =>
        {
            m_OverlayPreviewTimer.Stop();
            m_OverlayPreviewVisible = false;
            UpdateViewerOverlay();
        };
        UpdateMenu();
        SetTaskbarButtonVisible(m_Config.ShowTaskbarButton);
        UpdateViewerOverlay();
        m_StartupNotificationTimer.Start();
        m_ActivityRefreshTimer.Start();
        if (!m_Config.StartMinimized) OpenUrl(m_Host.LanUrl);
    }

    private void BuildMenu()
    {
        var title = new WinForms.ToolStripMenuItem(ProductInfo.Name)
        {
            Enabled = false,
            Font = new System.Drawing.Font(m_Menu.Font, System.Drawing.FontStyle.Bold)
        };
        m_HostNameStatus.Enabled = false;
        m_LanStatus.Enabled = false;
        m_RemoteStatus.Enabled = false;

        var openLan = new WinForms.ToolStripMenuItem("Open LAN Dashboard");
        openLan.Click += (_, _) => OpenUrl(m_Host.LanUrl);
        var copyLan = new WinForms.ToolStripMenuItem("Copy LAN URL");
        copyLan.Click += (_, _) => CopyText(m_Host.LanUrl);
        m_ActiveViewers.Click += (_, _) => ShowActiveViewers();
        var renameHost = new WinForms.ToolStripMenuItem("Rename This PC...");
        renameHost.Click += (_, _) => RenameHost();
        var settings = new WinForms.ToolStripMenuItem("Settings...");
        settings.Click += (_, _) => ShowSettings();

        var remoteMenu = new WinForms.ToolStripMenuItem("Remote Access");
        m_OpenRemote.Click += async (_, _) => await SafeAsync(OpenRemoteAsync);
        m_CloseRemote.Click += async (_, _) => await SafeAsync(CloseRemoteAsync);
        m_CopyRemoteUrl.Click += (_, _) => CopyText(m_Remote.RemoteUrl ?? m_Config.LastRemoteUrl);
        var checkRemote = new WinForms.ToolStripMenuItem("Check Remote Access");
        checkRemote.Click += (_, _) =>
        {
            var result = m_Remote.Check(m_Host.IsRunning);
            UpdateMenu();
            PromptDialogs.ShowInfo(result.Message);
        };
        remoteMenu.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            m_OpenRemote, m_CloseRemote, m_CopyRemoteUrl, checkRemote
        });

        var accessMode = new WinForms.ToolStripMenuItem("Access Mode");
        m_LanMode.Click += async (_, _) => await SafeAsync(() => SetModeAsync(RemoteAccessMode.LanOnly));
        m_AutomaticMode.Click += async (_, _) => await SafeAsync(() => SetModeAsync(RemoteAccessMode.Automatic));
        m_ManualMode.Click += async (_, _) => await SafeAsync(() => SetModeAsync(RemoteAccessMode.ManualOnly));
        accessMode.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            m_LanMode, m_AutomaticMode, m_ManualMode
        });

        var generateCode = new WinForms.ToolStripMenuItem("Generate Access Code");
        foreach (GuestAccessLevel accessLevel in Enum.GetValues<GuestAccessLevel>())
        {
            var item = new WinForms.ToolStripMenuItem(accessLevel switch
            {
                GuestAccessLevel.Spectator => "Spectator",
                GuestAccessLevel.Control => "Control",
                _ => "Full Access"
            });
            item.Click += (_, _) => GenerateGuestInvite(accessLevel);
            generateCode.DropDownItems.Add(item);
        }
        var manageCodes = new WinForms.ToolStripMenuItem("Manage Access Codes...");
        manageCodes.Click += (_, _) => ShowAccessCodeManager();
        m_GuestAccess.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            generateCode, manageCodes
        });

        var streaming = new WinForms.ToolStripMenuItem("Streaming");
        var fps = new WinForms.ToolStripMenuItem("FPS Limit");
        foreach (int value in new[] { 15, 30, 60 })
        {
            var item = new WinForms.ToolStripMenuItem($"{value} FPS");
            item.Click += (_, _) => SetFps(value);
            m_FpsItems[value] = item;
            fps.DropDownItems.Add(item);
        }
        var quality = new WinForms.ToolStripMenuItem("Quality");
        foreach (var preset in new[] { ("Low", 55), ("Balanced", 75), ("High", 90) })
        {
            var item = new WinForms.ToolStripMenuItem(preset.Item1);
            item.Click += (_, _) => SetQuality(preset.Item2);
            m_QualityItems[preset.Item2] = item;
            quality.DropDownItems.Add(item);
        }
        m_HardwareVideo.Click += (_, _) =>
        {
            // On = GPU capture + hardware H.264 (WebCodecs low latency); off = JPEG tiles.
            m_Config.UseHardwareVideo = !m_Config.UseHardwareVideo;
            m_Config.Save();
            UpdateMenu();
            // The codec path is chosen when a viewer connects, so drop active viewers to
            // make them reconnect on the new path.
            m_Host.Sessions.DisconnectAll();
        };
        streaming.DropDownItems.Add(fps);
        streaming.DropDownItems.Add(quality);
        streaming.DropDownItems.Add(m_HardwareVideo);

        var security = new WinForms.ToolStripMenuItem("Security");
        var changePassword = new WinForms.ToolStripMenuItem("Change Password");
        changePassword.Click += (_, _) => ChangePassword();
        var lockSessions = new WinForms.ToolStripMenuItem("Lock All Sessions");
        lockSessions.Click += (_, _) =>
        {
            m_Host.Sessions.DisconnectAll();
            m_Host.LoginSessions.RevokeAll();
            m_Host.RevokeAllGuestInvites();
            PromptDialogs.ShowInfo("All streaming and login sessions have been locked.");
        };
        var resetSetup = new WinForms.ToolStripMenuItem("Reset Setup");
        resetSetup.Click += async (_, _) => await SafeAsync(ResetSetupAsync);
        security.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            changePassword, lockSessions, resetSetup
        });

        var startup = new WinForms.ToolStripMenuItem("Startup");
        m_StartWithWindows.Click += (_, _) => ToggleStartWithWindows();
        m_StartMinimized.Click += (_, _) =>
        {
            m_Config.StartMinimized = !m_Config.StartMinimized;
            m_Config.Save();
            UpdateMenu();
        };
        m_ShowTaskbarButton.Click += (_, _) =>
        {
            m_Config.ShowTaskbarButton = !m_Config.ShowTaskbarButton;
            m_Config.Save();
            SetTaskbarButtonVisible(m_Config.ShowTaskbarButton);
            UpdateMenu();
        };
        startup.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            m_StartWithWindows, m_StartMinimized, m_ShowTaskbarButton
        });

        var advanced = new WinForms.ToolStripMenuItem("Advanced");
        var viewerOverlay = new WinForms.ToolStripMenuItem("Viewer Overlay");
        m_ViewerOverlayEnabled.Click += (_, _) =>
        {
            m_Config.ShowViewerOverlay = !m_Config.ShowViewerOverlay;
            m_Config.Save();
            UpdateMenu();
        };
        var previewViewerOverlay = new WinForms.ToolStripMenuItem("Preview for 5 seconds");
        previewViewerOverlay.Click += (_, _) => PreviewViewerOverlay();
        viewerOverlay.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            m_ViewerOverlayEnabled, previewViewerOverlay
        });
        m_OpenRouter.Click += (_, _) => OpenRouter();
        m_CopyForwarding.Click += (_, _) => CopyForwardingValues();
        var ensureLanFirewall = new WinForms.ToolStripMenuItem("Add/Repair LAN Firewall Rule");
        ensureLanFirewall.Click += async (_, _) => await SafeAsync(EnsureLanFirewallRuleAsync);
        var removeFirewall = new WinForms.ToolStripMenuItem("Remove Public Firewall Rule");
        removeFirewall.Click += async (_, _) => await SafeAsync(RemovePublicFirewallRuleAsync);
        var viewLogs = new WinForms.ToolStripMenuItem("View Logs");
        viewLogs.Click += (_, _) => ViewLogs();
        var openConfig = new WinForms.ToolStripMenuItem("Open Config Folder");
        openConfig.Click += (_, _) => OpenPath(AppPaths.ConfigFolder);
        advanced.DropDownItems.AddRange(new WinForms.ToolStripItem[]
        {
            viewerOverlay, new WinForms.ToolStripSeparator(),
            m_OpenRouter, m_CopyForwarding, ensureLanFirewall, removeFirewall,
            new WinForms.ToolStripSeparator(), viewLogs, openConfig
        });

        var quit = new WinForms.ToolStripMenuItem("Quit");
        quit.Click += async (_, _) => await SafeAsync(QuitAsync);

        m_Menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            title, m_HostNameStatus, m_LanStatus, m_RemoteStatus,
            new WinForms.ToolStripSeparator(), openLan, copyLan, m_ActiveViewers, renameHost, settings,
            new WinForms.ToolStripSeparator(), remoteMenu, m_GuestAccess, accessMode, streaming, security, startup, advanced,
            new WinForms.ToolStripSeparator(), quit
        });
    }

    private void RenameHost()
    {
        string current = HostDisplayName.Get(m_Config);
        string? entered = PromptDialogs.ShowText(
            "Rename This PC",
            "Shown on the CouchDesk login screen",
            current,
            "Leave blank to use the Windows user or PC name automatically.");
        if (entered is null) return;

        m_Config.HostDisplayName = HostDisplayName.NormalizeCustom(entered);
        m_Config.Save();
        UpdateMenu();
        PromptDialogs.ShowInfo($"This PC is shown as \"{HostDisplayName.Get(m_Config)}\".");
    }

    private void ShowSettings()
    {
        if (m_SettingsForm is null || m_SettingsForm.IsDisposed)
        {
            m_SettingsForm = new CouchDeskSettingsForm(
                m_Config,
                m_StatusIcons.AppIcon,
                SetTaskbarButtonVisible,
                UpdateMenu);
            m_SettingsForm.FormClosed += (_, _) => m_SettingsForm = null;
            m_SettingsForm.Show();
        }
        else
        {
            m_SettingsForm.WindowState = WinForms.FormWindowState.Normal;
            m_SettingsForm.Activate();
        }
    }

    private void OnClientJoined(ClientView client)
    {
        if (m_Quitting) return;

        void Notify()
        {
            if (m_Quitting || !m_NotifyIcon.Visible) return;
            UpdateMenu();
            if (!ShouldShowJoinNotification(client)) return;

            string role = client.Role == SessionRole.Guest
                ? $"Guest ({client.GuestAccessLevel?.ToString() ?? "Spectator"})"
                : "Owner";
            m_NotifyIcon.ShowBalloonTip(
                5000,
                "Viewer joined",
                $"{role} connected from {client.ClientIp}.",
                WinForms.ToolTipIcon.Info);
        }

        try
        {
            if (m_MenuHost.IsHandleCreated)
                m_MenuHost.BeginInvoke((Action)Notify);
            else
                Notify();
        }
        catch
        {
            // Notification failed; the stream itself should not care.
        }
    }

    private bool ShouldShowJoinNotification(ClientView client)
    {
        string key = $"{client.ClientIp}|{client.Role}|{client.GuestAccessLevel}";
        DateTime now = DateTime.UtcNow;
        foreach (var stale in m_LastJoinNotificationByClient
                     .Where(entry => now - entry.Value > TimeSpan.FromMinutes(5))
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            m_LastJoinNotificationByClient.Remove(stale);
        }

        if (m_LastJoinNotificationByClient.TryGetValue(key, out var last)
            && now - last < TimeSpan.FromSeconds(60))
            return false;

        m_LastJoinNotificationByClient[key] = now;
        return true;
    }

    private async Task OpenRemoteAsync()
    {
        if (!m_Config.IsConfigured)
        {
            OpenUrl(m_Host.LanUrl);
            PromptDialogs.ShowWarning("Create a password before opening remote access.");
            return;
        }

        if (m_Config.AccessMode is RemoteAccessMode.LanOnly or RemoteAccessMode.Disabled)
        {
            if (!PromptDialogs.Confirm(
                    "Remote access exposes this server beyond your LAN. Switch to Automatic Remote Access and continue?"))
                return;
            m_Config.AccessMode = RemoteAccessMode.Automatic;
            m_Config.Save();
        }
        else if (!PromptDialogs.Confirm(
                     "Open public remote access now? Use only a strong, unique password."))
        {
            return;
        }

        if (!m_Host.IsRunning) await m_Host.StartAsync();
        var result = await m_Remote.OpenAsync();
        UpdateMenu();
        if (result.State == RemoteAccessState.ManualSetupRequired)
            PromptDialogs.ShowWarning(result.Message + "\n\nUse Advanced > Copy Port Forwarding Values.");
        else if (!result.Success)
            PromptDialogs.ShowError(result.Message);
        else
            PromptDialogs.ShowInfo($"Remote access is active.\n\n{result.RemoteUrl}");
    }

    private async Task CloseRemoteAsync()
    {
        await m_Remote.CloseAsync();
        UpdateMenu();
    }

    private async Task SetModeAsync(RemoteAccessMode mode)
    {
        if (m_Config.RemoteAccessEnabled) await m_Remote.CloseAsync();
        m_Config.AccessMode = mode;
        if (mode is RemoteAccessMode.LanOnly or RemoteAccessMode.Disabled)
            m_Config.RemoteAccessEnabled = false;
        m_Config.Save();
        UpdateMenu();
    }

    private void SetFps(int value)
    {
        m_Config.FpsLimit = value;
        m_Config.Save();
        UpdateMenu();
    }

    private void SetQuality(int value)
    {
        m_Config.JpegQuality = value;
        m_Config.Save();
        UpdateMenu();
    }

    private void GenerateGuestInvite(GuestAccessLevel accessLevel)
    {
        if (!m_Config.IsConfigured)
        {
            OpenUrl(m_Host.LanUrl);
            PromptDialogs.ShowWarning("Create the owner password before generating guest access codes.");
            return;
        }

        if (accessLevel == GuestAccessLevel.Full
            && !PromptDialogs.Confirm(
                "Full guest access includes system keys and file transfer. Create this code?"))
            return;

        int minutes = Math.Clamp(m_Config.GuestInviteDefaultMinutes, 5, 7 * 24 * 60);
        var invite = m_Host.CreateGuestInvite(accessLevel, TimeSpan.FromMinutes(minutes));
        CopyText(invite.Code);
        UpdateMenu();
        m_NotifyIcon.ShowBalloonTip(
            5000,
            $"{accessLevel} guest code copied",
            $"{invite.Code}\nExpires {invite.ExpiresUtc.ToLocalTime():g}",
            WinForms.ToolTipIcon.Info);
    }

    private void ShowAccessCodeManager()
    {
        if (m_AccessCodeManager is null || m_AccessCodeManager.IsDisposed)
        {
            m_AccessCodeManager = new AccessCodeManagerForm(m_Host, m_Config);
            m_AccessCodeManager.FormClosed += (_, _) => m_AccessCodeManager = null;
            m_AccessCodeManager.Show();
        }
        else
        {
            m_AccessCodeManager.WindowState = WinForms.FormWindowState.Normal;
            m_AccessCodeManager.Activate();
        }
    }

    private void ShowActiveViewers()
    {
        if (m_ActiveViewersForm is null || m_ActiveViewersForm.IsDisposed)
        {
            m_ActiveViewersForm = new ActiveViewersForm(m_Host, ShowAccessCodeManager);
            m_ActiveViewersForm.FormClosed += (_, _) => m_ActiveViewersForm = null;
            m_ActiveViewersForm.Show();
        }
        else
        {
            m_ActiveViewersForm.WindowState = WinForms.FormWindowState.Normal;
            m_ActiveViewersForm.Activate();
        }
    }

    private void ChangePassword()
    {
        if (!m_Config.IsConfigured)
        {
            OpenUrl(m_Host.LanUrl);
            PromptDialogs.ShowInfo("Complete first-run setup in the LAN dashboard.");
            return;
        }

        var change = PromptDialogs.ShowChangePassword();
        if (change is null) return;
        if (m_Config.PasswordHash is null
            || !PasswordHasher.Verify(change.CurrentPassword, m_Config.PasswordHash))
        {
            PromptDialogs.ShowError("The current password is incorrect.");
            return;
        }

        m_Config.PasswordHash = PasswordHasher.Hash(change.NewPassword);
        m_Config.Save();
        m_Host.Sessions.DisconnectAll();
        m_Host.LoginSessions.RevokeAll();
        m_Host.RevokeAllGuestInvites();
        PromptDialogs.ShowInfo("The password was changed and all sessions were locked.");
    }

    private async Task ResetSetupAsync()
    {
        if (!PromptDialogs.Confirm(
                "Reset setup? This removes the password and locks every active session."))
            return;

        await m_Remote.CloseAsync();
        m_Host.Sessions.DisconnectAll();
        m_Host.LoginSessions.RevokeAll();
        m_Host.RevokeAllGuestInvites();
        m_Config.PasswordHash = null;
        m_Config.AccessMode = RemoteAccessMode.LanOnly;
        m_Config.RemoteAccessEnabled = false;
        m_Config.Save();
        UpdateMenu();
        OpenUrl(m_Host.LanUrl);
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            bool enabled = !m_Config.StartWithWindows;
            StartupRegistrationService.SetEnabled(enabled);
            m_Config.StartWithWindows = enabled;
            m_Config.Save();
            UpdateMenu();
        }
        catch (Exception ex)
        {
            PromptDialogs.ShowError($"The startup entry could not be changed.\n\n{ex.Message}");
        }
    }

    private void OpenRouter()
    {
        string? url = m_Config.LastRouterUrl ?? m_Network.GetRouterUrl();
        if (url is null)
        {
            PromptDialogs.ShowWarning("No IPv4 gateway/router address was detected.");
            return;
        }
        m_Config.LastRouterUrl = url;
        m_Config.Save();
        OpenUrl(url);
    }

    private void CopyForwardingValues()
    {
        string text = $"{ProductInfo.Name} port forwarding:\r\n\r\n" +
            $"Protocol: TCP\r\n" +
            $"External port: {m_Config.ExternalPort}\r\n" +
            $"Internal IP: {m_Host.LanIp}\r\n" +
            $"Internal port: {m_Config.Port}";
        CopyText(text);
    }

    private async Task RemovePublicFirewallRuleAsync()
    {
        var result = await m_Firewall.RemovePublicRuleAsync();
        if (!result.Success) PromptDialogs.ShowError(result.Message);
    }

    private async Task EnsureLanFirewallRuleAsync()
    {
        var result = await m_Firewall.EnsureLanRuleAsync(m_Config.Port);
        if (result.Success) PromptDialogs.ShowInfo("The LAN firewall rule is ready.");
        else PromptDialogs.ShowError(result.Message);
    }

    private static void ViewLogs()
    {
        if (!File.Exists(AppPaths.AuditLog)) File.WriteAllText(AppPaths.AuditLog, "");
        OpenPath(AppPaths.AuditLog);
    }

    private async Task QuitAsync()
    {
        if (m_Quitting) return;
        m_Quitting = true;
        try
        {
            await m_Remote.CloseAsync();
            await m_Host.StopAsync();
            m_NotifyIcon.Visible = false;
            ExitThread();
        }
        catch
        {
            m_Quitting = false;
            throw;
        }
    }

    private void UpdateMenu()
    {
        m_HostNameStatus.Text = $"This PC: {HostDisplayName.Get(m_Config)}";
        m_LanStatus.Text = m_Host.IsRunning
            ? $"LAN Access: Active ({m_Host.LanIp}:{m_Host.Port})"
            : "LAN Access: Stopped";
        m_LanStatus.Image = m_Host.IsRunning
            ? m_StatusIcons.GreenDot
            : m_StatusIcons.RedDot;
        m_RemoteStatus.Text = m_Remote.State switch
        {
            RemoteAccessState.Active => "Remote Access: Active",
            RemoteAccessState.Opening => "Remote Access: Opening",
            RemoteAccessState.ManualSetupRequired => "Remote Access: Manual setup required",
            RemoteAccessState.Failed => "Remote Access: Failed",
            _ => "Remote Access: Closed"
        };
        m_RemoteStatus.Image = m_Remote.State switch
        {
            RemoteAccessState.Active => m_StatusIcons.GreenDot,
            RemoteAccessState.Opening => m_StatusIcons.YellowDot,
            RemoteAccessState.ManualSetupRequired => m_StatusIcons.YellowDot,
            RemoteAccessState.Failed => m_StatusIcons.RedDot,
            _ => m_StatusIcons.GrayDot
        };
        m_NotifyIcon.Icon = GetTrayIcon();
        m_NotifyIcon.Text = GetTrayToolTip();
        m_OpenRemote.Enabled = m_Remote.State is not RemoteAccessState.Opening and not RemoteAccessState.Active;
        m_CloseRemote.Enabled = m_Config.RemoteAccessEnabled || m_Remote.State != RemoteAccessState.Closed;
        m_CopyRemoteUrl.Enabled = !string.IsNullOrWhiteSpace(m_Remote.RemoteUrl ?? m_Config.LastRemoteUrl);
        int activeInvites = m_Host.GuestInvites.Snapshot().Count(invite => invite.IsActive);
        m_GuestAccess.Text = activeInvites == 0 ? "Guest Access" : $"Guest Access ({activeInvites})";
        m_GuestAccess.Enabled = m_Config.IsConfigured;
        int sessions = m_Host.Sessions.Count;
        m_ActiveViewers.Text = sessions == 0 ? "Active Viewers..." : $"Active Viewers ({sessions})...";

        m_LanMode.Checked = m_Config.AccessMode == RemoteAccessMode.LanOnly;
        m_AutomaticMode.Checked = m_Config.AccessMode == RemoteAccessMode.Automatic;
        m_ManualMode.Checked = m_Config.AccessMode == RemoteAccessMode.ManualOnly;
        foreach (var item in m_FpsItems) item.Value.Checked = m_Config.FpsLimit == item.Key;
        foreach (var item in m_QualityItems) item.Value.Checked = m_Config.JpegQuality == item.Key;
        m_StartWithWindows.Checked = m_Config.StartWithWindows;
        m_StartMinimized.Checked = m_Config.StartMinimized;
        m_ShowTaskbarButton.Checked = m_Config.ShowTaskbarButton;
        m_HardwareVideo.Checked = m_Config.UseHardwareVideo;
        m_ViewerOverlayEnabled.Checked = m_Config.ShowViewerOverlay;

        string? router = m_Config.LastRouterUrl ?? m_Network.GetRouterUrl();
        m_OpenRouter.Enabled = router is not null;
        m_CopyForwarding.Enabled = !m_Host.LanIp.Equals(System.Net.IPAddress.Loopback);
        UpdateViewerOverlay();
        m_TaskbarStatusForm?.UpdateStatus();
    }

    private void UpdateViewerOverlay()
    {
        if (m_OverlayPreviewVisible) return;

        int sessions = m_Host.Sessions.Count;
        if (!m_Config.ShowViewerOverlay || sessions == 0)
        {
            m_ViewerOverlay?.UpdateViewers(Array.Empty<ClientView>(), m_Host.PointerInput.Snapshot());
            return;
        }

        if (m_ViewerOverlay is null || m_ViewerOverlay.IsDisposed)
            m_ViewerOverlay = new ViewerOverlayForm();
        m_ViewerOverlay.UpdateViewers(m_Host.Sessions.Snapshot(), m_Host.PointerInput.Snapshot());
    }

    private void PreviewViewerOverlay()
    {
        if (m_ViewerOverlay is null || m_ViewerOverlay.IsDisposed)
            m_ViewerOverlay = new ViewerOverlayForm();

        m_OverlayPreviewVisible = true;
        m_ViewerOverlay.ShowPreview();
        m_OverlayPreviewTimer.Stop();
        m_OverlayPreviewTimer.Start();
    }

    private void SetTaskbarButtonVisible(bool visible)
    {
        if (visible)
        {
            if (m_TaskbarStatusForm is not null) return;
            m_TaskbarStatusForm = new TaskbarStatusForm(
                m_Host,
                m_Remote,
                m_StatusIcons,
                () => OpenUrl(m_Host.LanUrl),
                ShowTrayMenu);
            m_TaskbarStatusForm.Show();
            m_TaskbarStatusForm.WindowState = WinForms.FormWindowState.Minimized;
            return;
        }

        if (m_TaskbarStatusForm is null) return;
        m_TaskbarStatusForm.ClosePermanently();
        m_TaskbarStatusForm.Dispose();
        m_TaskbarStatusForm = null;
    }

    private void ShowTrayMenu()
    {
        // Invoke the exact WinForms path used by NotifyIcon's native right-click.
        // This preserves its placement, foreground ownership, and click-away
        // dismissal. Keep a public-API fallback for future framework changes.
        if (ShowContextMenuMethod is not null)
        {
            try
            {
                ShowContextMenuMethod.Invoke(m_NotifyIcon, null);
                return;
            }
            catch (TargetInvocationException)
            {
                // Fall through to the compatible manual path.
            }
        }

        SetForegroundWindow(m_MenuHost.Handle);
        m_Menu.Show(WinForms.Cursor.Position);
        m_Menu.Focus();
    }

    private System.Drawing.Icon GetTrayIcon()
    {
        return m_Host.Sessions.Count > 0
            ? m_StatusIcons.AccessOnTrayIcon
            : m_StatusIcons.IdleTrayIcon;
    }

    private void RefreshActivityStatus()
    {
        var icon = GetTrayIcon();
        if (!ReferenceEquals(m_NotifyIcon.Icon, icon)) m_NotifyIcon.Icon = icon;
        m_NotifyIcon.Text = GetTrayToolTip();
        UpdateViewerOverlay();
        m_TaskbarStatusForm?.UpdateStatus();
    }

    private string GetTrayToolTip()
    {
        string remote = m_Remote.State switch
        {
            RemoteAccessState.Active => "active",
            RemoteAccessState.Opening => "opening",
            RemoteAccessState.ManualSetupRequired => "needs setup",
            RemoteAccessState.Failed => "failed",
            _ => "closed"
        };
        int sessions = m_Host.Sessions.Count;
        string viewers = sessions == 0 ? "no active session" : $"{sessions} active session{(sessions == 1 ? "" : "s")}";
        return $"{ProductInfo.Name} - LAN {(m_Host.IsRunning ? "active" : "stopped")}, remote {remote}, {viewers}";
    }

    private void ShowStartupNotification()
    {
        string message = m_Config.IsConfigured
            ? $"LAN access is active at {m_Host.LanUrl}\nLeft-click for the dashboard; right-click for controls."
            : $"{ProductInfo.Name} is running. Left-click the tray icon to finish setup in the LAN dashboard.";
        m_NotifyIcon.ShowBalloonTip(
            5000,
            $"{ProductInfo.Name} started",
            message,
            WinForms.ToolTipIcon.Info);
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { PromptDialogs.ShowError(ex.Message); }
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { WinForms.Clipboard.SetText(text); }
        catch (Exception ex) { PromptDialogs.ShowError($"Could not copy to the clipboard.\n\n{ex.Message}"); }
    }

    private static void OpenUrl(string url) => OpenPath(url);

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PromptDialogs.ShowError($"Could not open this item.\n\n{ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            m_Host.Sessions.ClientJoined -= OnClientJoined;
            SetTaskbarButtonVisible(false);
            m_SettingsForm?.Close();
            m_SettingsForm?.Dispose();
            m_ViewerOverlay?.Close();
            m_ViewerOverlay?.Dispose();
            m_AccessCodeManager?.Close();
            m_AccessCodeManager?.Dispose();
            m_ActiveViewersForm?.Close();
            m_ActiveViewersForm?.Dispose();
            m_MenuHost.Close();
            m_MenuHost.Dispose();
            m_StartupNotificationTimer.Stop();
            m_StartupNotificationTimer.Dispose();
            m_ActivityRefreshTimer.Stop();
            m_OverlayPreviewTimer.Stop();
            m_ActivityRefreshTimer.Dispose();
            m_NotifyIcon.Visible = false;
            m_NotifyIcon.Dispose();
            m_Menu.Dispose();
            m_StatusIcons.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
