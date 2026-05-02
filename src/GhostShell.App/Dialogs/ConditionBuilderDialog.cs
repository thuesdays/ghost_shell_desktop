// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 22 hot-fix — pickable condition editor for if / while_loop
/// nodes. Lists every kind the runtime's <c>ConditionEvaluator</c>
/// understands, renders the relevant param fields when one is
/// chosen, and exposes a "Custom JSON" mode for power users (and / or
/// / not compound trees the picker can't represent yet).
///
/// Returns the resulting condition JSON via <see cref="Result"/> —
/// callers paste this into the step's <c>condition</c> field. Format
/// matches what <c>ConditionEvaluator</c> deserialises:
/// <code>{ "kind": "var_equals", "params": { "name": "x", "value": "5" } }</code>
/// </summary>
public sealed class ConditionBuilderDialog : Window
{
    public string? Result { get; private set; }

    private readonly ComboBox _kindCombo;
    private readonly StackPanel _paramsPanel;
    private readonly TextBox _customJsonField;
    private readonly TabControl _tabs;
    private readonly TabItem _formTab;
    private readonly TabItem _customTab;

    /// <summary>
    /// Catalog mirrors <c>ConditionEvaluator</c>. Each entry: kind,
    /// human label, brief description, and the params (name, label,
    /// hint) the runtime reads from the <c>params</c> object.
    /// </summary>
    private static readonly KindSpec[] Catalog =
    {
        new("true",             "Always true",
            "Unconditional pass — branch always taken."),
        new("false",            "Always false",
            "Unconditional skip — branch never taken."),
        new("var_equals",       "Variable == value",
            "True when {{var}} equals the literal value (string compare).",
            new ParamSpec("name",  "Variable name", "e.g. country, attempt"),
            new ParamSpec("value", "Expected value","string compare, exact")),
        new("var_exists",       "Variable is set",
            "True when the named variable has been saved by an earlier step.",
            new ParamSpec("name",  "Variable name", "e.g. last_extract")),
        new("var_matches",      "Variable matches regex",
            "True when {{var}} matches the regex pattern (200-ms timeout).",
            new ParamSpec("name",    "Variable name", ""),
            new ParamSpec("pattern", "Regex pattern", "e.g. ^[A-Z]{2}\\d+$")),
        new("has_ads",          "Ads parsed",
            "True when ctx.Ads has at least one entry (after parse_ads)."),
        new("ads_count_gte",    "Ad count ≥ N",
            "True when ctx.Ads.Count >= N.",
            new ParamSpec("n", "Min count", "integer")),
        new("url_contains",     "URL contains text",
            "True when document.location.href contains the substring.",
            new ParamSpec("needle", "Substring", "case-insensitive")),
        new("url_matches",      "URL matches regex",
            "True when document.location.href matches the regex.",
            new ParamSpec("pattern", "Regex pattern", "")),
        new("title_contains",   "Page title contains text",
            "True when document.title contains the substring.",
            new ParamSpec("needle", "Substring", "case-insensitive")),
        new("selector_present", "Selector present in DOM",
            "True when document.querySelector(selector) returns truthy.",
            new ParamSpec("selector", "CSS selector", "")),
        new("selector_visible", "Selector visible",
            "True when querySelector returns truthy AND el.offsetParent != null.",
            new ParamSpec("selector", "CSS selector", "")),
        new("captcha_visible",  "Captcha visible",
            "Heuristic — looks for reCAPTCHA / hCaptcha / Cloudflare iframes."),
        new("random",           "Random (probability)",
            "True with the given probability (0..1).",
            new ParamSpec("probability", "Probability 0..1", "0.5 = 50%")),
        new("ad_is_mine",       "Current ad on MY domain",
            "(inside foreach_ad) True when the current ad's domain is in profile.MyDomains."),
        new("ad_is_target",     "Current ad on TARGET domain",
            "(inside foreach_ad) True when the current ad's domain is in profile.TargetDomains."),
        new("ad_is_external",   "Current ad is external",
            "(inside foreach_ad) True when the current ad's domain is NOT in profile.MyDomains."),
        new("ad_is_competitor", "Current ad is competitor",
            "(inside foreach_ad) True when the ad is NEITHER mine NOR target."),
        new("own_domain",       "URL is on profile's OWN domain",
            "True when the given href's host matches the live page's host.",
            new ParamSpec("href", "URL (or {{ad_href}})", "absolute URL")),
    };

