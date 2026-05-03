// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GhostShell.App.Views;

/// <summary>
/// Custom splash screen shown during application startup. Displays a
/// progress bar + stage caption that update as the initialization
/// sequence progresses (database open, migrations, service registration,
/// UI init, etc). Borderless, centered, draggable by clicking anywhere.
/// Implements INotifyPropertyChanged so XAML bindings auto-update when
/// progress or stage message changes from background threads.
/// </summary>
public partial class SplashWindow : Window, INotifyPropertyChanged
{
    private double _progress;
    private string _stageMessage = "Initializing…";

    public event PropertyChangedEventHandler? PropertyChanged;

    public SplashWindow()
    {
        InitializeComponent();

        // Bind ProgressBar.Value and StageMessage TextBlock.Text to our
        // properties so progress updates from any thread automatically
        // marshal back to the UI via INotifyPropertyChanged.
        // RangeBase.ValueProperty (not instance-qualified) since
        // ValueProperty is a static DependencyProperty on the
        // RangeBase base class. Same for TextBlock.TextProperty —
        // referenced statically via the type, not via an instance.
        ProgressBar.SetBinding(RangeBase.ValueProperty,
            new System.Windows.Data.Binding("Progress") { Source = this });
        StageMessageBlock.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("StageMessage") { Source = this });
    }

    /// <summary>
    /// Current progress as a percentage (0-100). Setting this raises
    /// PropertyChanged so the ProgressBar updates automatically.
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.01)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Current initialization stage caption ("Opening database…", etc).
    /// Setting this raises PropertyChanged so the TextBlock updates.
    /// </summary>
    public string StageMessage
    {
        get => _stageMessage;
        set
        {
            if (_stageMessage != value)
            {
                _stageMessage = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Public method to set progress + stage in one call. Called from
    /// the Bootstrap sequence at known checkpoints (10% "Opening database…",
    /// 30% "Migrating schema…", etc). Thread-safe: marshals to the
    /// Dispatcher if needed.
    /// </summary>
    public void SetProgress(double percentage, string stage)
    {
        // Clamp percentage to 0-100 range.
        percentage = Math.Max(0, Math.Min(100, percentage));

        if (Dispatcher.CheckAccess())
        {
            // Already on UI thread — update directly.
            Progress = percentage;
            StageMessage = stage;
        }
        else
        {
            // Called from a background thread — marshal back to UI thread.
            Dispatcher.Invoke(() =>
            {
                Progress = percentage;
                StageMessage = stage;
            });
        }
    }

    /// <summary>
    /// Allow the splash to be dragged by clicking anywhere on it.
    /// Standard WPF DragMove() call marshalled from the MouseLeftButtonDown
    /// routed event on the Window.
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove() throws if the window is maximized or minimized,
            // or if called from certain contexts. Swallow silently since
            // this is a non-critical affordance.
        }
    }

    /// <summary>
    /// Standard INotifyPropertyChanged helper. Raises PropertyChanged
    /// for the calling property name so bindings update automatically.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
