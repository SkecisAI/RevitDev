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
                return ((string[])DefaultDialogKeywords.Clone(), (string[])DefaultButtonPriority.Clone());
            }

            string json = File.ReadAllText(ConfigPath);
            var parsed = JsonSerializer.Deserialize<MonitorConfigJson>(json, JsonOptions);
            if (parsed == null)
            {
                log?.Invoke("monitor_config.json could not be parsed, using built-in defaults.");
                return ((string[])DefaultDialogKeywords.Clone(), (string[])DefaultButtonPriority.Clone());
            }

            var dialog = NormalizeArray(parsed.DialogKeywords, DefaultDialogKeywords, "DialogKeywords", log);
            var buttons = NormalizeArray(parsed.ButtonPriority, DefaultButtonPriority, "ButtonPriority", log);
            return (dialog, buttons);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to read monitor_config.json: {ex.Message}. Using built-in defaults.");
            return ((string[])DefaultDialogKeywords.Clone(), (string[])DefaultButtonPriority.Clone());
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
