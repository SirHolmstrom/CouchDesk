using Core.Hosting;
using Core.Branding;
using Core.RemoteAccess;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

/// <summary>
/// Optional compact window for users who prefer a normal taskbar button. It starts
/// minimized and keeps the full application controlled from the tray.
/// </summary>
internal sealed class TaskbarStatusForm : WinForms.Form
{
    private static readonly System.Drawing.Color BrandOrange = System.Drawing.Color.FromArgb(255, 122, 26);
    private static readonly System.Drawing.Color BrandInk = System.Drawing.Color.FromArgb(33, 16, 6);
    private readonly RemoteDesktopHost m_Host;
    private readonly RemoteAccessController m_Remote;
    private readonly TrayStatusIcons m_Icons;
    private readonly WinForms.Label m_LanDot = CreateDotLabel();
    private readonly WinForms.Label m_LanText = CreateStatusLabel();
    private readonly WinForms.Label m_RemoteDot = CreateDotLabel();
    private readonly WinForms.Label m_RemoteText = CreateStatusLabel();
    private bool m_AllowClose;

    public TaskbarStatusForm(
        RemoteDesktopHost host,
        RemoteAccessController remote,
        TrayStatusIcons icons,
        Action openDashboard,
        Action showTrayMenu)
    {
        m_Host = host;
        m_Remote = remote;
        m_Icons = icons;

        Text = ProductInfo.Name;
        Width = 390;
        Height = 205;
        FormBorderStyle = WinForms.FormBorderStyle.FixedSingle;
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        WindowState = WinForms.FormWindowState.Minimized;
        Icon = m_Icons.AppIcon;

        var heading = new WinForms.Label
        {
            Text = ProductInfo.Name,
            Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
            ForeColor = BrandOrange,
            AutoSize = true,
            Left = 18,
            Top = 16
        };
        var hint = new WinForms.Label
        {
            Text = "Runs in the background; closing this panel minimizes it.",
            ForeColor = System.Drawing.SystemColors.GrayText,
            AutoSize = true,
            Left = 18,
            Top = 42
        };

        PositionStatusRow(m_LanDot, m_LanText, 68);
        PositionStatusRow(m_RemoteDot, m_RemoteText, 96);

        var dashboard = new WinForms.Button
        {
            Text = "Open Dashboard",
            Left = 18,
            Top = 130,
            Width = 150,
            Height = 30
        };
        dashboard.Click += (_, _) => openDashboard();
        StylePrimaryButton(dashboard);
        var controls = new WinForms.Button
        {
            Text = "Tray Controls",
            Left = 180,
            Top = 130,
            Width = 150,
            Height = 30
        };
        controls.Click += (_, _) => showTrayMenu();
        controls.FlatStyle = WinForms.FlatStyle.Flat;
        controls.FlatAppearance.BorderColor = BrandOrange;

        Controls.AddRange(new WinForms.Control[]
        {
            heading, hint, m_LanDot, m_LanText, m_RemoteDot, m_RemoteText,
            dashboard, controls
        });
        UpdateStatus();
    }

    protected override bool ShowWithoutActivation => true;

    public void UpdateStatus()
    {
        m_LanDot.ForeColor = m_Host.IsRunning
            ? System.Drawing.Color.FromArgb(34, 197, 94)
            : System.Drawing.Color.FromArgb(239, 68, 68);
        m_LanText.Text = m_Host.IsRunning
            ? $"LAN access active at {m_Host.LanIp}:{m_Host.Port}"
            : "LAN access stopped";

        (m_RemoteDot.ForeColor, m_RemoteText.Text) = m_Remote.State switch
        {
            RemoteAccessState.Active =>
                (System.Drawing.Color.FromArgb(34, 197, 94), "Remote access active"),
            RemoteAccessState.Opening =>
                (System.Drawing.Color.FromArgb(234, 179, 8), "Remote access opening"),
            RemoteAccessState.ManualSetupRequired =>
                (System.Drawing.Color.FromArgb(234, 179, 8), "Remote access needs setup"),
            RemoteAccessState.Failed =>
                (System.Drawing.Color.FromArgb(239, 68, 68), "Remote access failed"),
            _ =>
                (System.Drawing.Color.FromArgb(148, 163, 184), "Remote access closed")
        };
    }

    public void ClosePermanently()
    {
        m_AllowClose = true;
        Close();
    }

    public void RestoreAndActivate()
    {
        WindowState = WinForms.FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(WinForms.FormClosingEventArgs eventArgs)
    {
        if (!m_AllowClose && eventArgs.CloseReason == WinForms.CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            WindowState = WinForms.FormWindowState.Minimized;
            return;
        }
        base.OnFormClosing(eventArgs);
    }

    private static WinForms.Label CreateDotLabel() => new()
    {
        Text = "●",
        Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold),
        AutoSize = true
    };

    private static WinForms.Label CreateStatusLabel() => new()
    {
        AutoSize = true
    };

    private static void PositionStatusRow(
        WinForms.Label dot,
        WinForms.Label text,
        int top)
    {
        dot.Left = 18;
        dot.Top = top - 5;
        text.Left = 45;
        text.Top = top;
    }

    private static void StylePrimaryButton(WinForms.Button button)
    {
        button.FlatStyle = WinForms.FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = BrandOrange;
        button.ForeColor = BrandInk;
        button.Font = new System.Drawing.Font(button.Font, System.Drawing.FontStyle.Bold);
    }
}
