using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace RevitUpgradeController;

public partial class MainWindow : Window
{
    private static readonly Regex TimestampPrefixRegex = new(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s");
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "monitor_config.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private RevitMonitorService? _monitorService;
    private CancellationTokenSource? _monitorCts;
    private bool _isUpdatingUiFromConfig;

    public MainWindow()
    {
        InitializeComponent();
        ProcessNameTextBox.Text = "Revit";
        LoadKeywordConfigToUi();
        AppendLog("Ready. Monitor only running Revit process.");
    }

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_monitorCts != null)
        {
            AppendLog("Monitor already running.");
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorService = new RevitMonitorService(AppendLog);

        var processName = string.IsNullOrWhiteSpace(ProcessNameTextBox.Text)
            ? "Revit"
            : ProcessNameTextBox.Text.Trim();

        _ = _monitorService.RunAsync(processName, _monitorCts.Token);
        AppendLog($"Monitor started. Process={processName}");

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorService = null;

        AppendLog("Monitor stopping...");
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void CountButton_OnClick(object sender, RoutedEventArgs e)
    {
        var processName = string.IsNullOrWhiteSpace(ProcessNameTextBox.Text)
            ? "Revit"
            : ProcessNameTextBox.Text.Trim();

        var count = Process.GetProcessesByName(processName)
            .Count(p => !p.HasExited);

        AppendLog($"Current monitored process count: Process={processName}, Count={count}");
    }

    private void ClearLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        AppendLog("Log panel cleared.");
    }

    private void AddDialogKeywordButton_OnClick(object sender, RoutedEventArgs e)
    {
        AddKeywordToList(DialogKeywordInputTextBox, DialogKeywordsListBox, "dialog keyword");
    }

    private void RemoveDialogKeywordButton_OnClick(object sender, RoutedEventArgs e)
    {
        RemoveSelectedKeyword(DialogKeywordsListBox, "dialog keyword");
    }

    private void AddButtonKeywordButton_OnClick(object sender, RoutedEventArgs e)
    {
        AddKeywordToList(ButtonKeywordInputTextBox, ButtonPriorityListBox, "button keyword", insertAtTop: true);
    }

    private void RemoveButtonKeywordButton_OnClick(object sender, RoutedEventArgs e)
    {
        RemoveSelectedKeyword(ButtonPriorityListBox, "button keyword");
    }

    private void ReloadConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadKeywordConfigToUi();
        AppendLog("Config reloaded into UI.");
    }

    private bool SaveKeywordConfigFromUi(bool logSuccess)
    {
        var dialogKeywords = DialogKeywordsListBox.Items
            .Cast<string>()
            .ToArray();
        var buttonPriority = ButtonPriorityListBox.Items
            .Cast<string>()
            .ToArray();

        if (dialogKeywords.Length == 0)
        {
            if (logSuccess)
            {
                AppendLog("Save cancelled: Dialog keywords cannot be empty.");
            }
            return false;
        }

        if (buttonPriority.Length == 0)
        {
            if (logSuccess)
            {
                AppendLog("Save cancelled: Button priority cannot be empty.");
            }
            return false;
        }

        var config = new MonitorConfigJson
        {
            DialogKeywords = dialogKeywords,
            ButtonPriority = buttonPriority
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
        _monitorService?.ReloadRules();
        if (logSuccess)
        {
            AppendLog("Config auto-saved and monitor rules reloaded.");
        }

        return true;
    }

    private void LoadKeywordConfigToUi()
    {
        try
        {
            _isUpdatingUiFromConfig = true;
            var (dialogKeywords, buttonPriority) = MonitorRulesLoader.Load(AppendLog);
            FillList(DialogKeywordsListBox, dialogKeywords);
            FillList(ButtonPriorityListBox, buttonPriority);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load config into UI: {ex.Message}");
        }
        finally
        {
            _isUpdatingUiFromConfig = false;
        }
    }

    private void AddKeywordToList(TextBox input, ListBox targetList, string keywordType, bool insertAtTop = false)
    {
        var keyword = (input.Text ?? "").Trim();
        if (keyword.Length == 0)
        {
            AppendLog($"Cannot add empty {keywordType}.");
            return;
        }

        if (targetList.Items.Cast<string>().Any(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"Skipped duplicate {keywordType}: {keyword}");
            return;
        }

        if (insertAtTop)
        {
            targetList.Items.Insert(0, keyword);
        }
        else
        {
            targetList.Items.Add(keyword);
        }
        input.Clear();
        AppendLog($"Added {keywordType}: {keyword}");
        if (!_isUpdatingUiFromConfig)
        {
            SaveKeywordConfigFromUi(logSuccess: true);
        }
    }

    private void RemoveSelectedKeyword(ListBox targetList, string keywordType)
    {
        if (targetList.SelectedItem is not string selected)
        {
            AppendLog($"Select a {keywordType} to remove.");
            return;
        }

        targetList.Items.Remove(selected);
        AppendLog($"Removed {keywordType}: {selected}");
        if (!_isUpdatingUiFromConfig)
        {
            SaveKeywordConfigFromUi(logSuccess: true);
        }
    }

    private static void FillList(ListBox listBox, IEnumerable<string> values)
    {
        listBox.Items.Clear();
        foreach (var value in values)
        {
            listBox.Items.Add(value);
        }
    }

    private void AppendLog(string message)
    {
        var line = TimestampPrefixRegex.IsMatch(message)
            ? message
            : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }
}
