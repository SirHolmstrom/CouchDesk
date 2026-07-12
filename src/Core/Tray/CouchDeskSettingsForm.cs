using Core.Branding;
using Core.Config;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

internal sealed class CouchDeskSettingsForm : WinForms.Form
{
    private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(24, 24, 26);
    private static readonly System.Drawing.Color Raised = System.Drawing.Color.FromArgb(35, 35, 38);
    private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(57, 57, 61);
    private static readonly System.Drawing.Color TextColor = System.Drawing.Color.FromArgb(255, 250, 245);
    private static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(170, 163, 159);
    private static readonly System.Drawing.Color BrandOrange = System.Drawing.Color.FromArgb(255, 122, 26);
    private static readonly System.Drawing.Color BrandInk = System.Drawing.Color.FromArgb(33, 16, 6);

    private readonly AppConfig m_Config;
    private readonly Action<bool> m_SetTaskbarButtonVisible;
    private readonly Action m_RefreshMenu;
    private readonly WinForms.TextBox m_DisplayName = new();
    private readonly WinForms.Label m_Preview = new();
    private readonly WinForms.CheckBox m_StartWithWindows = new();
    private readonly WinForms.CheckBox m_StartMinimized = new();
    private readonly WinForms.CheckBox m_ShowTaskbarButton = new();
    private readonly WinForms.CheckBox m_ShowViewerOverlay = new();

    public CouchDeskSettingsForm(
        AppConfig config,
        System.Drawing.Icon appIcon,
        Action<bool> setTaskbarButtonVisible,
        Action refreshMenu)
    {
        m_Config = config;
        m_SetTaskbarButtonVisible = setTaskbarButtonVisible;
        m_RefreshMenu = refreshMenu;

        Text = $"{ProductInfo.Name} Settings";
        Width = 520;
        Height = 376;
        MinimumSize = new System.Drawing.Size(480, 356);
        FormBorderStyle = WinForms.FormBorderStyle.FixedSingle;
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Surface;
        ForeColor = TextColor;
        Icon = appIcon;

        BuildLayout();
        LoadValues();
    }

    private void BuildLayout()
    {
        var heading = new WinForms.Label
        {
            Text = $"{ProductInfo.Name} Settings",
            Font = new System.Drawing.Font(Font.FontFamily, 15f, System.Drawing.FontStyle.Bold),
            ForeColor = BrandOrange,
            AutoSize = true,
            Left = 22,
            Top = 18
        };
        var subtitle = new WinForms.Label
        {
            Text = "Friendly name and startup behavior.",
            ForeColor = Muted,
            AutoSize = true,
            Left = 23,
            Top = 47
        };

        var nameLabel = Label("This PC name", 24, 86);
        m_DisplayName.Left = 24;
        m_DisplayName.Top = 110;
        m_DisplayName.Width = 325;
        m_DisplayName.MaxLength = 64;
        StyleTextBox(m_DisplayName);
        m_DisplayName.TextChanged += (_, _) => UpdatePreview();

        var automatic = new WinForms.Button
        {
            Text = "Use automatic",
            Left = 362,
            Top = 108,
            Width = 120,
            Height = 29
        };
        StyleSecondaryButton(automatic);
        automatic.Click += (_, _) =>
        {
            m_DisplayName.Text = "";
            m_DisplayName.Focus();
        };

        m_Preview.Left = 24;
        m_Preview.Top = 143;
        m_Preview.Width = 458;
        m_Preview.Height = 22;
        m_Preview.ForeColor = Muted;

        var section = Label("Windows behavior", 24, 184);
        section.ForeColor = TextColor;
        section.Font = new System.Drawing.Font(section.Font, System.Drawing.FontStyle.Bold);

        ConfigureCheckBox(m_StartWithWindows, "Start CouchDesk with Windows", 24, 214);
        ConfigureCheckBox(m_StartMinimized, "Start in the background", 24, 240);
        ConfigureCheckBox(m_ShowTaskbarButton, "Show a taskbar status button", 24, 266);
        ConfigureCheckBox(m_ShowViewerOverlay, "Show active viewer overlay", 24, 292);

        var save = new WinForms.Button
        {
            Text = "Save",
            Left = 322,
            Top = 300,
            Width = 76,
            Height = 30
        };
        StylePrimaryButton(save);
        save.Click += (_, _) => Save();

        var cancel = new WinForms.Button
        {
            Text = "Cancel",
            Left = 406,
            Top = 300,
            Width = 76,
            Height = 30
        };
        StyleSecondaryButton(cancel);
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new WinForms.Control[]
        {
            heading, subtitle, nameLabel, m_DisplayName, automatic, m_Preview,
            section, m_StartWithWindows, m_StartMinimized, m_ShowTaskbarButton, m_ShowViewerOverlay,
            save, cancel
        });
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void LoadValues()
    {
        m_DisplayName.Text = m_Config.HostDisplayName ?? HostDisplayName.Get(m_Config);
        m_StartWithWindows.Checked = m_Config.StartWithWindows;
        m_StartMinimized.Checked = m_Config.StartMinimized;
        m_ShowTaskbarButton.Checked = m_Config.ShowTaskbarButton;
        m_ShowViewerOverlay.Checked = m_Config.ShowViewerOverlay;
        UpdatePreview();
    }

    private void Save()
    {
        try
        {
            StartupRegistrationService.SetEnabled(m_StartWithWindows.Checked);
        }
        catch (Exception ex)
        {
            PromptDialogs.ShowError($"The startup entry could not be changed.\n\n{ex.Message}");
            return;
        }

        m_Config.HostDisplayName = HostDisplayName.NormalizeCustom(m_DisplayName.Text);
        m_Config.StartWithWindows = m_StartWithWindows.Checked;
        m_Config.StartMinimized = m_StartMinimized.Checked;
        m_Config.ShowTaskbarButton = m_ShowTaskbarButton.Checked;
        m_Config.ShowViewerOverlay = m_ShowViewerOverlay.Checked;
        m_Config.Save();

        m_SetTaskbarButtonVisible(m_Config.ShowTaskbarButton);
        m_RefreshMenu();
        Close();
    }

    private void UpdatePreview()
    {
        string name = HostDisplayName.NormalizeCustom(m_DisplayName.Text)
            ?? HostDisplayName.AutomaticDefault;
        m_Preview.Text = $"Login page: Connect to {name}";
    }

    private static WinForms.Label Label(string text, int left, int top) => new()
    {
        Text = text,
        AutoSize = true,
        Left = left,
        Top = top,
        ForeColor = Muted
    };

    private static void ConfigureCheckBox(WinForms.CheckBox checkBox, string text, int left, int top)
    {
        checkBox.Text = text;
        checkBox.Left = left;
        checkBox.Top = top;
        checkBox.AutoSize = true;
        checkBox.ForeColor = TextColor;
        checkBox.FlatStyle = WinForms.FlatStyle.Flat;
    }

    private static void StyleTextBox(WinForms.TextBox textBox)
    {
        textBox.BackColor = Raised;
        textBox.ForeColor = TextColor;
        textBox.BorderStyle = WinForms.BorderStyle.FixedSingle;
    }

    private static void StylePrimaryButton(WinForms.Button button)
    {
        button.FlatStyle = WinForms.FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = BrandOrange;
        button.ForeColor = BrandInk;
        button.Font = new System.Drawing.Font(button.Font, System.Drawing.FontStyle.Bold);
    }

    private static void StyleSecondaryButton(WinForms.Button button)
    {
        button.FlatStyle = WinForms.FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Raised;
        button.ForeColor = TextColor;
    }
}
