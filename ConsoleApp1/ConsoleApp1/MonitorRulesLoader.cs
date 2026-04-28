using System.IO;
using System.Text.Json;

namespace RevitUpgradeController;

internal sealed class MonitorConfigJson
{
    public string[]? DialogKeywords { get; set; }
    public string[]? ButtonPriority { get; set; }
}

internal static class MonitorRulesLoader
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "monitor_config.json");

    private static readonly string[] DefaultDialogKeywords =
    {
        "Upgrade", "Warning", "Error", "Failed",
        "警告", "错误", "失败", "缺失"
    };

    private static readonly string[] DefaultButtonPriority =
    {
        "Continue", "OK", "Yes", "继续", "关闭", "确定", "Close", "是"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static (string[] DialogKeywords, string[] ButtonPriority) Load(Action<string>? log)
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                WriteDefaultConfigFile();
                log?.Invoke($"Created default monitor_config.json at: {ConfigPath}");
                var defaultDialog = (string[])DefaultDialogKeywords.Clone();
                var defaultButtons = (string[])DefaultButtonPriority.Clone();
                LogFriendlyConfig(log, defaultDialog, defaultButtons);
                return (defaultDialog, defaultButtons);
            }

            string json = File.ReadAllText(ConfigPath);
            var parsed = JsonSerializer.Deserialize<MonitorConfigJson>(json, JsonOptions);
            if (parsed == null)
            {
                log?.Invoke("monitor_config.json could not be parsed, using built-in defaults.");
                var defaultDialog = (string[])DefaultDialogKeywords.Clone();
                var defaultButtons = (string[])DefaultButtonPriority.Clone();
                LogFriendlyConfig(log, defaultDialog, defaultButtons);
                return (defaultDialog, defaultButtons);
            }

            var dialog = NormalizeArray(parsed.DialogKeywords, DefaultDialogKeywords, "DialogKeywords", log);
            var buttons = NormalizeArray(parsed.ButtonPriority, DefaultButtonPriority, "ButtonPriority", log);
            LogFriendlyConfig(log, dialog, buttons);
            return (dialog, buttons);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to read monitor_config.json: {ex.Message}. Using built-in defaults.");
            var defaultDialog = (string[])DefaultDialogKeywords.Clone();
            var defaultButtons = (string[])DefaultButtonPriority.Clone();
            LogFriendlyConfig(log, defaultDialog, defaultButtons);
            return (defaultDialog, defaultButtons);
        }
    }

    private static string[] NormalizeArray(string[]? parsed, string[] defaults, string fieldName, Action<string>? log)
    {
        if (parsed == null || parsed.Length == 0)
        {
            log?.Invoke($"{fieldName} missing or empty, using built-in defaults.");
            return (string[])defaults.Clone();
        }

        var cleaned = parsed
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (cleaned.Length == 0)
        {
            log?.Invoke($"{fieldName} had no non-empty entries, using built-in defaults.");
            return (string[])defaults.Clone();
        }

        return cleaned;
    }

    private static void LogFriendlyConfig(Action<string>? log, string[] dialog, string[] buttons)
    {
        string dialogPreview = string.Join(", ", dialog.Take(5));
        string buttonPreview = string.Join(" > ", buttons.Take(5));

        log?.Invoke($"Config loaded from: {ConfigPath}");
        log?.Invoke($"Dialog keywords: {dialog.Length} items (e.g. {dialogPreview})");
        log?.Invoke($"Button priority: {buttons.Length} items (top: {buttonPreview})");
    }

    private static void WriteDefaultConfigFile()
    {
        var doc = new MonitorConfigJson
        {
            DialogKeywords = (string[])DefaultDialogKeywords.Clone(),
            ButtonPriority = (string[])DefaultButtonPriority.Clone()
        };
        string json = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
