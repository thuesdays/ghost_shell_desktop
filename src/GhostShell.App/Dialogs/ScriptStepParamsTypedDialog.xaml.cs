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

    private readonly string _actionType;
    private readonly ObservableCollection<FieldRow> _rows = new();

    public ScriptStepParamsTypedDialog(string actionType, string currentJson)
    {
        InitializeComponent();
        _actionType   = actionType;
        TitleText.Text = $"Edit '{actionType}' params";
        SubText.Text   = ScriptActionSchema.HasSchema(actionType)
            ? "Typed form — flip to JSON for raw editing"
            : "No typed schema for this action — use JSON view";

        BuildFields(currentJson);
        FieldsList.ItemsSource = _rows;
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
            HelpText.Text = "This action has no typed schema yet — edit as JSON.";
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
            JsonField.Text = SerialiseFields();
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            Result = JsonSerializer.Serialize(doc.RootElement); // compact
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
