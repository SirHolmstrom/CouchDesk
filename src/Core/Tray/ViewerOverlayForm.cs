using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Core.Security;
using Core.Streaming;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

internal sealed class ViewerOverlayForm : WinForms.Form
{
    private static readonly Color Surface = Color.FromArgb(215, 18, 18, 20);
    private static readonly Color Border = Color.FromArgb(95, 255, 255, 255);
    private static readonly Color Green = Color.FromArgb(34, 197, 94);
    private static readonly Color Orange = Color.FromArgb(255, 122, 26);
    private static readonly Color Gray = Color.FromArgb(110, 110, 116);
    private static readonly Color TextColor = Color.FromArgb(255, 250, 245);
    private static readonly Color Muted = Color.FromArgb(185, 178, 172);
    private IReadOnlyList<ClientView> m_Viewers = Array.Empty<ClientView>();
    private PointerControlView m_Pointer = new("idle", null, 0, 0);

    public ViewerOverlayForm()
    {
        Width = 270;
        Height = 44;
        FormBorderStyle = WinForms.FormBorderStyle.None;
        StartPosition = WinForms.FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = 0.94;
        BackColor = Color.Black;
        ForeColor = TextColor;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            createParams.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            createParams.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
            return createParams;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        TryExcludeFromCapture();
    }

    public void UpdateViewers(IReadOnlyList<ClientView> viewers, PointerControlView pointer)
    {
        m_Viewers = viewers;
        m_Pointer = pointer;
        if (m_Viewers.Count == 0)
        {
            Hide();
            return;
        }

        int rows = Math.Min(4, m_Viewers.Count);
        Height = 33 + rows * 24 + (m_Viewers.Count > rows ? 18 : 8);
        PositionNearTaskbar();
        if (!Visible) Show();
        EnsureVisibleWithoutActivation();
        Invalidate();
    }

    public void ShowPreview()
    {
        UpdateViewers(
            new[]
            {
                new ClientView(
                    "overlay-preview",
                    "preview",
                    DateTime.UtcNow,
                    30,
                    75,
                    0,
                    SessionRole.Owner,
                    null,
                    null)
            },
            new PointerControlView("remote", "overlay-preview", 0, 0));
    }

    private void PositionNearTaskbar()
    {
        Rectangle area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        Left = area.Right - Width - 18;
        Top = area.Top + 18;
    }

    private void EnsureVisibleWithoutActivation()
    {
        SetWindowPos(
            Handle,
            HwndTopmost,
            Left,
            Top,
            Width,
            Height,
            SwpNoActivate | SwpShowWindow);
    }

    protected override void OnPaint(WinForms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var background = new SolidBrush(Surface);
        using var border = new Pen(Border);
        using var path = RoundedRect(ClientRectangle with { Width = Width - 1, Height = Height - 1 }, 15);
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);

        using var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var rowFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);
        using var textBrush = new SolidBrush(TextColor);
        using var mutedBrush = new SolidBrush(Muted);

        using var headerDot = new SolidBrush(HeaderColor());
        graphics.FillEllipse(headerDot, 12, 12, 10, 10);
        graphics.DrawString(
            $"{m_Viewers.Count} viewer{(m_Viewers.Count == 1 ? "" : "s")}",
            titleFont,
            textBrush,
            30,
            7);
        graphics.DrawString(PointerSummary(), smallFont, mutedBrush, 122, 9);

        int y = 31;
        foreach (var viewer in m_Viewers.Take(4))
        {
            bool steering = m_Pointer.Source == "remote" && m_Pointer.RemoteOwnerId == viewer.Id;
            using var dot = new SolidBrush(steering ? Orange : Gray);
            graphics.FillEllipse(dot, 14, y + 6, 8, 8);

            string label = $"{RoleText(viewer)} · {viewer.ClientIp}";
            string state = steering ? "controlling" : viewer.GuestAccessLevel == GuestAccessLevel.Spectator ? "watching" : "connected";
            using var stateBrush = new SolidBrush(steering ? Orange : Muted);
            graphics.DrawString(label, rowFont, textBrush, 30, y);
            graphics.DrawString(state, smallFont, stateBrush, 198, y + 1);
            y += 24;
        }

        if (m_Viewers.Count > 4)
            graphics.DrawString($"+{m_Viewers.Count - 4} more", smallFont, mutedBrush, 30, y - 2);
    }

    private Color HeaderColor() => m_Pointer.Source switch
    {
        "remote" => Green,
        "host" => Orange,
        _ => Gray
    };

    private string PointerSummary() => m_Pointer.Source switch
    {
        "remote" => "remote control active",
        "host" => "host mouse active",
        _ => "watching"
    };

    private static string RoleText(ClientView viewer) =>
        viewer.Role == SessionRole.Owner
            ? "Owner"
            : viewer.GuestAccessLevel?.ToString() ?? "Spectator";

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void TryExcludeFromCapture()
    {
        try
        {
            SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        }
        catch
        {
            // Older Windows builds may not support this. The overlay remains useful
            // locally, just without a capture-exclusion guarantee.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
}
