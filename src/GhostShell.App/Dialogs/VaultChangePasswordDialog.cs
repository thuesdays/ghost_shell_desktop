// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 26 — change master passphrase modal. Three fields: current,
/// new, confirm. Calls <see cref="IVaultService.ChangeMasterPasswordAsync"/>
/// which re-encrypts every vault item under the new key inside one
/// transaction.
///
/// Code-only Window (matches <see cref="VaultUnlockDialog"/> /
/// <see cref="VaultItemEditorDialog"/>).
/// </summary>
public sealed class VaultChangePasswordDialog : Window
{
    public bool Success { get; private set; }

    private readonly IVaultService _vault;
    private readonly PasswordBox _curField;
    private readonly PasswordBox _newField;
    private readonly PasswordBox _confirmField;
    private readonly TextBlock _strengthLabel;
    private readonly TextBlock _errorLabel;
    private readonly Button _okBtn;

    public VaultChangePasswordDialog(IVaultService vault)
    {
        _vault = vault;

        Title = "Change master passphrase";
        Width = 460; Height = 480;
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
            Text = "🔐  Rotate master passphrase",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(title);

        var sub = new TextBlock
        {
            Text = "Verifies the current passphrase, then re-encrypts every vault item under the new one. Runs inside a single DB transaction — a partial failure rolls back, so you can never end up with mixed-key items.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── Body ──
        var body = new StackPanel();
        body.Children.Add(MakeLabel("Current passphrase"));
        _curField = MakePw();
        _curField.KeyDown += OnAnyKey;
        body.Children.Add(_curField);

        body.Children.Add(MakeLabel("New passphrase"));
        _newField = MakePw();
        _newField.PasswordChanged += (_, _) => UpdateStrength();
        _newField.KeyDown += OnAnyKey;
        body.Children.Add(_newField);

        _strengthLabel = new TextBlock
        {
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 0, 12),
            Text = "strength: —",
        };
        _strengthLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(_strengthLabel);

        body.Children.Add(MakeLabel("Confirm new passphrase"));
        _confirmField = MakePw();
        _confirmField.KeyDown += OnAnyKey;
        body.Children.Add(_confirmField);

        var warn = new TextBlock
        {
            Text = "⚠  We can't recover the new passphrase. Make sure you can reproduce it from memory or a password manager BEFORE saving.",
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        body.Children.Add(warn);

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
        _okBtn = new Button { Content = "Rotate", MinWidth = 110, IsDefault = true };
        _okBtn.SetResourceReference(StyleProperty, "ButtonPrimary");
        _okBtn.Click += async (_, _) => await SubmitAsync();
        btns.Children.Add(cancel);
        btns.Children.Add(_okBtn);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => _curField.Focus();
        // Phase 26 audit fix — wipe password boxes on close so a stale
        // dialog reference can't surface the passphrases via reflection.
        Closed += (_, _) =>
        {
            try { _curField.Clear();     } catch { /* ignore */ }
            try { _newField.Clear();     } catch { /* ignore */ }
            try { _confirmField.Clear(); } catch { /* ignore */ }
        };
    }

    private void OnAnyKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Phase 26 audit fix — guard against double-Enter slipping
            // a second submission past the SubmitAsync IsEnabled flip.
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
            var cur = _curField.Password;
            var nw = _newField.Password;
            var cf = _confirmField.Password;
            if (string.IsNullOrEmpty(cur)) { ShowError("Current passphrase required."); return; }
            if (string.IsNullOrEmpty(nw))  { ShowError("New passphrase required.");     return; }
            if (nw.Length < 8)             { ShowError("New passphrase must be at least 8 characters."); return; }
            if (nw != cf)                  { ShowError("New passphrases don't match."); _confirmField.Focus(); return; }
            if (nw == cur)                 { ShowError("New passphrase must differ from the current one."); return; }
            await _vault.ChangeMasterPasswordAsync(cur, nw);
            Success = true;
            DialogResult = true;
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Current passphrase is wrong.");
            _curField.Password = "";
            _curField.Focus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally { _okBtn.IsEnabled = true; }
    }

    private void ShowError(string msg)
    {
        _errorLabel.Text = "✗  " + msg;
        _errorLabel.Visibility = Visibility.Visible;
    }

    private void UpdateStrength()
    {
        var pw = _newField.Password;
        var (label, brushKey) = ScorePassphrase(pw);
        _strengthLabel.Text = "strength: " + label;
        _strengthLabel.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private static (string label, string brushKey) ScorePassphrase(string pw)
    {
        if (string.IsNullOrEmpty(pw))           return ("—",           "TextMuted");
        if (pw.Length < 8)                      return ("too short",   "ErrBrush");
        var hasLower  = pw.Any(char.IsLower);
        var hasUpper  = pw.Any(char.IsUpper);
        var hasDigit  = pw.Any(char.IsDigit);
        var hasSymbol = pw.Any(c => !char.IsLetterOrDigit(c));
        var classes   = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        if (pw.Length >= 16 && classes >= 3) return ("strong",          "OkBrush");
        if (pw.Length >= 12 && classes >= 2) return ("good",            "Accent");
        if (pw.Length >= 10 && classes >= 2) return ("ok",              "WarnBrush");
        return ("weak", "WarnBrush");
    }

    private static TextBlock MakeLabel(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return t;
    }

    private static PasswordBox MakePw()
        => new PasswordBox
        {
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 12),
        };
}
