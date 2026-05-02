// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 25 — vault master-passphrase prompt. One Window covers two
/// flows because they share 95% of the UI:
///
///   • <b>Initialize</b> mode (vault.IsInitialized == false): user
///     picks a fresh master passphrase. Two PasswordBoxes (entry +
///     confirm) and a strength indicator. On OK the dialog calls
///     <see cref="IVaultService.InitializeAsync"/>.
///   • <b>Unlock</b> mode (vault.IsInitialized == true): single
///     PasswordBox. On OK calls <see cref="IVaultService.UnlockAsync"/>;
///     on wrong password flashes the field + leaves it focused.
///
/// Code-only WPF — keeps the dialog independent of the App-level XAML
/// resource pipeline so it can pop on top of any view (Profile editor,
/// Scripts page, ApplyToProfiles flow) without re-registering.
/// </summary>
public sealed class VaultUnlockDialog : Window
{
    public bool Success { get; private set; }

    /// <summary>The passphrase the user just typed, available only on a
    /// successful unlock/init. Some flows (Reset, ChangeMasterPassword)
    /// need to feed the verified passphrase into a follow-up service
    /// call. Cleared on Close so it doesn't linger longer than the modal.</summary>
    public string? VerifiedPassphrase { get; private set; }

    private readonly IVaultService _vault;
    private readonly bool _initMode;

    private readonly PasswordBox _pwField;
    private readonly PasswordBox? _pwConfirm;
    private readonly TextBlock _strengthLabel;
    private readonly TextBlock _errorLabel;
    private readonly Button _okBtn;

    public VaultUnlockDialog(IVaultService vault)
    {
        _vault = vault;
        _initMode = !vault.IsInitialized;

        Title = _initMode ? "Initialize vault" : "Unlock vault";
        Width = 460;
        Height = _initMode ? 410 : 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Header ──
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new TextBlock
        {
            Text = _initMode ? "🔐  Set master passphrase" : "🔐  Unlock vault",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var sub = new TextBlock
        {
            Text = _initMode
                ? "This passphrase encrypts every credential in the vault. We can't reset it for you — losing it means losing the vault. Pick something memorable but strong (12+ characters)."
                : "Enter the master passphrase you set when you initialised the vault.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(title);
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── Body ──
        var body = new StackPanel();

        var lblPw = new TextBlock
        {
            Text = _initMode ? "Master passphrase" : "Passphrase",
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        lblPw.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(lblPw);

        _pwField = new PasswordBox
        {
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
        };
        _pwField.PasswordChanged += (_, _) => UpdateStrength();
        _pwField.KeyDown += OnPwKeyDown;
        body.Children.Add(_pwField);

        if (_initMode)
        {
            _strengthLabel = new TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 14),
                Text = "strength: —",
            };
            _strengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            body.Children.Add(_strengthLabel);

            var lblConfirm = new TextBlock
            {
                Text = "Confirm passphrase",
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            lblConfirm.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            body.Children.Add(lblConfirm);

            _pwConfirm = new PasswordBox
            {
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 14),
            };
            _pwConfirm.KeyDown += OnPwKeyDown;
            body.Children.Add(_pwConfirm);

            var warn = new TextBlock
            {
                Text = "⚠  Write this down or store in a different password manager. " +
                       "There is no recovery flow.",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            body.Children.Add(warn);
        }
        else
        {
            _strengthLabel = new TextBlock { Visibility = Visibility.Collapsed };
            _pwConfirm = null;
        }

        _errorLabel = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _errorLabel.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        body.Children.Add(_errorLabel);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ── Footer ──
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _okBtn = new Button
        {
            Content = _initMode ? "Initialize" : "Unlock",
            MinWidth = 110,
            IsDefault = true,
        };
        _okBtn.SetResourceReference(StyleProperty, "ButtonPrimary");
        _okBtn.Click += async (_, _) => await SubmitAsync();
        btns.Children.Add(cancel);
        btns.Children.Add(_okBtn);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => _pwField.Focus();

        // Phase 26 audit fix — drop VerifiedPassphrase + the password
        // box buffers as soon as the dialog closes, so a stale dialog
        // reference held by GC roots can't hand the cleartext back to
        // any caller. Callers must read VerifiedPassphrase BEFORE
        // disposing of the dialog (the existing call sites all do).
        Closed += (_, _) =>
        {
            VerifiedPassphrase = null;
            try { _pwField.Clear(); } catch { /* ignore */ }
            try { _pwConfirm?.Clear(); } catch { /* ignore */ }
        };
    }

    private void OnPwKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Phase 26 audit fix — disable OK synchronously here so a
            // second Enter (or rapid double-press) doesn't slip past
            // before SubmitAsync's own _okBtn.IsEnabled = false runs.
            if (!_okBtn.IsEnabled) { e.Handled = true; return; }
            _okBtn.IsEnabled = false;
            _ = SubmitAsync();
            e.Handled = true;
        }
    }

    private async Task SubmitAsync()
    {
        _errorLabel.Visibility = Visibility.Collapsed;
        _okBtn.IsEnabled = false;
        try
        {
            if (_initMode)
            {
                var pw = _pwField.Password;
                var pw2 = _pwConfirm!.Password;
                if (string.IsNullOrEmpty(pw))
                {
                    ShowError("Passphrase is required.");
                    return;
                }
                if (pw.Length < 8)
                {
                    ShowError("Passphrase must be at least 8 characters.");
                    return;
                }
                if (pw != pw2)
                {
                    ShowError("Passphrases don't match.");
                    _pwConfirm.Focus();
                    return;
                }
                await _vault.InitializeAsync(pw);
                Success = true;
                VerifiedPassphrase = pw;
                DialogResult = true;
                Close();
            }
            else
            {
                var pw = _pwField.Password;
                if (string.IsNullOrEmpty(pw))
                {
                    ShowError("Passphrase is required.");
                    return;
                }
                var ok = await _vault.UnlockAsync(pw);
                if (!ok)
                {
                    ShowError("Wrong passphrase.");
                    _pwField.Password = "";
                    _pwField.Focus();
                    return;
                }
                Success = true;
                VerifiedPassphrase = pw;
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _okBtn.IsEnabled = true;
        }
    }

    private void ShowError(string msg)
    {
        _errorLabel.Text = "✗  " + msg;
        _errorLabel.Visibility = Visibility.Visible;
    }

    private void UpdateStrength()
    {
        if (!_initMode) return;
        var pw = _pwField.Password;
        var (label, brushKey) = ScorePassphrase(pw);
        _strengthLabel.Text = "strength: " + label;
        _strengthLabel.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>Cheap zxcvbn-lite — counts character classes + length.
    /// Not a real entropy estimator; just enough to nudge users away
    /// from "qwerty" / "12345678".</summary>
    private static (string label, string brushKey) ScorePassphrase(string pw)
    {
        if (string.IsNullOrEmpty(pw))           return ("—",            "TextMuted");
        if (pw.Length < 8)                      return ("too short",    "ErrBrush");
        var hasLower  = pw.Any(char.IsLower);
        var hasUpper  = pw.Any(char.IsUpper);
        var hasDigit  = pw.Any(char.IsDigit);
        var hasSymbol = pw.Any(c => !char.IsLetterOrDigit(c));
        var classes   = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        if (pw.Length >= 16 && classes >= 3)    return ("strong",       "OkBrush");
        if (pw.Length >= 12 && classes >= 2)    return ("good",         "Accent");
        if (pw.Length >= 10 && classes >= 2)    return ("ok",           "WarnBrush");
        return ("weak", "WarnBrush");
    }
}
