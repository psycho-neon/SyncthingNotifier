using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace SyncthingNotifier;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SyncthingNotifier";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startOnBootItem;
    private readonly SyncMonitor _monitor = new();
    private readonly SynchronizationContext _uiContext;
    private string _status = "Starting...";
    private string? _lastError;
    private bool _isExiting;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_uiContext);

        _startOnBootItem = new ToolStripMenuItem("Start on Boot")
        {
            Checked = IsStartOnBootEnabled()
        };
        _startOnBootItem.Click += (_, _) => ToggleStartOnBoot();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Web GUI", null, (_, _) => OpenWebGui()));
        menu.Items.Add(_startOnBootItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Send Test Notification", null, (_, _) =>
            ShowNotification("Syncthing Notifier", "Test notification", ToolTipIcon.Info)));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadSyncthingIcon(),
            ContextMenuStrip = menu,
            Text = "Syncthing Notifier",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenWebGui();

        _monitor.Synchronized += message => PostToUi(() =>
            ShowNotification("Syncthing synchronized", message, ToolTipIcon.Info));
        _monitor.StatusChanged += status => PostToUi(() => UpdateStatus(status));
        _monitor.Start();
    }

    private void OpenWebGui()
    {
        try
        {
            var configuration = SyncthingConfigurationReader.Load();
            Process.Start(new ProcessStartInfo(configuration.GuiUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowNotification("Syncthing Notifier", exception.Message, ToolTipIcon.Warning);
        }
    }

    private void ToggleStartOnBoot()
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (_startOnBootItem.Checked)
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
                _startOnBootItem.Checked = false;
            }
            else
            {
                var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
                runKey.SetValue(RunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
                _startOnBootItem.Checked = true;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ShowNotification("Syncthing Notifier", $"Could not update Start on Boot: {exception.Message}", ToolTipIcon.Error);
            _startOnBootItem.Checked = IsStartOnBootEnabled();
        }
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(
            $"Syncthing Notifier {version}\n\n" +
            "Monitors the Syncthing folders configured on this PC and notifies when a folder finishes synchronizing.\n\n" +
            $"Status: {_status}",
            "About Syncthing Notifier",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void UpdateStatus(string status)
    {
        _status = status;
        _notifyIcon.Text = ToTooltipText(status);

        if (status.StartsWith("Syncthing unavailable:", StringComparison.Ordinal))
        {
            if (!string.Equals(_lastError, status, StringComparison.Ordinal))
            {
                _lastError = status;
                ShowNotification("Syncthing Notifier", status, ToolTipIcon.Warning);
            }
        }
        else
        {
            _lastError = null;
        }
    }

    private static string ToTooltipText(string status)
    {
        const int maxLength = 63;
        var text = $"Syncthing Notifier - {status}";
        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon) =>
        _notifyIcon.ShowBalloonTip(5_000, title, message, icon);

    private void PostToUi(Action action) =>
        _uiContext.Post(_ =>
        {
            if (!_isExiting)
            {
                action();
            }
        }, null);

    private static bool IsStartOnBootEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(RunValueName) is not null;
    }

    private static Icon LoadSyncthingIcon()
    {
        const string resourceName = "SyncthingNotifier.Assets.syncthing.ico";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return SystemIcons.Application;
        }

        using var icon = new Icon(stream);
        return new Icon(icon, new Size(32, 32));
    }

    protected override void ExitThreadCore()
    {
        _isExiting = true;
        _monitor.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
