// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 25 — vault item editor. Renders a kind-aware form: pick a
/// kind from the dropdown, the secret-field rows update to match.
/// Plain metadata (name, identifier, profile, status, tags, notes)
/// stays consistent across kinds.
///
/// Two modes:
///   • Create — receives a null existing item, calls
///     <see cref="IVaultService.CreateAsync"/> on save.
///   • Edit   — receives an existing item; secrets are pre-decrypted
///     by the caller and passed in as <c>existingClear</c> so the
///     fields can be pre-populated. On save calls
///     <see cref="IVaultService.UpdateAsync"/>.
/// </summary>
public sealed class VaultItemEditorDialog : Window
{
    public VaultItem? Saved { get; private set; }

    private readonly IVaultService _vault;
    private readonly VaultItem? _existing;
    private readonly IReadOnlyDictionary<string, string>? _existingClear;
    private readonly IReadOnlyList<Profile> _profiles;

    private readonly TextBox _nameField;
    private readonly ComboBox _kindCombo;
    private readonly TextBox _serviceField;
    private readonly TextBox _identifierField;
    private readonly ComboBox _profileCombo;
    private readonly ComboBox _statusCombo;
    private readonly TextBox _tagsField;
    private readonly TextBox _notesField;
    private readonly StackPanel _secretsPanel;
    private readonly Button _okBtn;
    private readonly TextBlock _errorLabel;

    public VaultItemEditorDialog(
        IVaultService vault,
        VaultItem? existing,
        IReadOnlyDictionary<string, string>? existingClear,
        IReadOnlyList<Profile> profiles)
    {
        _vault = vault;
        _existing = existing;
        _existingClear = existingClear;
        _profiles = profiles;

        Title = existing is null ? "New vault item" : $"Edit — {existing.Name}";
        Width = 720;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
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
            Text = existing is null ? "New vault item" : "Edit vault item",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(title);
        var sub = new TextBlock
        {
            Text = "Pick a kind to load the right secret fields. Plaintext metadata (name, identifier, tags) is searchable without unlocking the vault; secret fields below are encrypted at rest.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── Body (scrollable) ──
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var stack = new StackPanel();
        body.Content = stack;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Name (required)
        stack.Children.Add(MakeLabel("Name *"));
        _nameField = MakeTextBox(existing?.Name ?? "");
        stack.Children.Add(_nameField);

        // Kind
        stack.Children.Add(MakeLabel("Kind"));
        _kindCombo = new ComboBox
        {
            ItemsSource = VaultKinds.Catalog,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Id",
            SelectedValue = existing?.Kind ?? "account",
            Margin = new Thickness(0, 0, 0, 12),
        };
        _kindCombo.SelectionChanged += (_, _) => RebuildSecretFields();
        stack.Children.Add(_kindCombo);

        // Service / identifier (plaintext metadata)
        stack.Children.Add(MakeLabel("Service slug (optional)"));
        _serviceField = MakeTextBox(existing?.Service ?? "");
        _serviceField.ToolTip = "e.g. google, github, aws — used for search + UI grouping";
        stack.Children.Add(_serviceField);

        stack.Children.Add(MakeLabel("Identifier (optional, plaintext)"));
        _identifierField = MakeTextBox(existing?.Identifier ?? "");
        _identifierField.ToolTip = "Searchable label like an email or address — visible without unlock";
        stack.Children.Add(_identifierField);

        // Profile binding
        stack.Children.Add(MakeLabel("Profile (optional)"));
        _profileCombo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 12),
        };
        _profileCombo.Items.Add(new ComboBoxItem { Content = "(any profile)", Tag = "" });
        foreach (var p in profiles)
            _profileCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
        var preProf = existing?.ProfileName ?? "";
        foreach (ComboBoxItem it in _profileCombo.Items)
        {
            if ((string)(it.Tag ?? "") == preProf) { _profileCombo.SelectedItem = it; break; }
        }
        if (_profileCombo.SelectedItem is null && _profileCombo.Items.Count > 0)
            _profileCombo.SelectedIndex = 0;
        stack.Children.Add(_profileCombo);

        // Status
        stack.Children.Add(MakeLabel("Status"));
        _statusCombo = new ComboBox
        {
            ItemsSource = VaultKinds.Statuses,
            SelectedValue = existing?.Status ?? "active",
            Margin = new Thickness(0, 0, 0, 12),
        };
        stack.Children.Add(_statusCombo);

        // Tags
        stack.Children.Add(MakeLabel("Tags (comma-separated)"));
        _tagsField = MakeTextBox(JoinTags(existing?.TagsJson));
        stack.Children.Add(_tagsField);

        // Notes (multi-line)
        stack.Children.Add(MakeLabel("Notes (plaintext)"));
        _notesField = new TextBox
        {
            Text = existing?.Notes ?? "",
            AcceptsReturn = true,
            MinHeight = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_notesField);

