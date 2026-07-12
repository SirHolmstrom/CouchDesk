using Core.Branding;
using WinForms = System.Windows.Forms;

namespace Core.Tray;

public sealed record PasswordChange(string CurrentPassword, string NewPassword);

public static class PromptDialogs
{
    public static PasswordChange? ShowChangePassword()
    {
        using var form = new WinForms.Form
        {
            Text = "Change Password",
            Width = 390,
            Height = 245,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var current = PasswordBox(62);
        var next = PasswordBox(112);
        var confirm = PasswordBox(162);
        form.Controls.AddRange(new WinForms.Control[]
        {
            Label("Current password", 15, 42),
            Label("New password", 15, 92),
            Label("Confirm password", 15, 142),
            current,
            next,
            confirm
        });

        var ok = new WinForms.Button
        {
            Text = "Change",
            DialogResult = WinForms.DialogResult.OK,
            Left = 205,
            Top = 177,
            Width = 75
        };
        var cancel = new WinForms.Button
        {
            Text = "Cancel",
            DialogResult = WinForms.DialogResult.Cancel,
            Left = 290,
            Top = 177,
            Width = 75
        };
        form.Controls.AddRange(new WinForms.Control[] { ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog() != WinForms.DialogResult.OK) return null;
        if (next.Text.Length < 12)
        {
            ShowWarning("The new password must be at least 12 characters.");
            return null;
        }
        if (!string.Equals(next.Text, confirm.Text, StringComparison.Ordinal))
        {
            ShowWarning("The new passwords do not match.");
            return null;
        }

        return new(current.Text, next.Text);
    }

    public static string? ShowText(string title, string label, string value, string hint)
    {
        using var form = new WinForms.Form
        {
            Text = title,
            Width = 430,
            Height = string.IsNullOrWhiteSpace(hint) ? 175 : 205,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            StartPosition = WinForms.FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var input = new WinForms.TextBox
        {
            Left = 18,
            Top = 54,
            Width = 376,
            Text = value,
            MaxLength = 64
        };

        form.Controls.Add(Label(label, 18, 24));
        form.Controls.Add(input);

        int buttonTop = 92;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            form.Controls.Add(new WinForms.Label
            {
                Text = hint,
                AutoSize = false,
                Left = 18,
                Top = 84,
                Width = 376,
                Height = 32
            });
            buttonTop = 122;
        }

        var ok = new WinForms.Button
        {
            Text = "Save",
            DialogResult = WinForms.DialogResult.OK,
            Left = 234,
            Top = buttonTop,
            Width = 75
        };
        var cancel = new WinForms.Button
        {
            Text = "Cancel",
            DialogResult = WinForms.DialogResult.Cancel,
            Left = 319,
            Top = buttonTop,
            Width = 75
        };
        form.Controls.AddRange(new WinForms.Control[] { ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        input.SelectAll();
        return form.ShowDialog() == WinForms.DialogResult.OK ? input.Text : null;
    }

    public static void ShowInfo(string message) => WinForms.MessageBox.Show(
        message, ProductInfo.Name, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);

    public static void ShowWarning(string message) => WinForms.MessageBox.Show(
        message, ProductInfo.Name, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);

    public static void ShowError(string message) => WinForms.MessageBox.Show(
        message, ProductInfo.Name, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);

    public static bool Confirm(string message) => WinForms.MessageBox.Show(
        message, ProductInfo.Name, WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning)
        == WinForms.DialogResult.Yes;

    private static WinForms.Label Label(string text, int left, int top) => new()
    {
        Text = text,
        AutoSize = true,
        Left = left,
        Top = top
    };

    private static WinForms.TextBox PasswordBox(int top) => new()
    {
        Left = 140,
        Top = top - 4,
        Width = 225,
        UseSystemPasswordChar = true
    };
}