    public ConditionBuilderDialog(string? currentConditionJson)
    {
        Title = "Condition";
        Width = 640;
        Height = 540;
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
        var t1 = new TextBlock
        {
            Text = "Condition",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var t2 = new TextBlock
        {
            Text = "Pick a built-in kind from the dropdown — the params will appear below. " +
                   "Use Custom JSON for compound (and/or/not) trees.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(t1);
        head.Children.Add(t2);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── Tabs: Form / Custom JSON ──
        _tabs = new TabControl();
        _formTab = new TabItem { Header = "Form" };
        _customTab = new TabItem { Header = "Custom JSON" };
        _tabs.Items.Add(_formTab);
        _tabs.Items.Add(_customTab);
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        // ── Form tab ──
        var formGrid = new Grid { Margin = new Thickness(8) };
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = "Condition kind",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            FontWeight = FontWeights.SemiBold,
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(lbl, 0);
        formGrid.Children.Add(lbl);

        _kindCombo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = Catalog,
            DisplayMemberPath = "Label",
        };
        _kindCombo.SelectionChanged += (_, _) => RebuildParams();
        Grid.SetRow(_kindCombo, 1);
        formGrid.Children.Add(_kindCombo);

        var descBlock = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Name = "DescBlock",
        };
        descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(descBlock, 2);
        formGrid.Children.Add(descBlock);

        var paramsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _paramsPanel = new StackPanel();
        paramsScroll.Content = _paramsPanel;
        Grid.SetRow(paramsScroll, 3);
        formGrid.Children.Add(paramsScroll);
        _formTab.Content = formGrid;

        // ── Custom JSON tab ──
        var customGrid = new Grid { Margin = new Thickness(8) };
        customGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        customGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var customHelp = new TextBlock
        {
            Text = "Raw JSON shape: { \"kind\": \"...\", \"params\": {...}, \"children\": [...] }. " +
                   "Children only used by 'and' / 'or' / 'not' compounds.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        customHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(customHelp, 0);
        customGrid.Children.Add(customHelp);
        _customJsonField = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = (FontFamily)Application.Current.Resources["FontMono"],
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
        };
        Grid.SetRow(_customJsonField, 1);
        customGrid.Children.Add(_customJsonField);
        _customTab.Content = customGrid;

        // ── Footer ──
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "OK", MinWidth = 100, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "ButtonPrimary");
        ok.Click += OnSave;
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        // Pre-load existing condition.
        LoadExisting(currentConditionJson);
    }

    private void RebuildParams()
    {
        _paramsPanel.Children.Clear();
        // Update description.
        if (_kindCombo.SelectedItem is KindSpec spec)
        {
            // Find the desc block (first descendant TextBlock with Name=DescBlock)
            var desc = FindByName<TextBlock>(_formTab.Content as DependencyObject, "DescBlock");
            if (desc is not null) desc.Text = spec.Description;
            foreach (var p in spec.Params)
            {
                var l = new TextBlock
                {
                    Text = p.Label,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 4),
                    FontWeight = FontWeights.SemiBold,
                };
                l.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                _paramsPanel.Children.Add(l);
                var t = new TextBox
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    Tag = p.Name,
                    ToolTip = p.Hint,
                };
                _paramsPanel.Children.Add(t);
                if (!string.IsNullOrEmpty(p.Hint))
                {
                    var h = new TextBlock
                    {
                        Text = p.Hint,
                        FontSize = 10,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    h.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                    _paramsPanel.Children.Add(h);
                }
            }
            if (spec.Params.Length == 0)
            {
                var none = new TextBlock
                {
                    Text = "(no params for this kind)",
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                none.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                _paramsPanel.Children.Add(none);
            }
        }
    }

    private static T? FindByName<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        if (root is T fe && fe.Name == name) return fe;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    private void LoadExisting(string? json)
    {
        // Empty / null → default to "true" kind.
        if (string.IsNullOrWhiteSpace(json))
        {
            _kindCombo.SelectedIndex = 0;
            return;
        }

        // Stash the raw text in the Custom tab regardless — power users
        // can flip there and edit verbatim.
        _customJsonField.Text = PrettyJson(json);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _tabs.SelectedItem = _customTab;
                return;
            }
            var kind = doc.RootElement.TryGetProperty("kind", out var k)
                ? k.GetString() ?? "" : "";
            var spec = Array.Find(Catalog, x => x.Kind == kind);
            if (spec is null)
            {
                // Compound (and/or/not) or unknown — Custom tab.
                _tabs.SelectedItem = _customTab;
                return;
            }
            _kindCombo.SelectedItem = spec;
            RebuildParams();
            // Hydrate param TextBoxes.
            if (doc.RootElement.TryGetProperty("params", out var pe)
                && pe.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in pe.EnumerateObject())
                {
                    var tb = FindParamBox(kv.Name);
                    if (tb is null) continue;
                    tb.Text = kv.Value.ValueKind switch
                    {
                        JsonValueKind.String => kv.Value.GetString() ?? "",
                        JsonValueKind.Number => kv.Value.GetRawText(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        _                    => kv.Value.GetRawText(),
                    };
                }
            }
        }
        catch
        {
            // JSON malformed — drop into Custom tab so user can fix.
            _tabs.SelectedItem = _customTab;
        }
    }

    private TextBox? FindParamBox(string name)
    {
        foreach (var c in _paramsPanel.Children)
            if (c is TextBox tb && tb.Tag is string n && n == name) return tb;
        return null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_tabs.SelectedItem, _customTab))
        {
            // Validate JSON before accepting.
            try
            {
                using var doc = JsonDocument.Parse(_customJsonField.Text);
                Result = JsonSerializer.Serialize(doc.RootElement);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Invalid JSON: " + ex.Message,
                    "Condition", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        if (_kindCombo.SelectedItem is not KindSpec spec)
        {
            MessageBox.Show(this, "Pick a condition kind first.",
                "Condition", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("kind", spec.Kind);
            if (spec.Params.Length > 0)
            {
                w.WritePropertyName("params");
                w.WriteStartObject();
                foreach (var p in spec.Params)
                {
                    var box = FindParamBox(p.Name);
                    var val = (box?.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(val)) continue;
                    // Numbers — try int/double; otherwise string.
                    if (int.TryParse(val, out var iv))      w.WriteNumber(p.Name, iv);
                    else if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var dv))
                        w.WriteNumber(p.Name, dv);
                    else                                    w.WriteString(p.Name, val);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        Result = Encoding.UTF8.GetString(ms.ToArray());
        DialogResult = true;
        Close();
    }

    private static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch { return raw; }
    }

    // ─── Catalog row ──────────────────────────────────────────────

    private sealed record KindSpec(
        string Kind, string Label, string Description, params ParamSpec[] Params);

    private sealed record ParamSpec(string Name, string Label, string Hint);
}
