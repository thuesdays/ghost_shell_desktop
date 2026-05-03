// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Two-mode params editor: typed form (Phase 14) for actions in
/// <see cref="ScriptActionSchema"/>, raw JSON otherwise. The user
/// can flip between modes — switching from Form → JSON serialises
/// the form values; switching JSON → Form parses the JSON back into
/// fields where possible.
/// </summary>
public partial class ScriptStepParamsTypedDialog : Window
{
    public string? Result { get; private set; }

    /// <summary>
    /// When the action is a control-flow step (`if` / `while_loop`)
    /// this carries the edited condition JSON object, e.g.
    /// <c>{"kind":"ad_is_external"}</c>. Null for non-control-flow
    /// steps. Caller writes it back into the parent step's
    /// <c>condition</c> field on save.
    /// </summary>
    public string? ConditionResult { get; private set; }

    private readonly string _actionType;
    private readonly ObservableCollection<FieldRow> _rows = new();
    private readonly ObservableCollection<FieldRow> _condRows = new();

    /// <summary>True if the current action is `if` or `while_loop` —
    /// drives the condition panel's visibility.</summary>
    private readonly bool _wantsCondition;

    /// <summary>The condition's current "kind" (e.g. "url_contains").
    /// Updated by OnConditionKindChanged.</summary>
    private string _conditionKind = "true";

    /// <summary>For compound (and/or/not) conditions, the children
    /// JSON array is held verbatim and round-tripped through the
    /// JSON view — typed form doesn't expose a child editor.</summary>
    private string _conditionChildrenJson = "[]";

    /// <summary>
    /// Backwards-compat overload — non-control-flow steps don't have
    /// a condition. Just forwards to the full constructor.
    /// </summary>
    public ScriptStepParamsTypedDialog(string actionType, string currentJson)
        : this(actionType, currentJson, conditionJson: null) { }

    public ScriptStepParamsTypedDialog(
        string actionType, string currentJson, string? conditionJson)
    {
        InitializeComponent();
        _actionType   = actionType;
        _wantsCondition = string.Equals(actionType, "if", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(actionType, "while_loop", StringComparison.OrdinalIgnoreCase);

        TitleText.Text = $"Edit '{actionType}' params";
        SubText.Text   = ScriptActionSchema.HasSchema(actionType)
            ? (_wantsCondition
                ? "Typed form — pick the condition kind + fill its parameters, or flip to JSON"
                : "Typed form — flip to JSON for raw editing")
            : "No typed schema for this action — use JSON view";

        BuildFields(currentJson);
        FieldsList.ItemsSource = _rows;
        ConditionFieldsList.ItemsSource = _condRows;

        if (_wantsCondition)
        {
            ConditionPanel.Visibility = Visibility.Visible;
            BuildConditionEditor(conditionJson);
        }

        if (!ScriptActionSchema.HasSchema(actionType))
        {
            // Force JSON view for unknown actions.
            ViewJson.IsChecked = true;
        }
        // Pre-populate the JSON editor too so flipping is instant.
        JsonField.Text = PrettyJson(currentJson);
    }

    private void BuildFields(string currentJson)
    {
        var schema = ScriptActionSchema.Get(_actionType);
        if (schema.Count == 0)
        {
            // For if/while_loop the params object is empty by design
            // (the action's "shape" lives in the condition tree, not
            // in params). Don't say "no typed schema" — that's
            // misleading and was the source of the empty-form
            // confusion in earlier builds.
            HelpText.Text = _wantsCondition
                ? "This action has no params of its own — configure it via the Condition panel above."
                : ScriptActionSchema.HasSchema(_actionType)
                    ? "This action takes no parameters."
                    : "This action has no typed schema yet — edit as JSON.";
            return;
        }

        // Parse current JSON into a dict so we can pre-fill values.
        var currentValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(currentJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    currentValues[p.Name] = p.Value.Clone();
            }
        }
        catch { /* fall through with empty defaults */ }

        foreach (var field in schema)
        {
            var initial = currentValues.TryGetValue(field.Name, out var v)
                ? ValueToString(v)
                : field.DefaultValue;
            _rows.Add(FieldRow.Make(field, initial));
        }
        HelpText.Text = "Fill in the action's parameters. Required fields are marked.";
    }

    // ─── Condition editor ─────────────────────────────────────────
    //
    // The condition panel mirrors the runtime catalog defined in
    // ConditionEvaluator.cs. Adding a new kind here without wiring
    // it in the runtime evaluator gives a kind that always falls
    // through to `false` (the evaluator's safe-default for unknown
    // kinds) — so keep these two lists in sync.

