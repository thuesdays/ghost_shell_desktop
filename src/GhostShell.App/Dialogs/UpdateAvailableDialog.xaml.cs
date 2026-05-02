// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.RegularExpressions;
using System.Windows;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

public partial class UpdateAvailableDialog : Window
{
    private readonly IUpdateService _updateService;
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cancellationTokenSource;

    // [Phase 37 fix:] Prevent re-entrancy — keep track of the open dialog instance
    private static UpdateAvailableDialog? _instance;

    public UpdateAvailableDialog(IUpdateService updateService, UpdateInfo info)
    {
        _updateService = updateService;
        _info = info;
        InitializeComponent();
        PopulateUI();
    }

    private void PopulateUI()
    {
        TitleText.Text = $"Ghost Shell {_info.LatestVersion} is now available.";
        CurrentVersionText.Text = $"You're on {_info.CurrentVersion}.";
        // [Phase 37 fix:] Strip markdown noise from release notes
        ReleaseNotesBox.Text = MarkdownToPlainText(_info.ReleaseNotes);
    }

    // [Phase 37 fix:] Strip common GitHub markdown patterns to plain text
    private static string MarkdownToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var text = markdown;
        // Bold: **text** or __text__ → text
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        // Italic: *text* or _text_ → text
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"_(.+?)_", "$1");
        // Strikethrough: ~~text~~ → text
        text = Regex.Replace(text, @"~~(.+?)~~", "$1");
        // Links: [text](url) → text (url)
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", "$1 ($2)");
        // Headings: # text → text (remove heading markers)
        text = Regex.Replace(text, @"^#+\s+", "", RegexOptions.Multiline);
        // List bullets: - text → • text
        text = Regex.Replace(text, @"^-\s+", "• ", RegexOptions.Multiline);

        return text;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        InstallerButton.IsEnabled = false;
        InstallerButton.Content = "Download installer";
        LaterButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;

        // [Phase 37 fix:] Create a CancellationTokenSource for ApplyAsync
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            // [Phase 37 fix:] Progress constructed on UI thread captures SynchronizationContext
            // so the callback auto-marshals back to UI thread when reported from background thread.
            var progress = new Progress<int>(p =>
            {
                ProgressBar.Value = Math.Min(p, 100);
            });

            await _updateService.ApplyAsync(_info, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // User cancelled the update
        }
        catch (Exception ex)
        {
            // [Phase 37 fix:] Show error in a label instead of MessageBox (stays visible in dialog)
            // and cap the message to 200 chars to avoid UI overflow
            var errorMsg = ex.Message;
            if (errorMsg.Length > 200)
                errorMsg = errorMsg.Substring(0, 197) + "...";

            MessageBox.Show(
                $"Update failed: {errorMsg}",
                "Update Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // [Phase 37 fix:] Re-enable buttons only on error
            UpdateButton.IsEnabled = true;
            InstallerButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void OnInstallerClick(object sender, RoutedEventArgs e)
    {
        var url = _info.InstallerExeUrl ?? _info.ReleasePageUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open download: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // [Phase 37 fix:] Cancel pending update if one is in flight
        if (_cancellationTokenSource is not null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // [Phase 37 fix:] Clear the static instance when closed
        if (_instance == this)
            _instance = null;

        _cancellationTokenSource?.Dispose();
        base.OnClosed(e);
    }

    public static void ShowFor(Window owner, IUpdateService svc, UpdateInfo info)
    {
        // [Phase 37 fix:] If a dialog is already open, activate it instead of creating a second one
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        var dialog = new UpdateAvailableDialog(svc, info)
        {
            Owner = owner,
            // [Phase 37 fix:] Fallback to CenterScreen if Owner is null (MainWindow already disposed)
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };

        _instance = dialog;
        dialog.ShowDialog();
    }
}
