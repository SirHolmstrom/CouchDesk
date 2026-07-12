using Core.Hosting;
using Core.Security;
using Core.Streaming;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

internal sealed class ActiveViewersForm : WinForms.Form
{
    private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(24, 24, 26);
    private static readonly System.Drawing.Color Raised = System.Drawing.Color.FromArgb(35, 35, 38);
    private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(57, 57, 61);
    private static readonly System.Drawing.Color TextColor = System.Drawing.Color.FromArgb(255, 250, 245);
    private static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(170, 163, 159);

    private readonly RemoteDesktopHost m_Host;
    private readonly Action m_OpenAccessCodes;
    private readonly WinForms.DataGridView m_Grid = new();
    private readonly WinForms.Label m_Summary = new();
    private readonly WinForms.Timer m_RefreshTimer = new() { Interval = 1000 };

    public ActiveViewersForm(RemoteDesktopHost host, Action openAccessCodes)
    {
        m_Host = host;
        m_OpenAccessCodes = openAccessCodes;

        Text = "Active Viewers";
        Width = 840;
        Height = 450;
        MinimumSize = new System.Drawing.Size(720, 360);
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BackColor = Surface;
        ForeColor = TextColor;

        BuildLayout();
        RefreshRows();
        m_RefreshTimer.Tick += (_, _) => RefreshRows();
        m_RefreshTimer.Start();
        FormClosed += (_, _) => m_RefreshTimer.Stop();
    }

    private void BuildLayout()
    {
        var top = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Top,
            Height = 44,
            Padding = new WinForms.Padding(12, 11, 12, 8),
            BackColor = Surface
        };
        m_Summary.Dock = WinForms.DockStyle.Fill;
        m_Summary.ForeColor = Muted;
        top.Controls.Add(m_Summary);

        m_Grid.Dock = WinForms.DockStyle.Fill;
        m_Grid.ReadOnly = true;
        m_Grid.AllowUserToAddRows = false;
        m_Grid.AllowUserToDeleteRows = false;
        m_Grid.AllowUserToResizeRows = false;
        m_Grid.MultiSelect = false;
        m_Grid.SelectionMode = WinForms.DataGridViewSelectionMode.FullRowSelect;
        m_Grid.AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.Fill;
        m_Grid.RowHeadersVisible = false;
        m_Grid.BackgroundColor = Surface;
        m_Grid.BorderStyle = WinForms.BorderStyle.None;
        m_Grid.GridColor = Border;
        m_Grid.EnableHeadersVisualStyles = false;
        m_Grid.ColumnHeadersDefaultCellStyle.BackColor = Raised;
        m_Grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
        m_Grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Raised;
        m_Grid.DefaultCellStyle.BackColor = Surface;
        m_Grid.DefaultCellStyle.ForeColor = TextColor;
        m_Grid.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(92, 47, 17);
        m_Grid.DefaultCellStyle.SelectionForeColor = TextColor;
        m_Grid.Columns.Add("Client", "Client");
        m_Grid.Columns.Add("Role", "Role");
        m_Grid.Columns.Add("Access", "Access");
        m_Grid.Columns.Add("Steering", "Steering");
        m_Grid.Columns.Add("Stream", "Stream");
        m_Grid.Columns.Add("Monitor", "Monitor");
        m_Grid.Columns.Add("Connected", "Connected");
        m_Grid.Columns.Add("Code", "Code");

        var actions = new WinForms.FlowLayoutPanel
        {
            Dock = WinForms.DockStyle.Bottom,
            Height = 54,
            Padding = new WinForms.Padding(10, 9, 10, 8),
            FlowDirection = WinForms.FlowDirection.LeftToRight,
            BackColor = Surface
        };
        var disconnect = new WinForms.Button { Text = "Disconnect Selected", AutoSize = true };
        StyleSecondaryButton(disconnect);
        disconnect.Click += (_, _) => DisconnectSelected();
        var disconnectAll = new WinForms.Button { Text = "Disconnect All", AutoSize = true };
        StyleSecondaryButton(disconnectAll);
        disconnectAll.Click += (_, _) => DisconnectAll();
        var revoke = new WinForms.Button { Text = "Revoke Guest Code", AutoSize = true };
        StyleSecondaryButton(revoke);
        revoke.Click += (_, _) => RevokeSelectedCode();
        var codes = new WinForms.Button { Text = "Access Codes...", AutoSize = true };
        StyleSecondaryButton(codes);
        codes.Click += (_, _) => m_OpenAccessCodes();
        var refresh = new WinForms.Button { Text = "Refresh", AutoSize = true };
        StyleSecondaryButton(refresh);
        refresh.Click += (_, _) => RefreshRows();
        var close = new WinForms.Button { Text = "Close", AutoSize = true };
        StyleSecondaryButton(close);
        close.Click += (_, _) => Close();
        actions.Controls.AddRange(new WinForms.Control[]
        {
            disconnect, disconnectAll, revoke, codes, refresh, close
        });

