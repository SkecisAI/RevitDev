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
    private string[] _dialogKeywords;
    private string[] _buttonPriority;
    private DateTime _lastNoProcessLog = DateTime.MinValue;
    private DateTime _lastUnhandledDialogLog = DateTime.MinValue;

    public RevitMonitorService(Action<string>? uiLogger = null)
    {
        _uiLogger = uiLogger;
        (_dialogKeywords, _buttonPriority) = MonitorRulesLoader.Load(Log);
    }

    public void ReloadRules()
    {
        var (dialogKeywords, buttonPriority) = MonitorRulesLoader.Load(Log);
        _dialogKeywords = dialogKeywords;
        _buttonPriority = buttonPriority;
        Log("Monitor rules hot-reloaded.");
    }

    public Task RunAsync(string processName, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            Log($"Start monitoring process: {processName}");
            AutomationEventHandler? windowOpenedHandler = null;

            try
            {
                // Event-based fallback: catch windows immediately when they are created.
                windowOpenedHandler = (sender, eventArgs) =>
                {
                    try
                    {
                        if (eventArgs is not AutomationEventArgs openedArgs ||
                            openedArgs.EventId != WindowPattern.WindowOpenedEvent)
                        {
                            return;
                        }

                        if (_dialogKeywords.Length == 0)
                        {
                            return;
                        }

                        if (sender is not AutomationElement openedWindow)
                        {
                            return;
                        }

                        if (!IsTargetProcessWindow(openedWindow, processName))
                        {
                            return;
                        }

                        TryHandleSingleWindow(openedWindow, out _);
                    }
                    catch (Exception ex)
                    {
                        Log($"WindowOpened handler error: {ex.Message}");
                    }
                };

                Automation.AddAutomationEventHandler(
                    WindowPattern.WindowOpenedEvent,
                    AutomationElement.RootElement,
                    TreeScope.Subtree,
                    windowOpenedHandler);

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

                    cancellationToken.WaitHandle.WaitOne(500);
                }
            }
            finally
            {
                if (windowOpenedHandler != null)
                {
                    Automation.RemoveAutomationEventHandler(
                        WindowPattern.WindowOpenedEvent,
                        AutomationElement.RootElement,
                        windowOpenedHandler);
                }
                Log("Monitor stopped.");
            }
        }, cancellationToken);
    }

    private void HandleRevitDialogs(int processId)
    {
        var desktop = AutomationElement.RootElement;
        var windows = desktop.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));

        foreach (AutomationElement window in windows)
        {
            if (TryHandleSingleWindow(window, out string message))
            {
                Log($"PID={processId} {message}");
            }
        }
    }

    private bool TryHandleSingleWindow(AutomationElement window, out string logMessage)
    {
        logMessage = "";
        string title = SafeName(window);
        if (!ShouldHandleWindow(title, window, out string reason))
        {
            return false;
        }

        if (ClickBestButton(window, out string buttonName, out string failureReason))
        {
            logMessage = $"Dialog=\"{title}\" Reason=\"{reason}\" Click=\"{buttonName}\"";
            return true;
        }

        if ((DateTime.Now - _lastUnhandledDialogLog).TotalMilliseconds >= 1500)
        {
            Log($"Dialog not handled. Title=\"{title}\" Reason=\"{reason}\" Detail=\"{failureReason}\"");
            _lastUnhandledDialogLog = DateTime.Now;
        }

        return false;
    }

    private static bool IsTargetProcessWindow(AutomationElement window, string processName)
    {
        int processId;
        try
        {
            processId = window.Current.ProcessId;
        }
        catch
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                   process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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
        return ClickBestButton(window, out clickedButtonName, out _);
    }

    private bool ClickBestButton(AutomationElement window, out string clickedButtonName, out string failureReason)
    {
        clickedButtonName = "";
        failureReason = "";
        var buttons = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        if (buttons.Count == 0)
        {
            failureReason = "no buttons found in window";
            return false;
        }

        var matchedButtonNames = new List<string>();
        var invokeFailureReasons = new List<string>();

        foreach (string key in _buttonPriority)
        {
            foreach (AutomationElement button in buttons)
            {
                string name = SafeName(button);
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matchedButtonNames.Add(name.Length == 0 ? "<empty>" : name);

                if (TryInvokeButton(button, out string invokedButtonName, out string invokeFailure))
                {
                    clickedButtonName = invokedButtonName;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(invokeFailure))
                {
                    invokeFailureReasons.Add($"\"{(name.Length == 0 ? "<empty>" : name)}\": {invokeFailure}");
                }
            }
        }

        foreach (AutomationElement button in buttons)
        {
            string name = SafeName(button);
            if (TryInvokeButton(button, out string invokedButtonName, out string invokeFailure))
            {
                clickedButtonName = invokedButtonName;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(invokeFailure))
            {
                invokeFailureReasons.Add($"\"{(name.Length == 0 ? "<empty>" : name)}\": {invokeFailure}");
            }
        }

        if (matchedButtonNames.Count == 0)
        {
            failureReason = "no button matched configured priority keywords";
        }
        else
        {
            failureReason = $"matched buttons: {string.Join(", ", matchedButtonNames)}";
        }

        if (invokeFailureReasons.Count > 0)
        {
            failureReason += $"; invoke failures: {string.Join(" | ", invokeFailureReasons.Distinct())}";
        }

        return false;
    }

    private bool TryInvokeButton(AutomationElement button, out string buttonName)
    {
        return TryInvokeButton(button, out buttonName, out _);
    }

    private bool TryInvokeButton(AutomationElement button, out string buttonName, out string failureReason)
    {
        buttonName = SafeName(button);
        failureReason = "";

        bool isEnabled;
        try
        {
            isEnabled = button.Current.IsEnabled;
        }
        catch
        {
            failureReason = "failed to read IsEnabled";
            return false;
        }

        if (!isEnabled)
        {
            failureReason = "button disabled";
            return false;
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            failureReason = "InvokePattern unavailable";
            return false;
        }

        try
        {
            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch (ElementNotEnabledException)
        {
            failureReason = "ElementNotEnabledException during invoke";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            failureReason = $"InvalidOperationException: {ex.Message}";
            Log($"Skip invoke on button \"{buttonName}\": {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
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

        try
        {
            lock (LogLock)
            {
                try
                {
                    _uiLogger?.Invoke(line);
                }
                catch
                {
                    // Never let UI logging crash monitor thread.
                }

                try
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch
                {
                    // Best-effort file logging only.
                }
            }
        }
        catch
        {
            // Swallow all logging failures to keep monitoring alive.
        }
    }
}