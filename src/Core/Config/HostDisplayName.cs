using System.Net;
using System.Text;

namespace Core.Config;

public static class HostDisplayName
{
    private const int MaxLength = 64;

    public static string AutomaticDefault => GetDefault();

    public static string Get(AppConfig config) =>
        Normalize(config.HostDisplayName) ?? GetDefault();

    public static string? NormalizeCustom(string? value) =>
        Normalize(value);

    private static string GetDefault()
    {
        string? machine = Normalize(Environment.MachineName)
            ?? Normalize(Dns.GetHostName());
        string? user = Normalize(Environment.UserName);
        string chosen = IsUsefulUserName(user, machine) ? user! : machine ?? "This PC";
        return AddPcSuffix(chosen);
    }

    private static bool IsUsefulUserName(string? user, string? machine)
    {
        if (string.IsNullOrWhiteSpace(user)) return false;
        if (user.Contains('$')) return false;
        if (machine is not null && string.Equals(user, machine, StringComparison.OrdinalIgnoreCase))
            return false;

        return !user.Equals("system", StringComparison.OrdinalIgnoreCase)
            && !user.Equals("localsystem", StringComparison.OrdinalIgnoreCase)
            && !user.Equals("local service", StringComparison.OrdinalIgnoreCase)
            && !user.Equals("network service", StringComparison.OrdinalIgnoreCase)
            && !user.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase);
    }

    private static string AddPcSuffix(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.EndsWith(" pc", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("-pc", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("_pc", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(" computer", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"{trimmed} PC";
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char ch in value.Trim())
        {
            if (char.IsControl(ch)) continue;
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0) builder.Append(' ');
            builder.Append(ch);
            pendingSpace = false;
            if (builder.Length >= MaxLength) break;
        }

        string normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