        Controls.Add(m_Grid);
        Controls.Add(actions);
        Controls.Add(top);
    }

    private void RefreshRows()
    {
        string? selectedId = SelectedClient()?.Id;
        var clients = m_Host.Sessions.Snapshot()
            .OrderBy(client => client.ConnectedUtc)
            .ToList();
        var pointer = m_Host.PointerInput.Snapshot();
        var invites = m_Host.GuestInvites.Snapshot().ToDictionary(invite => invite.Id);

        m_Summary.Text = clients.Count == 0
            ? "No active viewers."
            : $"{clients.Count} active viewer{(clients.Count == 1 ? "" : "s")} · {PointerSummary(pointer)}";

        m_Grid.Rows.Clear();
        foreach (var client in clients)
        {
            invites.TryGetValue(client.GuestInviteId ?? Guid.Empty, out var invite);
            int rowIndex = m_Grid.Rows.Add(
                client.ClientIp,
                RoleText(client),
                AccessText(client),
                SteeringText(client, pointer),
                $"{client.Fps} fps · Q{client.Quality}",
                $"#{client.Monitor}",
                ConnectedText(client.ConnectedUtc),
                invite?.Code ?? "-");

            var row = m_Grid.Rows[rowIndex];
            row.Tag = client;
            if (pointer.Source == "remote" && pointer.RemoteOwnerId == client.Id)
                row.DefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(255, 190, 120);
            else if (client.Role == SessionRole.Guest && client.GuestAccessLevel == GuestAccessLevel.Spectator)
                row.DefaultCellStyle.ForeColor = Muted;

            if (selectedId == client.Id) row.Selected = true;
        }
    }

    private ClientView? SelectedClient() =>
        m_Grid.SelectedRows.Count == 1 ? m_Grid.SelectedRows[0].Tag as ClientView : null;

    private void DisconnectSelected()
    {
        var client = SelectedClient();
        if (client is null) return;
        m_Host.Sessions.Disconnect(client.Id);
        RefreshRows();
    }

    private void DisconnectAll()
    {
        if (m_Host.Sessions.Count == 0) return;
        if (!PromptDialogs.Confirm("Disconnect every active viewer?")) return;
        m_Host.Sessions.DisconnectAll();
        RefreshRows();
    }

    private void RevokeSelectedCode()
    {
        var client = SelectedClient();
        if (client?.GuestInviteId is not Guid inviteId) return;
        if (!PromptDialogs.Confirm("Revoke this guest code and disconnect sessions using it?")) return;
        m_Host.RevokeGuestInvite(inviteId);
        RefreshRows();
    }

    private static string RoleText(ClientView client) =>
        client.Role == SessionRole.Owner ? "Owner" : "Guest";

    private static string AccessText(ClientView client) =>
        client.Role == SessionRole.Owner
            ? "Full"
            : client.GuestAccessLevel?.ToString() ?? "Spectator";

    private static string SteeringText(ClientView client, PointerControlView pointer)
    {
        if (pointer.Source == "host") return "Host active";
        if (pointer.Source == "remote")
            return pointer.RemoteOwnerId == client.Id
                ? "Steering"
                : pointer.RemoteIdleMs < 2000 ? "Waiting" : "Can take over";
        return client.GuestAccessLevel == GuestAccessLevel.Spectator ? "Spectating" : "Idle";
    }

    private static string PointerSummary(PointerControlView pointer) =>
        pointer.Source switch
        {
            "host" => $"host has cursor ({Math.Ceiling(pointer.HostBlockMs / 1000.0):0}s)",
            "remote" => "remote steering",
            _ => "cursor idle"
        };

    private static string ConnectedText(DateTime connectedUtc)
    {
        var age = DateTime.UtcNow - connectedUtc;
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalMinutes >= 1) return $"{(int)age.TotalMinutes}m {age.Seconds}s";
        return $"{Math.Max(0, age.Seconds)}s";
    }

    private static void StyleSecondaryButton(WinForms.Button button)
    {
        button.FlatStyle = WinForms.FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Raised;
        button.ForeColor = TextColor;
    }
}
