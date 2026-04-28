using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace RevitUpgradeController;

internal sealed class RevitMonitorService
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "revit_dialog_monitor.log");
    private static readonly object LogLock = new();

    private readonly Action<string>? _uiLogger;
    private readonly string[] _dialogKeywords;
    private readonly string[] _buttonPriority;
    private DateTime _lastNoProcessLog = DateTime.MinValue;

    public RevitMonitorService(Action<string>? uiLogger = null)
    {
        _uiLogger = uiLogger;
        (_dialogKeywords, _buttonPriority) = MonitorRulesLoader.Load(Log);
    }

    public Task RunAsync(string processName, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            Log($"Start monitoring process: {processName}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var revitProcesses = Process.GetProcessesByName(processName)
                        .Where(p => !p.HasExited)
                        .ToArray();

                    if (revitProcesses.Length == 0)
                    {
                        if ((DateTime.Now - _lastNoProcessLog).TotalSeconds >= 10)
                        {
                            Log($"No running process found: {processName}");
                            _lastNoProcessLog = DateTime.Now;
                        }
                    }

                    foreach (var process in revitProcesses)
                    {
                        HandleRevitDialogs(process.Id);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Monitor error: {ex.Message}");
                }

                Thread.Sleep(500);
            }

            Log("Monitor stopped.");
        }, cancellationToken);
    }

    private void HandleRevitDialogs(int processId)
    {
        var desktop = AutomationElement.RootElement;
        var windows = desktop.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId));

        foreach (AutomationElement window in windows)
        {
            string title = SafeName(window);
            if (!ShouldHandleWindow(title, window, out string reason))
            {
                continue;
            }

            if (ClickBestButton(window, out string buttonName))
            {
                Log($"PID={processId} Dialog=\"{title}\" Reason=\"{reason}\" Click=\"{buttonName}\"");
            }
        }
    }

    private bool ShouldHandleWindow(string title, AutomationElement window, out string reason)
    {
        if (TryGetMatchedKeyword(title, _dialogKeywords, out string titleMatched))
        {
            reason = $"title matched keywords: \"{titleMatched}\"";
            return true;
        }

        string allText = GetDescendantText(window);
        if (TryGetMatchedKeyword(allText, _dialogKeywords, out string textMatched))
        {
            reason = $"descendant text matched keywords: \"{textMatched}\"";
            return true;
        }

        var buttons = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        bool fallback = buttons.Count > 0 && buttons.Count <= 6;
        reason = fallback
            ? $"fallback small interactive window: buttonCount={buttons.Count}"
            : "";
        return fallback;
    }

    private bool ClickBestButton(AutomationElement window, out string clickedButtonName)
    {
        clickedButtonName = "";
        var buttons = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (string key in _buttonPriority)
        {
            foreach (AutomationElement button in buttons)
            {
                string name = SafeName(button);
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    clickedButtonName = name;
                    return true;
                }
            }
        }

        foreach (AutomationElement button in buttons)
        {
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
            {
                clickedButtonName = SafeName(button);
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }

        return false;
    }

    private static string GetDescendantText(AutomationElement root)
    {
        try
        {
            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            return string.Join(" ",
                elements.Cast<AutomationElement>()
                    .Select(SafeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch
        {
            return "";
        }
    }

    private static string SafeName(AutomationElement e)
    {
        try
        {
            return e.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool TryGetMatchedKeyword(string text, string[] keys, out string matchedKeyword)
    {
        matchedKeyword = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matchedKeyword = key;
                return true;
            }
        }

        return false;
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (LogLock)
        {
            _uiLogger?.Invoke(line);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}