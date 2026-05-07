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
    private readonly TextBox _emailField;
    private readonly ComboBox _profileCombo;
    private readonly ComboBox _statusCombo;
    private readonly TextBox _tagsField;
    private readonly TextBox _notesField;
    private readonly StackPanel _secretsPanel;
    private readonly Button _okBtn;
    private readonly TextBlock _errorLabel;

    /// <summary>
    /// Phase 71 — per-field metadata accumulated during this editor
    /// session (encrypted? is_totp?). Pre-populated from the existing
    /// item's <see cref="VaultItem.FieldMetaJson"/> so re-edits keep
    /// the user's prior choices. New "+ Add custom field" rows append
    /// here. Canonical universal fields (Name/Email/Tags/Notes/etc.)
    /// don't get a row — only true custom keys.
    /// </summary>
    private readonly Dictionary<string, VaultFieldMeta> _fieldMeta = new(StringComparer.Ordinal);

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
            // Phase 71 — text updated to reflect the universal model.
            // Kind dropdown is now mostly informational (legacy items
            // keep their kind; new ones default to "universal" which
            // accepts arbitrary user-defined fields).
            Text = "Plaintext metadata (Name, Email, Tags, Notes) is searchable without unlocking. " +
                   "Secret fields below are encrypted at rest. Use \"+ Add custom field\" to extend " +
                   "this item with arbitrary keys (Twitter, Discord, wallets, codes — anything).",
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

        // Phase 71 — Kind defaults to "universal" for new items. The
        // dropdown is hidden when kind=universal (the new default) so
        // the form stays clean for the common case; legacy items
        // (account/social/crypto_wallet/…) still surface the dropdown
        // so users can keep editing them in the legacy mode.
        var initialKind = existing?.Kind ?? "universal";
        var isUniversal = string.Equals(initialKind, "universal", StringComparison.OrdinalIgnoreCase);

        var kindLabel = MakeLabel("Kind");
        kindLabel.Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible;
        stack.Children.Add(kindLabel);
        _kindCombo = new ComboBox
        {
            ItemsSource = VaultKinds.Catalog,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Id",
            SelectedValue = initialKind,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible,
        };
        _kindCombo.SelectionChanged += (_, _) => RebuildSecretFields();
        stack.Children.Add(_kindCombo);

        // Phase 71 — Email is now a first-class plaintext field. Lives
        // in vault_items.email so it's searchable from the list view
        // without unlocking. The matching email_password lands in the
        // encrypted secrets section below.
        stack.Children.Add(MakeLabel("Email (plaintext, searchable)"));
        _emailField = MakeTextBox(existing?.Email ?? "");
        _emailField.ToolTip = "Searchable email address. The matching password lives below as " +
                              "an encrypted field.";
        stack.Children.Add(_emailField);

        // Service / identifier (plaintext metadata) — hidden in
        // universal mode to keep the form short. Still persisted /
        // round-tripped when present on legacy items.
        var serviceLabel = MakeLabel("Service slug (optional)");
        serviceLabel.Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible;
        stack.Children.Add(serviceLabel);
        _serviceField = MakeTextBox(existing?.Service ?? "");
        _serviceField.ToolTip = "e.g. google, github, aws — used for search + UI grouping";
        _serviceField.Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible;
        stack.Children.Add(_serviceField);

        var idLabel = MakeLabel("Identifier (optional, plaintext)");
        idLabel.Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible;
        stack.Children.Add(idLabel);
        _identifierField = MakeTextBox(existing?.Identifier ?? "");
        _identifierField.ToolTip = "Searchable label like an email or address — visible without unlock";
        _identifierField.Visibility = isUniversal ? Visibility.Collapsed : Visibility.Visible;
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

        // Phase 71d — secrets panel now owns its own section headers
        // ("🔒 ENCRYPTED SECRETS" + optionally "📄 PLAINTEXT FIELDS")
        // because we split the rows into two visually-distinct groups
        // based on per-field meta. Without the split, plaintext custom
        // fields appeared under the "encrypted" header which lied to
        // the user about how their data is stored.
        _secretsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_secretsPanel);

        // Phase 71 — pre-load per-field metadata so re-edits keep prior
        // encrypted/totp choices. Parse defensively: a corrupt JSON
        // payload shouldn't prevent the user from opening + saving the
        // item (they may want to fix the data, not be locked out).
        if (!string.IsNullOrWhiteSpace(existing?.FieldMetaJson))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<
                    Dictionary<string, VaultFieldMeta>>(existing!.FieldMetaJson!);
                if (parsed is not null)
                    foreach (var kv in parsed) _fieldMeta[kv.Key] = kv.Value;
            }
            catch { /* ignore — fall back to defaults at save time */ }
        }

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

        // Phase 70b — collect ALL keys to render: kind's canonical fields
        // PLUS any extra keys present in the existing item's secrets bag
        // that aren't part of the canonical schema (custom fields added
        // earlier via bulk import or a previous "+ Add custom field"
        // round). This keeps the editor non-destructive: opening + saving
        // a vault item never silently drops user-defined keys.
        var renderFields = new List<string>(fields);
        if (_existingClear is not null)
        {
            foreach (var key in _existingClear.Keys)
            {
                if (!renderFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                    renderFields.Add(key);
            }
        }

        // Phase 71d — split into encrypted vs plaintext groups so each
        // gets its own section header. A field is plaintext ONLY when
        // _fieldMeta has an entry with Encrypted=false AND IsTotp=false.
        // Anything else (including kind-canonical secrets without a
        // meta entry, like "password" / "seed_phrase") is treated as
        // encrypted by default.
        var encryptedKeys = new List<string>();
        var plaintextKeys = new List<string>();
        foreach (var fname in renderFields)
        {
            var meta = _fieldMeta.TryGetValue(fname, out var m) ? m : null;
            if (meta is not null && !meta.Encrypted && !meta.IsTotp)
                plaintextKeys.Add(fname);
            else
                encryptedKeys.Add(fname);
        }

        if (encryptedKeys.Count > 0)
        {
            _secretsPanel.Children.Add(MakeSectionHeader("🔒  ENCRYPTED SECRETS"));
            foreach (var fname in encryptedKeys)
                RenderFieldRow(fname, isEncrypted: true);
        }

        if (plaintextKeys.Count > 0)
        {
            _secretsPanel.Children.Add(MakeSectionHeader("📄  PLAINTEXT FIELDS"));
            foreach (var fname in plaintextKeys)
                RenderFieldRow(fname, isEncrypted: false);
        }

        // Phase 70b — "+ Add custom field" button at the bottom of the
        // secrets panel. Lets the user grow the schema for THIS item
        // without switching to "custom" kind (which loses the kind-aware
        // form). New fields land as plain TextBox rows tagged with the
        // user-supplied key; CollectSecrets() picks them up just like
        // the canonical ones.
        var addRow = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var addBtn = new Button
        {
            Content = "+ Add custom field",
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Add an extra secret field to this vault item — usable in scripts " +
                      "as {{vault.<field_name>}} for the bound profile.",
        };
        addBtn.SetResourceReference(StyleProperty, "ButtonGhost");
        addBtn.Click += (_, _) => OnAddCustomField();
        Grid.SetColumn(addBtn, 0);
        addRow.Children.Add(addBtn);
        _secretsPanel.Children.Add(addRow);
    }

    /// <summary>
    /// Phase 71d — section header inside the secrets panel. Used to
    /// title the encrypted vs plaintext groups so the user can tell
    /// at a glance which fields are stored ciphertext vs plaintext.
    /// Style mirrors the original "ENCRYPTED SECRETS" header.
    /// </summary>
    private static TextBlock MakeSectionHeader(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 8),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    /// <summary>
    /// Phase 71d — render one field row into <see cref="_secretsPanel"/>.
    /// Extracted from the previous inline foreach so the encrypted
    /// and plaintext sections can share the same layout but pick a
    /// suitable control:
    ///
    ///   • <paramref name="isEncrypted"/>=true:
    ///       - PasswordBox + reveal toggle for known password keys
    ///         (password / wallet_password)
    ///       - multi-line TextBox for seed_phrase / private_key /
    ///         recovery / body / session_cookie
    ///       - single-line TextBox otherwise
    ///   • <paramref name="isEncrypted"/>=false:
    ///       - always single-line TextBox; plaintext fields are
    ///         visible by definition, no PasswordBox needed.
    /// </summary>
    private void RenderFieldRow(string fname, bool isEncrypted)
    {
        var labelText = PrettyFieldName(fname);
        if (_fieldMeta.TryGetValue(fname, out var metaForLabel))
            labelText += BadgeSuffix(metaForLabel.Encrypted, metaForLabel.IsTotp);
        _secretsPanel.Children.Add(MakeLabel(labelText));
        var initial = _existingClear is not null && _existingClear.TryGetValue(fname, out var v) ? v : "";

        if (!isEncrypted)
        {
            // Plaintext — render a regular TextBox unconditionally.
            var tb = new TextBox
            {
                Text = initial,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 12),
                Tag = fname,
            };
            _secretsPanel.Children.Add(tb);
            return;
        }

        // Encrypted — pick the right control type for this field.
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
            var pw = new PasswordBox { Padding = new Thickness(8, 6, 8, 6), Tag = fname };
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
            pwHost.Children.Add(reveal);
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

    /// <summary>
    /// Phase 70b — append a new secret field to the live editor.
    /// Reuses the bulk-import dialog's <see cref="InputDialog"/> for
    /// consistency. Field name is normalised to a placeholder-safe
    /// snake_case identifier; collisions with existing rows reselect
    /// (focus) the existing row instead of duplicating.
    /// </summary>
    private void OnAddCustomField()
    {
        // Phase 71 — richer dialog with Encrypted (default ON) and
        // Is TOTP code (default OFF) checkboxes. Encrypted controls
        // both at-rest encryption AND log redaction; is_totp marks the
        // value as a TOTP seed so {{vault.X}} returns a live 6-digit
        // code instead of the raw seed at script execution time.
        var prompt = new CustomFieldDialog { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var name = VaultBulkImportDialog.NormaliseFieldName(prompt.FieldName);
        if (string.IsNullOrEmpty(name)) return;
        // is_totp implies encrypted — never store a TOTP seed plaintext.
        var encrypted = prompt.Encrypted || prompt.IsTotp;
        var isTotp    = prompt.IsTotp;

        // Collision: focus existing TextBox/PasswordBox with that Tag.
        foreach (var child in _secretsPanel.Children)
        {
            if (child is TextBox existing && existing.Tag is string t1
                && string.Equals(t1, name, StringComparison.OrdinalIgnoreCase))
            { existing.Focus(); return; }
            if (child is PasswordBox p1 && p1.Tag is string t2
                && string.Equals(t2, name, StringComparison.OrdinalIgnoreCase))
            { p1.Focus(); return; }
        }

        // Stamp metadata so the save-time router knows where this
        // field's value belongs (encrypted vs extras_json) and whether
        // to mark it as a TOTP source.
        _fieldMeta[name] = new VaultFieldMeta { Encrypted = encrypted, IsTotp = isTotp };

        // Insert before the "+ Add custom field" row (last child).
        var insertAt = Math.Max(0, _secretsPanel.Children.Count - 1);
        _secretsPanel.Children.Insert(insertAt, MakeLabel(PrettyFieldName(name) + BadgeSuffix(encrypted, isTotp)));

        // Encrypted fields get a PasswordBox with a reveal toggle for
        // consistency with the built-in password fields. Plaintext
        // custom fields get a regular TextBox.
        if (encrypted)
        {
            var pwHost = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            pwHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pwHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pw = new PasswordBox { Padding = new Thickness(8, 6, 8, 6), Tag = name };
            Grid.SetColumn(pw, 0);
            pwHost.Children.Add(pw);
            var reveal = new ToggleButton { Content = "👁", Width = 32, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Reveal" };
            Grid.SetColumn(reveal, 1);
            var plainBox = new TextBox
            {
                Padding = new Thickness(8, 6, 8, 6),
                Visibility = Visibility.Collapsed,
                Tag = name,
            };
            Grid.SetColumn(plainBox, 0);
            pwHost.Children.Add(plainBox);
            pwHost.Children.Add(reveal);
            reveal.Checked   += (_, _) => { plainBox.Text = pw.Password; plainBox.Visibility = Visibility.Visible; pw.Visibility = Visibility.Collapsed; };
            reveal.Unchecked += (_, _) => { pw.Password = plainBox.Text;   pw.Visibility = Visibility.Visible; plainBox.Visibility = Visibility.Collapsed; };
            _secretsPanel.Children.Insert(insertAt + 1, pwHost);
            pw.Focus();
        }
        else
        {
            var tb = new TextBox
            {
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 12),
                Tag = name,
            };
            _secretsPanel.Children.Insert(insertAt + 1, tb);
            tb.Focus();
        }
    }

    /// <summary>Phase 71 — small visual annotation appended to a custom
    /// field's label so the user can see the field's encrypted/TOTP
    /// status at a glance.</summary>
    private static string BadgeSuffix(bool encrypted, bool isTotp)
    {
        var bits = new List<string>();
        if (isTotp)        bits.Add("⏱ TOTP");
        if (!encrypted)    bits.Add("📄 plaintext");
        return bits.Count == 0 ? "" : "  (" + string.Join(", ", bits) + ")";
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

            // Phase 71 — split clear into encrypted secrets + plaintext
            // extras based on _fieldMeta. Built-in canonical keys stay
            // encrypted (email_password) or plaintext (email/name/etc.)
            // by their nature; only user-added custom fields consult
            // the per-field meta.
            var encryptedClear = new Dictionary<string, string>(StringComparer.Ordinal);
            var plaintextExtras = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in clear)
            {
                if (_fieldMeta.TryGetValue(kv.Key, out var meta) && !meta.Encrypted && !meta.IsTotp)
                    plaintextExtras[kv.Key] = kv.Value;
                else
                    encryptedClear[kv.Key] = kv.Value;
            }
            string? extrasJson = plaintextExtras.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(plaintextExtras);
            string? fieldMetaJson = _fieldMeta.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(_fieldMeta);

            var item = (_existing ?? new VaultItem { Name = name }) with
            {
                Name          = name,
                Kind          = kind,
                Service       = NullIfBlank(_serviceField.Text),
                Identifier    = NullIfBlank(_identifierField.Text),
                Email         = NullIfBlank(_emailField.Text),
                ProfileName   = profile,
                Status        = status,
                TagsJson      = tagsJson,
                Notes         = NullIfBlank(_notesField.Text),
                FieldMetaJson = fieldMetaJson,
                ExtrasJson    = extrasJson,
            };
            if (_existing is null)
                Saved = await _vault.CreateAsync(item, encryptedClear);
            else
            {
                await _vault.UpdateAsync(item, encryptedClear);
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

/// <summary>
/// Phase 71 — input dialog for the universal editor's "+ Add custom
/// field" flow. Asks for a field name and exposes two flags that
/// drive runtime behaviour:
///
///   • <see cref="Encrypted"/> (default ON) — the value lives in the
///     encrypted secrets blob and is redacted from script logs.
///     Turn off only for non-secret tagging keys (device_id, hint).
///
///   • <see cref="IsTotp"/> (default OFF) — the value is a TOTP seed
///     (Base32). When referenced from a script via <c>{{vault.X}}</c>,
///     the runtime calls <c>Totp.Compute</c> on the seed and returns
///     the live 6-digit code. Implies Encrypted=true.
///
/// Both flags are surfaced in the universal editor dialog AND in the
/// bulk-import dialog when the user picks "+ Add custom field…".
/// Sharing one dialog keeps the UX uniform.
/// </summary>
internal sealed class CustomFieldDialog : Window
{
    public string FieldName { get; private set; } = "";
    public bool   Encrypted { get; private set; } = true;
    public bool   IsTotp    { get; private set; } = false;

    public CustomFieldDialog()
    {
        Title = "Add custom field";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new StackPanel { Margin = new Thickness(20) };

        var prompt = new TextBlock
        {
            Text = "Field name (lowercase letters, digits, underscores).\n" +
                   "Examples: discord_token, twitter_ct0, wallet_pin, totp_seed.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        root.Children.Add(prompt);

        var input = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(input);

        // Encrypted checkbox.
        var encryptedRow = new CheckBox
        {
            Content = "🔒  Encrypted at rest + redacted in script logs (recommended for secrets)",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "ON: value stored in the encrypted secrets blob and replaced with *** in script step logs. " +
                      "OFF: stored plaintext in the extras JSON column and logged verbatim — use only for " +
                      "non-sensitive tagging keys.",
        };
        root.Children.Add(encryptedRow);

        // TOTP checkbox.
        var totpRow = new CheckBox
        {
            Content = "⏱  Is TOTP code (treat value as Base32 seed and emit live 6-digit codes in scripts)",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 14),
            ToolTip = "ON: the field stores a TOTP secret. {{vault.<field>}} returns a freshly computed 6-digit code " +
                      "instead of the raw seed at script execution time. Implies encrypted (TOTP seeds are always sensitive).",
        };
        root.Children.Add(totpRow);

        // TOTP forces encrypted (semantically — and we re-stamp on OK
        // anyway, but visually flipping the checkbox makes the UX honest).
        totpRow.Checked += (_, _) =>
        {
            encryptedRow.IsChecked = true;
            encryptedRow.IsEnabled = false;
        };
        totpRow.Unchecked += (_, _) =>
        {
            encryptedRow.IsEnabled = true;
        };

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "Add", MinWidth = 80, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "ButtonPrimary");
        ok.Click += (_, _) =>
        {
            FieldName = input.Text ?? "";
            Encrypted = encryptedRow.IsChecked == true;
            IsTotp    = totpRow.IsChecked == true;
            DialogResult = true;
            Close();
        };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => input.Focus();
    }
}
