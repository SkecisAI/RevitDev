using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace RevitUpgradeController;

public partial class MainWindow : Window
{
    private static readonly Regex TimestampPrefixRegex = new(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s");

    private RevitMonitorService? _monitorService;
    private CancellationTokenSource? _monitorCts;

    public MainWindow()
    {
        InitializeComponent();
        ProcessNameTextBox.Text = "Revit";
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
