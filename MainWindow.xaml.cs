using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using AlexisScreenPro.Helpers;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.ViewModels;

namespace AlexisScreenPro.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ITrayService _trayService;
    private readonly ILogService _log;

    private bool _allowClose;
    private bool _isHiding;
    private bool _hasPlaced;
    private bool _pulseStarted;
    private bool _hasShownTrayHint;

    public MainWindow(MainViewModel viewModel, ITrayService trayService, ILogService log)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        Closing += MainWindow_Closing;
    }

    public event EventHandler? SettingsRequested;

    /// <summary>Allows the host application to close this window during exit.</summary>
    public void PrepareForClose()
    {
        _allowClose = true;
    }

    public void ShowAndActivate()
    {
        if (!_hasPlaced)
        {
            WindowEffects.PlaceBottomRight(this);
            _hasPlaced = true;
        }

        if (!IsVisible)
        {
            Opacity = 0;
            WindowTranslate.Y = 10;
            Show();
            AnimateIn();
            _log.Info("Widget shown.");
        }

        Activate();

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                _ = NativeMethods.SetForegroundWindow(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Activation stays within WPF's normal routing.
        }

        UpdatePulse();
    }

    public void HideToTray()
    {
        _ = HideToTrayAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowEffects.Apply(this);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_hasPlaced)
        {
            WindowEffects.PlaceBottomRight(this);
            _hasPlaced = true;
        }

        UpdatePulse();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        _log.Info("Widget close requested; cancelling and hiding to tray.");
        e.Cancel = true;
        _ = HideToTrayAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsActivelyRecording)
            or nameof(MainViewModel.IsRecording))
        {
            UpdatePulse();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsToastVisible))
        {
            AnimateToast(_viewModel.IsToastVisible);
        }
    }

    private async Task HideToTrayAsync()
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        _log.Info("Widget hiding to tray.");
        _isHiding = true;
        try
        {
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var slide = new DoubleAnimation
            {
                To = 14,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            BeginAnimation(OpacityProperty, fade);
            WindowTranslate.BeginAnimation(TranslateTransform.YProperty, slide);

            await Task.Delay(155).ConfigureAwait(true);

            Hide();
            BeginAnimation(OpacityProperty, null);
            WindowTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            WindowTranslate.Y = 0;

            if (!_hasShownTrayHint)
            {
                _hasShownTrayHint = true;
                _trayService.ShowNotification(
                    "Still running",
                    _viewModel.IsRecording
                        ? "Recording continues safely in the background. Click the tray icon to return."
                        : "Alexis Screen Pro is tucked into the notification area.",
                    TrayNotificationKind.Info);
            }
        }
        finally
        {
            _isHiding = false;
        }
    }

    private void AnimateIn()
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(230),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation
        {
            From = 10,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(OpacityProperty, fade);
        WindowTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            slide);
    }

    private void UpdatePulse()
    {
        if (_viewModel.IsActivelyRecording)
        {
            if (_pulseStarted)
            {
                return;
            }

            _pulseStarted = true;

            var dotPulse = new DoubleAnimation
            {
                From = 1,
                To = 0.28,
                Duration = TimeSpan.FromMilliseconds(760),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            StatusDot.BeginAnimation(OpacityProperty, dotPulse);

            var ringFade = new DoubleAnimation
            {
                From = 0.72,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(1600),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            AmbientPulse.BeginAnimation(OpacityProperty, ringFade);

            var ringScaleX = new DoubleAnimation
            {
                From = 0.9,
                To = 1.65,
                Duration = TimeSpan.FromMilliseconds(1600),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            var ringScaleY = new DoubleAnimation
            {
                From = 0.9,
                To = 1.65,
                Duration = TimeSpan.FromMilliseconds(1600),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            AmbientPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringScaleX);
            AmbientPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringScaleY);
        }
        else
        {
            if (!_pulseStarted)
            {
                return;
            }

            _pulseStarted = false;
            StatusDot.BeginAnimation(OpacityProperty, null);
            AmbientPulse.BeginAnimation(OpacityProperty, null);
            AmbientPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AmbientPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StatusDot.Opacity = 1;
            AmbientPulse.Opacity = 0;
            AmbientPulseScale.ScaleX = 1;
            AmbientPulseScale.ScaleY = 1;
        }
    }

    private void AnimateToast(bool visible)
    {
        if (visible)
        {
            ToastCard.Visibility = Visibility.Visible;
            ToastCard.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(190),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                if (!_viewModel.IsToastVisible)
                {
                    ToastCard.Visibility = Visibility.Collapsed;
                }
            };
            ToastCard.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse was released before DragMove acquired the window.
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }
}
