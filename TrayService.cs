using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AlexisScreenPro.Services;

/// <summary>
/// Owns the Windows notification-area icon and its cobalt context menu. The
/// TaskbarIcon is a WPF control, so every mutation is marshalled to the UI
/// dispatcher; notifications are safe to request from worker threads.
/// </summary>
public sealed class TrayService : DisposableBase, ITrayService
{
    private readonly ILogService _log;
    private readonly Action _showDashboard;
    private readonly Action _toggleRecording;
    private readonly Action _openSettings;
    private readonly Action _openRecordings;
    private readonly Action _exitApplication;

    private TaskbarIcon? _taskbarIcon;
    private MenuItem? _startStopMenuItem;
    private ImageSource? _idleIcon;
    private ImageSource? _recordingIcon;
    private RecordingState _state = RecordingState.Idle;
    private TimeSpan _lastElapsed;
    private long _lastTooltipSecond = -1;
    private bool _initialized;

    public TrayService(
        ILogService log,
        Action showDashboard,
        Action toggleRecording,
        Action openSettings,
        Action openRecordings,
        Action exitApplication)
    {
        _log = log;
        _showDashboard = showDashboard;
        _toggleRecording = toggleRecording;
        _openSettings = openSettings;
        _openRecordings = openRecordings;
        _exitApplication = exitApplication;
    }

    public void Initialize()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(Initialize);
            return;
        }

        try
        {
            _idleIcon = LoadIcon("tray-idle.ico");
            _recordingIcon = LoadIcon("tray-recording.ico");

            _taskbarIcon = new TaskbarIcon
            {
                ToolTipText = $"{AppConstants.ProductName}\nReady to capture",
                IconSource = _idleIcon,
                NoLeftClickDelay = true,
                MenuActivation = PopupActivationMode.RightClick,
                LeftClickCommand = new RelayCommand(_showDashboard)
            };

            _taskbarIcon.ContextMenu = BuildContextMenu();
            _taskbarIcon.TrayMouseDoubleClick += (_, _) => _showDashboard();
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            _initialized = true;
            _log.Info("Tray icon registered.");
        }
        catch (Exception ex)
        {
            _log.Error("The system tray icon could not be created.", ex);
            throw new InvalidOperationException(
                "Alexis Screen Pro could not create its notification-area icon.", ex);
        }
    }

    public void SetRecordingState(RecordingState state)
    {
        if (!_initialized || _taskbarIcon is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            _state = state;
            if (_startStopMenuItem is not null)
            {
                _startStopMenuItem.Header = state switch
                {
                    RecordingState.Starting => "Starting capture…",
                    RecordingState.Recording => "Stop recording",
                    RecordingState.Stopping => "Finalizing capture…",
                    _ => "Start recording"
                };
                _startStopMenuItem.IsEnabled = state is RecordingState.Idle or RecordingState.Recording;
            }

            _taskbarIcon.IconSource = state switch
            {
                RecordingState.Recording or RecordingState.Stopping => _recordingIcon,
                _ => _idleIcon
            };

            _lastTooltipSecond = -1;
            UpdateTooltipCore();
        });
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        if (!_initialized || _taskbarIcon is null || _state is not RecordingState.Recording)
        {
            return;
        }

        _lastElapsed = elapsed;
        var second = (long)elapsed.TotalSeconds;
        if (second == _lastTooltipSecond)
        {
            return;
        }

        _lastTooltipSecond = second;
        RunOnUiThread(UpdateTooltipCore);
    }

    public void ShowNotification(
        string title,
        string message,
        TrayNotificationKind kind = TrayNotificationKind.Info)
    {
        if (!_initialized || _taskbarIcon is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            try
            {
                _taskbarIcon.ShowNotification(
                    title,
                    message,
                    kind switch
                    {
                        TrayNotificationKind.Error => NotificationIcon.Error,
                        TrayNotificationKind.Warning => NotificationIcon.Warning,
                        TrayNotificationKind.Success => NotificationIcon.Info,
                        _ => NotificationIcon.Info
                    });
            }
            catch (Exception ex)
            {
                _log.Warn($"Tray notification could not be shown: {ex.Message}");
            }
        });
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        ApplyStyle(menu, "TrayContextMenuStyle");

        var header = new MenuItem
        {
            Header = "ALEXIS SCREEN PRO",
            IsEnabled = false
        };
        ApplyStyle(header, "TrayMenuHeaderStyle");
        menu.Items.Add(header);
        menu.Items.Add(CreateSeparator());

        menu.Items.Add(CreateMenuItem("Open dashboard", _showDashboard, "TrayMenuItemStyle", true));

        _startStopMenuItem = CreateMenuItem("Start recording", _toggleRecording, "TrayMenuItemStyle", true);
        menu.Items.Add(_startStopMenuItem);

        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("Open recordings folder", _openRecordings, "TrayMenuItemStyle", true));
        menu.Items.Add(CreateMenuItem("Studio settings", _openSettings, "TrayMenuItemStyle", true));

        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("Exit Alexis Screen Pro", _exitApplication, "TrayMenuItemStyle", true));

        menu.Opened += (_, _) => UpdateMenuState();
        return menu;
    }

    private void UpdateMenuState()
    {
        if (_startStopMenuItem is null)
        {
            return;
        }

        _startStopMenuItem.Header = _state switch
        {
            RecordingState.Starting => "Starting capture…",
            RecordingState.Recording => "Stop recording",
            RecordingState.Stopping => "Finalizing capture…",
            _ => "Start recording"
        };
        _startStopMenuItem.IsEnabled = _state is RecordingState.Idle or RecordingState.Recording;
    }

    private static MenuItem CreateMenuItem(
        string header,
        Action click,
        string styleKey,
        bool isEnabled)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        ApplyStyle(item, styleKey);
        item.Click += (_, _) => click();
        return item;
    }

    private static Separator CreateSeparator()
    {
        var separator = new Separator();
        ApplyStyle(separator, "TrayMenuSeparatorStyle");
        return separator;
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        if (Application.Current.TryFindResource(key) is Style style)
        {
            element.Style = style;
        }
    }

    private static ImageSource LoadIcon(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(
            $"pack://application:,,,/{AppConstants.ProductName.Replace(" ", string.Empty)};component/Assets/{fileName}",
            UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void UpdateTooltipCore()
    {
        if (_taskbarIcon is null)
        {
            return;
        }

        _taskbarIcon.ToolTipText = _state switch
        {
            RecordingState.Starting => $"{AppConstants.ProductName}\nStarting capture…",
            RecordingState.Recording =>
                $"{AppConstants.ProductName}\nRecording · {FormatElapsed(_lastElapsed)}",
            RecordingState.Stopping => $"{AppConstants.ProductName}\nFinalizing capture…",
            _ => $"{AppConstants.ProductName}\nReady to capture"
        };
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

    private void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _taskbarIcon?.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warn($"Tray icon disposal raised: {ex.Message}");
                }
                finally
                {
                    _taskbarIcon = null;
                }
            });
        }

        base.Dispose(disposing);
    }
}