        // Secrets panel (rebuilt on kind change)
        var secretsHeader = new TextBlock
        {
            Text = "🔒  ENCRYPTED SECRETS",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 8),
        };
        secretsHeader.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        stack.Children.Add(secretsHeader);
        _secretsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_secretsPanel);
        RebuildSecretFields();

        _errorLabel = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _errorLabel.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        stack.Children.Add(_errorLabel);

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
            Content = existing is null ? "Create" : "Save",
            MinWidth = 110, IsDefault = true,
        };
        _okBtn.SetResourceReference(StyleProperty, "ButtonPrimary");
        _okBtn.Click += async (_, _) => await SaveAsync();
        btns.Children.Add(cancel);
        btns.Children.Add(_okBtn);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        // Phase 26 audit fix — wipe any cleartext password TextBox /
        // PasswordBox on close. The reveal toggle leaves the secret in
        // BOTH controls (PasswordBox for masked mode, TextBox for the
        // reveal mode); without this, a stale dialog reference held
        // by GC roots would surface the cleartext via reflection.
        Closed += (_, _) => WipeSecretsPanel(_secretsPanel);
    }

    private static void WipeSecretsPanel(DependencyObject? d)
    {
        if (d is null) return;
        if (d is PasswordBox pwb)
        {
            try { pwb.Clear(); } catch { /* ignore */ }
            return;
        }
        if (d is TextBox tb && tb.Tag is string)
        {
            try { tb.Clear(); } catch { /* ignore */ }
            return;
        }
        if (d is Panel p) foreach (var ch in p.Children) WipeSecretsPanel(ch as DependencyObject);
        if (d is Grid g)  foreach (var ch in g.Children) WipeSecretsPanel(ch as DependencyObject);
    }

    // ─── Field rebuild ───────────────────────────────────────────

    private void RebuildSecretFields()
    {
        _secretsPanel.Children.Clear();
        var kind = (_kindCombo.SelectedValue as string) ?? "account";
        var spec = VaultKinds.Get(kind);
        var fields = spec?.Fields ?? Array.Empty<string>();

        if (fields.Length == 0 && string.Equals(kind, "custom", StringComparison.OrdinalIgnoreCase))
        {
            // Custom kind — render a free-form key/value editor.
            var help = new TextBlock
            {
                Text = "Custom kind — paste a JSON object with your own field names.",
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 6),
            };
            help.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            _secretsPanel.Children.Add(help);
            var customJson = new TextBox
            {
                Name = "_customJson",
                AcceptsReturn = true,
                AcceptsTab = true,
                Padding = new Thickness(8, 6, 8, 6),
                MinHeight = 120,
                FontFamily = (FontFamily)Application.Current.Resources["FontMono"],
                Text = SerializeExisting(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            _secretsPanel.Children.Add(customJson);
            return;
        }

        foreach (var fname in fields)
        {
            _secretsPanel.Children.Add(MakeLabel(PrettyFieldName(fname)));
            var initial = _existingClear is not null && _existingClear.TryGetValue(fname, out var v) ? v : "";
            // Multi-line for seed_phrase / private_key / recovery / body / notes-like fields.
            if (fname is "seed_phrase" or "private_key" or "recovery" or "body" or "session_cookie")
            {
                var tb = new TextBox
                {
                    Text = initial,
                    AcceptsReturn = true,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 12),
                    MinHeight = 60,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    TextWrapping = TextWrapping.Wrap,
                    Tag = fname,
                };
                _secretsPanel.Children.Add(tb);
            }
            else if (fname is "password" or "wallet_password")
            {
                var pwHost = new Grid();
                pwHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pwHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pwHost.Margin = new Thickness(0, 0, 0, 12);
                // Use TextBox with password masking via PasswordChar would
                // require a more complex control; for simplicity show a
                // PasswordBox + reveal button.
                var pw = new PasswordBox
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    Tag = fname,
                };
                pw.Password = initial;
                Grid.SetColumn(pw, 0);
                pwHost.Children.Add(pw);

                var reveal = new ToggleButton
                {
                    Content = "👁",
                    Width = 32,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = "Reveal",
                };
                Grid.SetColumn(reveal, 1);
                var plainBox = new TextBox
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    Visibility = Visibility.Collapsed,
                    Tag = fname,
                    Text = initial,
                };
                Grid.SetColumn(plainBox, 0);
                pwHost.Children.Add(plainBox);
                reveal.Checked   += (_, _) => { plainBox.Text = pw.Password; plainBox.Visibility = Visibility.Visible; pw.Visibility = Visibility.Collapsed; };
                reveal.Unchecked += (_, _) => { pw.Password = plainBox.Text;   pw.Visibility = Visibility.Visible; plainBox.Visibility = Visibility.Collapsed; };
                _secretsPanel.Children.Add(pwHost);
            }
            else
            {
                var tb = new TextBox
                {
                    Text = initial,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 12),
                    Tag = fname,
                };
                _secretsPanel.Children.Add(tb);
            }
        }
    }

    private string SerializeExisting()
    {
        if (_existingClear is null || _existingClear.Count == 0) return "{}";
        return JsonSerializer.Serialize(_existingClear, new JsonSerializerOptions { WriteIndented = true });
    }

    private Dictionary<string, string> CollectSecrets()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var kind = (_kindCombo.SelectedValue as string) ?? "account";
        if (string.Equals(kind, "custom", StringComparison.OrdinalIgnoreCase))
        {
            // Parse the JSON textbox. Phase 26 audit fix — invalid JSON
            // throws now (was silently saving empty secrets); SaveAsync
            // catches and surfaces the parser message to the user so
            // they don't end up with a "saved" item with zero fields.
            foreach (var c in _secretsPanel.Children)
            {
                if (c is TextBox tb && tb.Name == "_customJson")
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(tb.Text) ? "{}" : tb.Text);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException(
                            "Custom kind needs a JSON object at the top level " +
                            "(e.g. {\"api_key\": \"...\"}).");
                    foreach (var kv in doc.RootElement.EnumerateObject())
                    {
                        dict[kv.Name] = kv.Value.ValueKind switch
                        {
                            JsonValueKind.String => kv.Value.GetString() ?? "",
                            JsonValueKind.Number => kv.Value.GetRawText(),
                            JsonValueKind.True   => "true",
                            JsonValueKind.False  => "false",
                            _                    => kv.Value.GetRawText(),
                        };
                    }
                    return dict;
                }
            }
            return dict;
        }
        // Fixed-schema kind: each child has Tag = field name. We have
        // mixed control types (TextBox / PasswordBox / Grid wrapping
        // both). Walk recursively to collect the live value.
        foreach (var c in _secretsPanel.Children)
        {
            CollectControlValue(c as DependencyObject, dict);
        }
        return dict;
    }

    private static void CollectControlValue(DependencyObject? d, Dictionary<string, string> dict)
    {
        if (d is null) return;
        if (d is PasswordBox pwb && pwb.Tag is string pname && pwb.Visibility == Visibility.Visible)
        {
            dict[pname] = pwb.Password ?? "";
            return;
        }
        if (d is TextBox tb && tb.Tag is string tname && tb.Visibility == Visibility.Visible)
        {
            // Don't double-write: a Grid-wrapped password may contain
            // both the TextBox (reveal) and PasswordBox; the visibility
            // check above + on the PasswordBox path picks exactly one.
            if (!dict.ContainsKey(tname))
                dict[tname] = tb.Text ?? "";
            return;
        }
        // Recurse into Grid / StackPanel children.
        if (d is Grid g)
            foreach (var ch in g.Children) CollectControlValue(ch as DependencyObject, dict);
        if (d is Panel p)
            foreach (var ch in p.Children) CollectControlValue(ch as DependencyObject, dict);
    }

    // ─── Save ────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        _errorLabel.Visibility = Visibility.Collapsed;
        _okBtn.IsEnabled = false;
        try
        {
            var name = (_nameField.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowError("Name is required.");
                return;
            }
            var kind = (_kindCombo.SelectedValue as string) ?? "account";
            var status = (_statusCombo.SelectedValue as string) ?? "active";
            var profile = (_profileCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(profile)) profile = null;
            var tagsJson = SerialiseTags(_tagsField.Text);

            // Phase 26 audit fix — wrap with friendly message so a JSON
            // parse error surfaces as a label, not a stack trace.
            Dictionary<string, string> clear;
            try { clear = CollectSecrets(); }
            catch (JsonException jx)
            {
                ShowError("Custom JSON didn't parse: " + jx.Message);
                return;
            }
            catch (ArgumentException ax)
            {
                ShowError(ax.Message);
                return;
            }

            var item = (_existing ?? new VaultItem { Name = name }) with
            {
                Name        = name,
                Kind        = kind,
                Service     = NullIfBlank(_serviceField.Text),
                Identifier  = NullIfBlank(_identifierField.Text),
                ProfileName = profile,
                Status      = status,
                TagsJson    = tagsJson,
                Notes       = NullIfBlank(_notesField.Text),
            };
            if (_existing is null)
                Saved = await _vault.CreateAsync(item, clear);
            else
            {
                await _vault.UpdateAsync(item, clear);
                Saved = item;
            }
            DialogResult = true;
            Close();
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

    // ─── Helpers ─────────────────────────────────────────────────

    private static string? NullIfBlank(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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

    private static TextBox MakeTextBox(string initial)
        => new TextBox
        {
            Text = initial,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 12),
        };

    private static string PrettyFieldName(string n)
        => string.Join(' ',
            n.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string JoinTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return tagsJson;
            return string.Join(", ",
                doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? ""));
        }
        catch { return tagsJson; }
    }

    private static string? SerialiseTags(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                                  | StringSplitOptions.TrimEntries);
        return JsonSerializer.Serialize(parts);
    }
}