    /// <summary>Display-friendly catalogue of supported condition
    /// kinds. The first half is constants/compounds; the second
    /// half is the "data" probes (vars, ads, URL, selector, etc.);
    /// the tail is ad-domain probes used inside foreach_ad.</summary>
    private static readonly (string Kind, string Label)[] _kinds = new[]
    {
        ("true",             "true (always)"),
        ("false",            "false (never)"),
        ("and",              "and (all children)"),
        ("or",               "or (any child)"),
        ("not",              "not (invert child)"),
        ("var_equals",       "var_equals — variable equals value"),
        ("var_exists",       "var_exists — variable defined"),
        ("var_matches",      "var_matches — variable matches regex"),
        ("has_ads",          "has_ads — SERP has ads"),
        ("ads_count_gte",    "ads_count_gte — at least N ads"),
        ("url_contains",     "url_contains — URL contains substring"),
        ("url_matches",      "url_matches — URL matches regex"),
        ("title_contains",   "title_contains — page title contains"),
        ("selector_present", "selector_present — element exists"),
        ("selector_visible", "selector_visible — element visible"),
        ("random",           "random — probability gate"),
        ("captcha_visible",  "captcha_visible — captcha on page"),
        ("ad_is_mine",       "ad_is_mine — ad on profile-owned domain"),
        ("ad_is_target",     "ad_is_target — ad on target domain"),
        ("ad_is_external",   "ad_is_external — ad NOT on profile domain"),
        ("ad_is_competitor", "ad_is_competitor — ad on neither own nor target"),
        ("own_domain",       "own_domain — explicit href matches page host"),
    };

    /// <summary>Per-kind parameter schema. Empty for kinds that take
    /// no params (true/false/has_ads/captcha_visible/ad_is_*).</summary>
    private static IReadOnlyList<ParamField> ConditionFieldsFor(string kind) => kind switch
    {
        "var_equals" => new[]
        {
            new ParamField("name",  "Variable name",   ParamFieldKind.String, "", Required: true),
            new ParamField("value", "Expected value",  ParamFieldKind.String, "", Required: true),
        },
        "var_exists" => new[]
        {
            new ParamField("name", "Variable name", ParamFieldKind.String, "", Required: true),
        },
        "var_matches" => new[]
        {
            new ParamField("name",    "Variable name",      ParamFieldKind.String, "", Required: true),
            new ParamField("pattern", "Regex pattern",      ParamFieldKind.String, "", Required: true),
        },
        "ads_count_gte" => new[]
        {
            new ParamField("n", "Min ad count", ParamFieldKind.Int, "1", Required: true),
        },
        "url_contains" => new[]
        {
            new ParamField("needle", "Substring to find", ParamFieldKind.String, "", Required: true),
        },
        "url_matches" => new[]
        {
            new ParamField("pattern", "Regex pattern", ParamFieldKind.String, "", Required: true),
        },
        "title_contains" => new[]
        {
            new ParamField("needle", "Title substring", ParamFieldKind.String, "", Required: true),
        },
        "selector_present" or "selector_visible" => new[]
        {
            new ParamField("selector", "CSS selector", ParamFieldKind.Selector, "", Required: true),
        },
        "random" => new[]
        {
            new ParamField("probability", "Probability (0–1)", ParamFieldKind.String, "0.5", Required: true),
        },
        "own_domain" => new[]
        {
            new ParamField("href", "Link href to test", ParamFieldKind.String, ""),
        },
        _ => Array.Empty<ParamField>(),
    };

    private void BuildConditionEditor(string? conditionJson)
    {
        // Populate the kind combo from the catalogue.
        ConditionKindCombo.ItemsSource = _kinds.Select(k => new ConditionKindItem(k.Kind, k.Label)).ToList();
        ConditionKindCombo.DisplayMemberPath = nameof(ConditionKindItem.Label);
        ConditionKindCombo.SelectedValuePath = nameof(ConditionKindItem.Kind);

        // Parse current condition JSON (or default to "true").
        var initialKind = "true";
        var initialParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _conditionChildrenJson = "[]";
        try
        {
            if (!string.IsNullOrWhiteSpace(conditionJson))
            {
                using var doc = JsonDocument.Parse(conditionJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("kind", out var kEl)
                        && kEl.ValueKind == JsonValueKind.String)
                        initialKind = kEl.GetString() ?? "true";
                    if (doc.RootElement.TryGetProperty("params", out var pEl)
                        && pEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in pEl.EnumerateObject())
                            initialParams[p.Name] = ValueToString(p.Value);
                    }
                    if (doc.RootElement.TryGetProperty("children", out var chEl)
                        && chEl.ValueKind == JsonValueKind.Array)
                        _conditionChildrenJson = chEl.GetRawText();
                }
            }
        }
        catch { /* fall through with default "true" kind */ }

        _conditionKind = initialKind;
        // Set the combo selection without firing the SelectionChanged
        // handler before _condRows is built (we'll build them right
        // after via RebuildConditionFields).
        var found = _kinds.FirstOrDefault(k => string.Equals(k.Kind, initialKind, StringComparison.OrdinalIgnoreCase));
        if (found.Kind is null)
        {
            // Unknown kind from JSON — fall back to "true" but keep
            // the JSON children/params intact for the round-trip.
            ConditionKindCombo.SelectedIndex = 0;
            _conditionKind = "true";
        }
        else
        {
            ConditionKindCombo.SelectedValue = found.Kind;
        }
        RebuildConditionFields(initialParams);
    }

    private void OnConditionKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ConditionKindCombo.SelectedItem is not ConditionKindItem item) return;
        _conditionKind = item.Kind;
        // Rebuild fields with empty defaults — switching kind shouldn't
        // pre-fill from the previous kind's values (those keys may not
        // even exist on the new kind).
        RebuildConditionFields(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private void RebuildConditionFields(IDictionary<string, string> initialValues)
    {
        _condRows.Clear();
        var fields = ConditionFieldsFor(_conditionKind);
        foreach (var f in fields)
        {
            var initial = initialValues.TryGetValue(f.Name, out var v) ? v : f.DefaultValue;
            _condRows.Add(FieldRow.Make(f, initial));
        }
        // Compound kinds (and/or/not) carry a children array — show a
        // hint that the JSON view is needed for child editing.
        var isCompound = _conditionKind is "and" or "or" or "not";
        CompoundHint.Visibility = isCompound ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Serialise the current condition editor state back to
    /// a JSON object string. Always includes "kind"; "params" only
    /// when at least one field has a value; "children" only for
    /// compound kinds (round-tripped from the original JSON).</summary>
    private string SerialiseCondition()
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("kind", _conditionKind);

            // Params object — only emit if non-empty.
            var fields = ConditionFieldsFor(_conditionKind);
            var anyValue = false;
            foreach (var r in _condRows)
                if (!r.IsEmpty) { anyValue = true; break; }
            if (anyValue)
            {
                w.WritePropertyName("params");
                w.WriteStartObject();
                foreach (var r in _condRows)
                {
                    if (r.IsEmpty && !r.Field.Required) continue;
                    switch (r.Field.Kind)
                    {
                        case ParamFieldKind.Int:
                            if (int.TryParse(r.Value, NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out var iv))
                                w.WriteNumber(r.Field.Name, iv);
                            else
                                w.WriteString(r.Field.Name, r.Value);
                            break;
                        case ParamFieldKind.Bool:
                            w.WriteBoolean(r.Field.Name,
                                string.Equals(r.Value, "true", StringComparison.OrdinalIgnoreCase));
                            break;
                        default:
                            w.WriteString(r.Field.Name, r.Value);
                            break;
                    }
                }
                w.WriteEndObject();
            }

            // Children array for and/or/not — round-trip from the
            // initial JSON (typed form doesn't edit children).
            if (_conditionKind is "and" or "or" or "not")
            {
                try
                {
                    using var ch = JsonDocument.Parse(_conditionChildrenJson);
                    if (ch.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        w.WritePropertyName("children");
                        ch.RootElement.WriteTo(w);
                    }
                }
                catch { /* invalid children JSON — drop quietly */ }
            }

            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Lightweight item type for the kind combo. Display
    /// shows the friendly label, SelectedValue returns the raw
    /// kind string the runtime expects.</summary>
    private sealed record ConditionKindItem(string Kind, string Label);

    private static string ValueToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        JsonValueKind.Null   => "",
        _                    => v.GetRawText(),
    };

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

    // ─── View toggle ─────────────────────────────────────────────

    private void OnViewSwitch(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ViewTyped.IsChecked == true)
        {
            // Re-parse JSON into fields so user-entered JSON shows up.
            try
            {
                _rows.Clear();
                BuildFields(JsonField.Text);
                FormScroll.Visibility = Visibility.Visible;
                JsonView.Visibility   = Visibility.Collapsed;
            }
            catch
            {
                // Couldn't parse — keep JSON visible.
                ViewJson.IsChecked = true;
            }
        }
        else
        {
            JsonField.Text = SerialiseFields();
            FormScroll.Visibility = Visibility.Collapsed;
            JsonView.Visibility   = Visibility.Visible;
        }
    }

    private string SerialiseFields()
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var r in _rows)
            {
                if (r.IsEmpty && !r.Field.Required) continue;
                switch (r.Field.Kind)
                {
                    case ParamFieldKind.Int:
                        if (int.TryParse(r.Value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var iv))
                            w.WriteNumber(r.Field.Name, iv);
                        else
                            w.WriteString(r.Field.Name, r.Value);
                        break;
                    case ParamFieldKind.Bool:
                        w.WriteBoolean(r.Field.Name,
                            string.Equals(r.Value, "true", StringComparison.OrdinalIgnoreCase));
                        break;
                    default:
                        w.WriteString(r.Field.Name, r.Value);
                        break;
                }
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private void OnJsonChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("must be a JSON object");
            JsonStatus.Text = "✓ valid";
            JsonStatus.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "OkBrush");
            OkBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            JsonStatus.Text = "✗ " + ex.Message;
            JsonStatus.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ErrBrush");
            OkBtn.IsEnabled = false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Validate required fields when in form mode.
        if (ViewTyped.IsChecked == true)
        {
            foreach (var r in _rows)
            {
                if (r.Field.Required && r.IsEmpty)
                {
                    MessageBox.Show(this,
                        $"'{r.Field.Label}' is required.",
                        "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            // Same validation for the condition fields when the
            // condition panel is active. Required fields like
            // url_contains.needle / var_equals.name are mandatory.
            if (_wantsCondition)
            {
                foreach (var r in _condRows)
                {
                    if (r.Field.Required && r.IsEmpty)
                    {
                        MessageBox.Show(this,
                            $"Condition: '{r.Field.Label}' is required.",
                            "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }
            JsonField.Text = SerialiseFields();
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            Result = JsonSerializer.Serialize(doc.RootElement); // compact

            if (_wantsCondition)
            {
                // Form mode owns the condition; in JSON-only mode we
                // also produce a condition string so the caller can
                // round-trip it (typed form is the only view that
                // knows the condition object's shape — JSON view
                // edits the params blob only).
                ConditionResult = SerialiseCondition();
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "JSON invalid: " + ex.Message,
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ─── FieldRow ────────────────────────────────────────────────

    /// <summary>
    /// One row in the typed form. Stores both the underlying value
    /// and the live UIElement (so binding doesn't have to reconstruct
    /// per-frame).
    /// </summary>
    public sealed class FieldRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required ParamField Field { get; init; }
        public string Label => Field.Required ? Field.Label + " *" : Field.Label;
        public string Hint  { get; init; } = "";

        private string _value = "";
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            }
        }
        public bool IsEmpty => string.IsNullOrWhiteSpace(_value);

        public required FrameworkElement Editor { get; init; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public static FieldRow Make(ParamField f, string initial)
        {
            FrameworkElement editor;
            var row = (FieldRow?)null;
            switch (f.Kind)
            {
                case ParamFieldKind.Multiline:
                {
                    var tb = new TextBox
                    {
                        AcceptsReturn = true, AcceptsTab = true,
                        FontFamily = (System.Windows.Media.FontFamily)
                            Application.Current.Resources["FontMono"],
                        FontSize = 12, MinHeight = 100,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Text = initial,
                    };
                    tb.TextChanged += (_, _) => { if (row is not null) row.Value = tb.Text; };
                    editor = tb;
                    break;
                }
                case ParamFieldKind.Bool:
                {
                    var cb = new CheckBox
                    {
                        IsChecked = string.Equals(initial, "true", StringComparison.OrdinalIgnoreCase),
                    };
                    cb.Checked   += (_, _) => { if (row is not null) row.Value = "true"; };
                    cb.Unchecked += (_, _) => { if (row is not null) row.Value = "false"; };
                    editor = cb;
                    break;
                }
                case ParamFieldKind.Select:
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = f.Options ?? Array.Empty<string>(),
                        SelectedItem = initial,
                    };
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (row is not null) row.Value = combo.SelectedItem?.ToString() ?? "";
                    };
                    editor = combo;
                    break;
                }
                default:
                {
                    var tb = new TextBox { Text = initial };
                    tb.TextChanged += (_, _) => { if (row is not null) row.Value = tb.Text; };
                    editor = tb;
                    break;
                }
            }
            row = new FieldRow
            {
                Field  = f,
                Hint   = HintFor(f),
                Editor = editor,
                Value  = initial,
            };
            return row;
        }

        private static string HintFor(ParamField f) => f.Kind switch
        {
            ParamFieldKind.Int       => "integer",
            ParamFieldKind.Selector  => "CSS selector",
            ParamFieldKind.Url       => "absolute URL",
            ParamFieldKind.Bool      => "true / false",
            ParamFieldKind.Multiline => "JS source",
            ParamFieldKind.Select    => "pick from list",
            _                        => "",
        };
    }
}
