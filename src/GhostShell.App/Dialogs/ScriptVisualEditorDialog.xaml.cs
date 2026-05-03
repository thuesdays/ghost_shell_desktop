// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;  // ToggleButton
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Visual script editor with palette + step-cards (Phase 13G).
///
/// Three views the user can flip between:
///   • <b>Visual</b> — current dialog. Click palette → appends step.
///     Up/Down to reorder. ✎ opens param sub-dialog. × removes.
///   • <b>JSON</b>   — falls back to <see cref="ScriptJsonEditorDialog"/>
///     for power users / actions not yet in the palette (notably
///     control-flow nesting).
///
/// The visual editor handles flat step lists fully. Scripts with
/// nested control-flow (if/foreach Body[]) round-trip through the
/// JSON view — the visual editor displays them as compact cards
/// labelled "if (...) — N then-steps, M else-steps" but doesn't yet
/// expand the nesting (Phase 14 graph view).
/// </summary>
public partial class ScriptVisualEditorDialog : Window
{
    public Script? Result { get; private set; }
    public string? ResultExpectedEtag { get; private set; }

    /// <summary>Set when user clicked "JSON view" — caller falls
    /// back to <see cref="ScriptJsonEditorDialog"/>.</summary>
    public bool SwitchToJson { get; private set; }

    /// <summary>Phase 21: set when the user clicks "Graph mode" —
    /// caller (ScriptsViewModel) converts the list to a graph
    /// skeleton and opens <see cref="ScriptGraphEditorDialog"/>.</summary>
    public bool SwitchToGraph { get; private set; }

    /// <summary>
    /// Phase 20: set when user clicked "▶ Run on…". The caller saves
    /// the edited script first, then opens a profile picker and kicks
    /// the runner against each chosen profile. The dialog itself
    /// doesn't have access to <c>IProfileRunner</c> / <c>IProfileService</c>;
    /// they live in <c>ScriptsViewModel</c>, which orchestrates.
    /// </summary>
    public bool RequestRun { get; private set; }

    private readonly Script? _existing;
    private readonly ObservableCollection<StepRow> _steps = new();

    public ScriptVisualEditorDialog(Script? existing)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is not null)
        {
            HeaderTitleText.Text   = existing.Name ?? "(unnamed script)";
            NameField.Text         = existing.Name;
            var desc = existing.Description ?? "";
            DescriptionField.Text  = desc;
            // Read-block text mirrors Description; if empty we fall
            // back to the placeholder so the block still tells the
            // user that clicking ✎ will let them add one.
            DescriptionDisplayText.Text = string.IsNullOrWhiteSpace(desc)
                ? "Click ✎ to add a description…"
                : desc;
            EnabledCheck.IsChecked = existing.Enabled;
            DefaultCheck.IsChecked = existing.IsDefault;
            DefaultPill.Visibility = existing.IsDefault
                ? Visibility.Visible : Visibility.Collapsed;
            LoadSteps(existing.StepsJson);
        }
        else
        {
            HeaderTitleText.Text = "Create script";
        }
        // Header title tracks the Name field while the user types.
        NameField.TextChanged += (_, _) =>
        {
            HeaderTitleText.Text = string.IsNullOrWhiteSpace(NameField.Text)
                ? "Create script" : NameField.Text;
        };
        // DEFAULT pill follows the checkbox.
        DefaultCheck.Checked   += (_, _) => DefaultPill.Visibility = Visibility.Visible;
        DefaultCheck.Unchecked += (_, _) => DefaultPill.Visibility = Visibility.Collapsed;

        BuildPalette();
        StepList.ItemsSource = _steps;
        RefreshIndices();
    }

    // ─── Zoom ─────────────────────────────────────────────────────

    private double _zoom = 1.0;
    private void OnZoomIn(object sender, RoutedEventArgs e)    => SetZoom(_zoom + 0.1);
    private void OnZoomOut(object sender, RoutedEventArgs e)   => SetZoom(_zoom - 0.1);
    private void OnZoomReset(object sender, RoutedEventArgs e) => SetZoom(1.0);
    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.5, 2.0);
        CanvasZoom.ScaleX = _zoom;
        CanvasZoom.ScaleY = _zoom;
        ZoomLabel.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

    // ─── Palette filter ──────────────────────────────────────────

    private List<PaletteGroup> _allGroups = new();
    private void OnPaletteFilterChanged(object sender, TextChangedEventArgs e)
    {
        var needle = (PaletteFilterField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(needle))
        {
            PaletteRoot.ItemsSource = _allGroups;
            return;
        }
        var filtered = new List<PaletteGroup>();
        foreach (var g in _allGroups)
        {
            var keep = g.Items.Where(i =>
                i.Type.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || i.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || i.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (keep.Count > 0)
                filtered.Add(new PaletteGroup(g.Header, keep, g.HeaderDotBrush));
        }
        PaletteRoot.ItemsSource = filtered;
    }

    // ─── Step click ───────────────────────────────────────────────

    private void OnStepClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Click anywhere on the card body → edit. Toolbar buttons
        // have their own handlers; their clicks bubble here too, so
        // we filter by sender's Tag.
        if (sender is not FrameworkElement fe || fe.Tag is not StepRow row) return;
        // Don't open editor if click landed on one of the row buttons.
        if (e.OriginalSource is DependencyObject d && IsButton(d)) return;
        EditStep(row);
    }

    private static bool IsButton(DependencyObject o)
    {
        DependencyObject? cur = o;
        while (cur is not null)
        {
            if (cur is Button or ToggleButton) return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    // ─── Description toggle ──────────────────────────────────────
    //
    // The description field has two visual modes:
    //   • Read mode (default): a highlighted Border with a TextBlock
    //     showing the description (or a "Click ✎ to add…" placeholder).
    //   • Edit mode: the underlying TextBox, focused for typing.
    //
    // ✎ button click and click-on-block both flip read → edit. Losing
    // focus on the TextBox flips edit → read and bakes the typed
    // value back into the read block.

    private const string DescriptionPlaceholder = "Click ✎ to add a description…";

    private void OnToggleDescriptionEdit(object sender, RoutedEventArgs e)
    {
        // If we're already in edit mode (e.g. user clicked ✎ twice),
        // just keep focus on the TextBox — flipping back would lose
        // their work.
        if (DescriptionField.Visibility == Visibility.Visible)
        {
            DescriptionField.Focus();
            return;
        }
        // Seed the editor with the actual description (not the
        // placeholder) so the user starts with a clean slate when
        // adding for the first time.
        var current = DescriptionDisplayText.Text;
        DescriptionField.Text = current == DescriptionPlaceholder ? "" : current;
        DescriptionReadBlock.Visibility = Visibility.Collapsed;
        DescriptionField.Visibility     = Visibility.Visible;
        DescriptionField.Focus();
        DescriptionField.SelectAll();
    }

    private void OnDescriptionLostFocus(object sender, RoutedEventArgs e)
    {
        // Bake the typed value into the read block, swap visibility.
        var text = (DescriptionField.Text ?? "").Trim();
        DescriptionDisplayText.Text = string.IsNullOrEmpty(text)
            ? DescriptionPlaceholder
            : text;
        DescriptionField.Visibility     = Visibility.Collapsed;
        DescriptionReadBlock.Visibility = Visibility.Visible;
    }

    // ─── Palette ─────────────────────────────────────────────────

    private void BuildPalette()
    {
        // Resolve category-dot brushes once.
        var flow      = (System.Windows.Media.Brush)Resources["CatFlow"];
        var nav       = (System.Windows.Media.Brush)Resources["CatNav"];
        var waitsB    = (System.Windows.Media.Brush)Resources["CatTiming"];
        var inter     = (System.Windows.Media.Brush)Resources["CatInteract"];
        var dataB     = (System.Windows.Media.Brush)Resources["CatData"];
        var ads       = (System.Windows.Media.Brush)Resources["CatAds"];
        var misc      = (System.Windows.Media.Brush)Resources["CatMisc"];

        // Categories + actions. Mirrors the action catalog in
        // ScriptRunner. Tooltip shows the param hint so the user
        // knows what to fill in.
        var groups = new[]
        {
            new PaletteGroup("CONTROL FLOW", new PaletteItem[]
            {
                new("if",        "🔀", "If / then / else", "branches via condition"),
                new("foreach",   "🔁", "Foreach",          "iterate items[]"),
                new("foreach_ad","📣", "Foreach ad",       "iterate parsed ads"),
                new("while_loop","🔄", "While loop",       "condition + body, capped"),
                new("break",     "⏹",  "Break loop",       "no params"),
                new("continue",  "⏭",  "Continue loop",    "no params"),
            }),
            new PaletteGroup("NAVIGATION", new PaletteItem[]
            {
                new("navigate",   "🧭", "Navigate to URL",      "url"),
                new("back",       "◀",  "History back",         ""),
                new("forward",    "▶",  "History forward",      ""),
                new("reload",     "↻",  "Reload page",          ""),
                new("new_tab",    "➕",  "Open new tab",         "url"),
                new("close_tab",  "✖",  "Close current tab",    ""),
                new("switch_tab", "🔀", "Switch to tab",         "index"),
                new("refresh",    "🔁", "Reload N times",        "max_attempts / delay"),
                new("rotate_ip",  "🛰", "Rotate proxy IP",       "wait_after_sec"),
            }),
            new PaletteGroup("WAITS", new PaletteItem[]
            {
                new("dwell",         "⏲", "Wait min..max ms",        "min_ms / max_ms"),
                new("random_delay",  "⌛", "Random delay",            "min_ms / max_ms"),
                new("pause",         "☕", "Pause (seconds)",         "min_sec / max_sec"),
                new("wait_for_selector","🔎","Wait for selector",     "selector / timeout_ms"),
                new("wait_for_url",  "🔗", "Wait for URL pattern",    "pattern / timeout_ms"),
            }),
            new PaletteGroup("INTERACTION", new PaletteItem[]
            {
                new("click_selector", "🖱", "Click selector",         "selector"),
                new("double_click",   "🖱", "Double click",           "selector"),
                new("right_click",    "🖱", "Right click",            "selector"),
                new("hover",          "🫳", "Hover selector",         "selector"),
                new("type",           "⌨", "Type text",              "selector / text / min_ms / max_ms"),
                new("press_key",      "⌨", "Press key",              "key"),
                new("scroll",         "🖱", "Scroll for N seconds",   "seconds"),
                new("scroll_to_bottom","↓", "Scroll to bottom",      ""),
                new("fill_form",      "📝", "Fill form (multi-field)","fields {...}"),
                new("move_random",    "↔",  "Idle pause",             "min_ms / max_ms"),
            }),
            new PaletteGroup("DATA", new PaletteItem[]
            {
                new("save_var",      "💾", "Save variable",          "name / value"),
                new("extract_text",  "📋", "Extract element text",   "selector / save_as"),
                new("read",          "📖", "Read element text",      "selector / save_as"),
                new("execute_js",    "⚡", "Execute JS",              "code"),
                new("http_request",  "🌐", "HTTP webhook / API",     "method / url / body"),
            }),
            new PaletteGroup("ADS", new PaletteItem[]
            {
                new("parse_ads",     "📣", "Parse ads in DOM",        ""),
                new("click_ad",      "🎯", "Click an ad",             "stamp_id (or random)"),
            }),
            new PaletteGroup("EXTENSIONS", new PaletteItem[]
            {
                new("open_extension_popup", "🧩", "Open ext popup",   "extension_id / page"),
                new("open_extension_page",  "🧩", "Open ext page",    "extension_id / page"),
                new("extension_click",      "🖱", "Click in ext",     "selector"),
                new("extension_fill",       "⌨", "Fill ext input",   "selector / value"),
                new("extension_eval",       "⚡", "Eval JS in ext",   "code / store_as"),
                new("extension_wait_for",   "⏳", "Wait selector ext","selector / timeout_sec"),
                new("extension_close",      "✖",  "Close ext tab",    ""),
            }),
            new PaletteGroup("MISC", new PaletteItem[]
            {
                new("solve_captcha", "🛡", "Detect + wait for solve", "timeout_sec"),
                new("screenshot",    "📷", "Capture screenshot",      "path / save_as"),
                new("log",           "📝", "Log a debug message",     "message"),
            }),
        };

        // Tag each group with its category dot brush — palette
        // header dots use this to read at a glance.
        groups[0] = groups[0] with { HeaderDotBrush = flow   };  // CONTROL FLOW
        groups[1] = groups[1] with { HeaderDotBrush = nav    };  // NAVIGATION
        groups[2] = groups[2] with { HeaderDotBrush = waitsB };  // WAITS
        groups[3] = groups[3] with { HeaderDotBrush = inter  };  // INTERACTION
        groups[4] = groups[4] with { HeaderDotBrush = dataB  };  // DATA
        groups[5] = groups[5] with { HeaderDotBrush = ads    };  // ADS
        groups[6] = groups[6] with { HeaderDotBrush = inter  };  // EXTENSIONS (reuse interaction)
        groups[7] = groups[7] with { HeaderDotBrush = misc   };  // MISC

        _allGroups = groups.ToList();
        PaletteRoot.ItemsSource = _allGroups;
    }

    /// <summary>
    /// Phase 19: keyboard activation for palette rows. Mirrors the
    /// MouseLeftButtonUp handler — fires on Enter or Space when the
    /// row has focus. Focus reaches the row via Tab navigation
    /// (Focusable=True on the Border).
    /// </summary>
    private void OnPaletteItemKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter
            && e.Key != System.Windows.Input.Key.Space) return;
        OnPaletteItem(sender, e);
        e.Handled = true;
    }

    private void OnPaletteItem(object sender, RoutedEventArgs e)
    {
        // Two paths: legacy <Button Click=...> (RoutedEventArgs only)
        // and the new <Border MouseLeftButtonUp=...>. Both arrive here,
        // and both expose Tag on the sender FrameworkElement.
        if (sender is not FrameworkElement fe || fe.Tag is not string actionType) return;
        // Append default step. Param-form opens immediately after if
        // the action type has required params (auto-edit-on-add UX).
        var row = StepRow.MakeDefault(actionType);
        _steps.Add(row);
        RefreshIndices();
        // Open param editor for actions with required params, but
        // not for the no-arg ones (back / break / continue / etc).
        if (RequiresParams(actionType))
            EditStep(row);
    }

    private static bool RequiresParams(string type) => type switch
    {
        "back" or "forward" or "reload" or "close_tab"
            or "break" or "continue" or "scroll_to_bottom"
            or "parse_ads" or "catch_ads"
            => false,
        // rotate_ip has only an optional wait param — fine to default it.
        "rotate_ip" => false,
        _   => true,
    };

    // ─── Step list operations ────────────────────────────────────

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StepRow r }) return;
        var i = _steps.IndexOf(r);
        if (i > 0) { _steps.Move(i, i - 1); RefreshIndices(); }
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StepRow r }) return;
        var i = _steps.IndexOf(r);
        if (i >= 0 && i < _steps.Count - 1) { _steps.Move(i, i + 1); RefreshIndices(); }
    }

    private void OnDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StepRow r }) return;
        _steps.Remove(r);
        RefreshIndices();
    }

    private void OnEditStep(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StepRow r }) return;
        EditStep(r);
    }

    /// <summary>
    /// "⚙" button on each step card → opens <see cref="StepFlagsDialog"/>
    /// for the universal per-step flags (probability, abort_on_error,
    /// the four ad-domain filters). On save, mutates the StepRow and
    /// notifies HasAdvancedFlags so the badge re-tints.
    /// </summary>
    private void OnStepFlags(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StepRow r }) return;
        var dlg = new StepFlagsDialog(
            r.TypeLabel,
            r.Probability,
            r.AbortOnError,
            r.SkipOnMyDomain, r.SkipOnTarget,
            r.OnlyOnTarget,   r.OnlyOnMyDomain,
            r.SkipOnBlocked,  r.OnlyOnBlocked) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.Saved) return;
        r.Probability    = dlg.Probability;
        r.AbortOnError   = dlg.AbortOnError;
        r.SkipOnMyDomain = dlg.SkipOnMyDomain;
        r.SkipOnTarget   = dlg.SkipOnTarget;
        r.OnlyOnTarget   = dlg.OnlyOnTarget;
        r.OnlyOnMyDomain = dlg.OnlyOnMyDomain;
        r.SkipOnBlocked  = dlg.SkipOnBlocked;
        r.OnlyOnBlocked  = dlg.OnlyOnBlocked;
        // StepRow doesn't fire INPC for these (they're plain set
        // properties); refresh the bound list so HasAdvancedFlags
        // re-evaluates and the ⚙ badge tints accent.
        StepList.Items.Refresh();
    }

    // ─── Nested-row toolbar (Phase 18) ──────────────────────────
    //
    // Each nested mini-card carries the same toolbar as a top-level
    // card: ✎ edits params, × removes, ↑/↓ reorders within the same
    // group (then/else/body). The handlers find the nested row's
    // parent StepRow by walking the visual tree (NestedRows itself
    // doesn't carry a back-reference).

    private void OnNestedEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NestedStepRow n }) return;
        // Pass the row's condition along for if/while_loop so the
        // dialog renders the typed condition editor pre-filled.
        // For other types the third arg is null and the dialog hides
        // its condition panel.
        var nKey = n.Type.ToLowerInvariant();
        var condIn = nKey is "if" or "while_loop" ? n.ConditionJson : null;
        var dlg = new ScriptStepParamsTypedDialog(n.Type, n.ParamsJson, condIn) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        n.ParamsJson = dlg.Result;
        if (dlg.ConditionResult is not null && (nKey is "if" or "while_loop"))
            n.ConditionJson = dlg.ConditionResult;
        // No call to Items.Refresh — INotifyPropertyChanged on
        // NestedStepRow.Summary fires automatically.
    }

    private void OnNestedDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NestedStepRow n }) return;
        var parent = FindParentStepRow(n);
        if (parent is null) return;
        // ObservableCollection notifies the bound ItemsControl on
        // Remove; no manual Refresh needed.
        parent.NestedRows.Remove(n);
        RecomputeShowGroup(parent);
    }

    private void OnNestedMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NestedStepRow n }) return;
        var parent = FindParentStepRow(n);
        if (parent is null) return;
        // Move within the same group only — swap with the previous
        // row that shares this row's Group. ObservableCollection.Move
        // fires a single NotifyCollectionChanged so the bound UI
        // animates rather than tearing through two index events.
        var siblings = parent.NestedRows
            .Where(x => string.Equals(x.Group, n.Group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var localIdx = siblings.IndexOf(n);
        if (localIdx <= 0) return;
        var prev = siblings[localIdx - 1];
        var i1 = parent.NestedRows.IndexOf(n);
        var i2 = parent.NestedRows.IndexOf(prev);
        parent.NestedRows.Move(i1, i2);
        RecomputeShowGroup(parent);
    }

    private void OnNestedMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NestedStepRow n }) return;
        var parent = FindParentStepRow(n);
        if (parent is null) return;
        var siblings = parent.NestedRows
            .Where(x => string.Equals(x.Group, n.Group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var localIdx = siblings.IndexOf(n);
        if (localIdx < 0 || localIdx >= siblings.Count - 1) return;
        var next = siblings[localIdx + 1];
        var i1 = parent.NestedRows.IndexOf(n);
        var i2 = parent.NestedRows.IndexOf(next);
        parent.NestedRows.Move(i1, i2);
        RecomputeShowGroup(parent);
    }

    /// <summary>Find which top-level StepRow owns this NestedStepRow.</summary>
    private StepRow? FindParentStepRow(NestedStepRow needle)
    {
        foreach (var s in _steps)
            if (s.NestedRows.Contains(needle))
                return s;
        return null;
    }

    /// <summary>
    /// After insert/delete/reorder, only the FIRST row in each group
    /// should show the "THEN"/"ELSE"/"BODY" header chip — re-flag the
    /// list so the divider chips render correctly.
    /// </summary>
    private static void RecomputeShowGroup(StepRow parent)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in parent.NestedRows)
        {
            n.ShowGroup = seen.Add(n.Group);
        }
    }

    /// <summary>
    /// "+ add step" / "+ then" / "+ else" buttons on a container
    /// header. Each handler dispatches to <see cref="AddNestedTo"/>
    /// with a hard-coded group name. The Tag carries the parent
    /// StepRow, no composite parsing.
    /// </summary>
    private void OnContainerAddInside(object sender, RoutedEventArgs e)
    {
        // Generic add (foreach/while/foreach_ad). Group is always "body".
        if (sender is Button { Tag: StepRow parent }) AddNestedTo(parent, "body");
    }

    private void OnContainerAddThen(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StepRow parent }) AddNestedTo(parent, "then");
    }

    private void OnContainerAddElse(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StepRow parent }) AddNestedTo(parent, "else");
    }

    /// <summary>
    /// Shared insert path: opens the action-picker dialog, appends a
    /// new <see cref="NestedStepRow"/> to <paramref name="parent"/>'s
    /// nested list under the requested group, and (for actions with
    /// required params) opens the params editor immediately.
    /// </summary>
    private void AddNestedTo(StepRow parent, string group)
    {
        // Flatten the palette catalogue — PaletteGroup / PaletteItem
        // are private to this class so we project to tuples.
        var flat = _allGroups.SelectMany(g =>
            g.Items.Select(it => (it.Type, it.Icon, it.Label, it.Description, g.Header)));
        var pick = new ScriptActionPickerDialog(flat) { Owner = this };
        if (pick.ShowDialog() != true || pick.SelectedType is null) return;
        var pickedType = pick.SelectedType;
        var (catBrush, _) = StepRow.CategoryBrushesFor(pickedType);
        var nrow = new NestedStepRow
        {
            Group         = group,
            Type          = pickedType,
            TypeLabel     = pickedType,
            Icon          = IconFor(pickedType),
            Enabled       = true,
            ParamsJson    = StepRow.DefaultParamsPublic(pickedType),
            CategoryBrush = catBrush,
        };
        parent.NestedRows.Add(nrow);
        RecomputeShowGroup(parent);
        // Auto-expand the container so the user immediately sees the
        // step they just added — otherwise the click feels like a
        // no-op (the new step is hidden behind a collapsed header).
        parent.IsExpanded = true;
        if (RequiresParams(pickedType))
        {
            var pKey = pickedType.ToLowerInvariant();
            var condIn = pKey is "if" or "while_loop" ? nrow.ConditionJson : null;
            var dlg = new ScriptStepParamsTypedDialog(nrow.Type, nrow.ParamsJson, condIn) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result is not null)
            {
                nrow.ParamsJson = dlg.Result;
                if (dlg.ConditionResult is not null && (pKey is "if" or "while_loop"))
                    nrow.ConditionJson = dlg.ConditionResult;
            }
        }
    }

    private void EditStep(StepRow row)
    {
        // Phase 14: typed form for actions with a registered schema,
        // raw JSON for everything else. The dialog supports flipping
        // between the two views in-line, so power users still get
        // the JSON path without leaving the visual editor.
        //
        // For control-flow steps we also pass the row's condition
        // so the dialog can render the typed condition editor.
        var rKey = row.Type.ToLowerInvariant();
        var condIn = rKey is "if" or "while_loop" ? row.ConditionJson : null;
        var dlg = new ScriptStepParamsTypedDialog(row.TypeLabel, row.ParamsJson, condIn)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        row.ParamsJson    = dlg.Result;
        row.ParamSummary  = SummariseParams(row.ParamsJson);
        if (dlg.ConditionResult is not null && (rKey is "if" or "while_loop"))
            row.ConditionJson = dlg.ConditionResult;
        StepList.Items.Refresh();
    }

    private void RefreshIndices()
    {
        // Phase 14 audit fix #1: rebuild Index + IsFirst + IsLast on
        // every reorder/delete/add. Without this, the count text
        // didn't update on delete-last and the move buttons stayed
        // active at boundaries.
        for (var i = 0; i < _steps.Count; i++)
        {
            _steps[i].Index   = i + 1;
            _steps[i].IsFirst = i == 0;
            _steps[i].IsLast  = i == _steps.Count - 1;
            // Connector line between sibling steps — first node has
            // none, every subsequent shows a thin vertical bar above.
            _steps[i].ShowConnector = i > 0;
        }
        StepsCountText.Text = _steps.Count switch
        {
            0 => "0 steps",
            1 => "1 step",
            _ => $"{_steps.Count} steps",
        };
        // Empty-state card visible when no steps; fade once the user
        // adds anything.
        if (EmptyHint is not null)
            EmptyHint.Visibility = _steps.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        // Force the ItemsControl to rebind so item-template triggers
        // pick up the new IsFirst/IsLast values immediately. Without
        // this the buttons stay enabled until something else mutates.
        StepList.Items.Refresh();
    }

    // ─── Load / save ─────────────────────────────────────────────

    /// <summary>
    /// Phase 66 — recursively rewrite every JSON object key in
    /// <paramref name="json"/> to snake_case_lower, leaving primitive
    /// values, array structure, and nested objects intact. Used at
    /// editor-load time to heal scripts saved by an early build of
    /// the recorder which serialised in PascalCase. The canonical
    /// editor format is snake_case_lower for multi-word keys
    /// (abort_on_error, skip_on_my_domain) and lowercase for single
    /// words (type, params, enabled). Calling this on already-
    /// canonical JSON is a safe no-op aside from re-emit cost.
    /// </summary>
    private static string NormaliseJsonKeysToSnakeCase(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            using var ms = new System.IO.MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                WriteWithSnakeKeys(doc.RootElement, w);
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            // If JSON is malformed, hand back unchanged — the existing
            // try/catch in LoadSteps surfaces a clean error to the user.
            return json;
        }
    }

    private static void WriteWithSnakeKeys(
        System.Text.Json.JsonElement el, System.Text.Json.Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject())
                {
                    w.WritePropertyName(ToSnakeCaseLower(p.Name));
                    WriteWithSnakeKeys(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    WriteWithSnakeKeys(item, w);
                w.WriteEndArray();
                break;
            default:
                el.WriteTo(w);
                break;
        }
    }

    private static string ToSnakeCaseLower(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Fast path: already lowercase / snake_case.
        bool hasUpper = false;
        for (int i = 0; i < name.Length; i++)
            if (char.IsUpper(name[i])) { hasUpper = true; break; }
        if (!hasUpper) return name;

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                // Insert '_' between transitions: lowercase→uppercase
                // (foo|Bar → foo_bar) and acronym tail (HTMLParser →
                // html_parser). Skip if at start or already after '_'.
                if (i > 0)
                {
                    char prev = name[i - 1];
                    if (prev != '_' && (char.IsLower(prev) || char.IsDigit(prev)))
                        sb.Append('_');
                    else if (prev != '_' && char.IsUpper(prev) && i + 1 < name.Length
                             && char.IsLower(name[i + 1]))
                        sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private void LoadSteps(string stepsJson)
    {
        try
        {
            // Phase 66 — auto-heal scripts saved with non-canonical
            // key casing. The recorder briefly used PascalCase /
            // CamelCase before settling on snake_case_lower; existing
            // recordings in the user's DB have keys like "Type",
            // "Params", "AbortOnError" which the strict TryGetProperty
            // calls below ignore (resulting in "(no params)" cards).
            // Normalising every JSON object's keys to snake_case_lower
            // before the parse means a single read path works for
            // all historical formats.
            stepsJson = NormaliseJsonKeysToSnakeCase(stepsJson);
            using var doc = JsonDocument.Parse(stepsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var type = s.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var paramsJson = s.TryGetProperty("params", out var p)
                    ? p.GetRawText() : "{}";
                var enabled = !s.TryGetProperty("enabled", out var en) || en.GetBoolean();

                var (catBrush, catSoft) = StepRow.CategoryBrushesFor(type);
                var row = new StepRow
                {
                    Type            = type,
                    TypeLabel       = type,
                    Icon            = IconFor(type),
                    ParamsJson      = paramsJson,
                    ParamSummary    = SummariseParams(paramsJson),
                    Enabled         = enabled,
                    OriginalRawJson = s.GetRawText(),
                    CategoryBrush   = catBrush,
                    CategoryBgBrush = catSoft,
                };
                // Phase 19: hydrate per-step flags from the JSON.
                // Booleans default to false, probability to 1.0.
                if (s.TryGetProperty("probability", out var pr) && pr.ValueKind == JsonValueKind.Number)
                    row.Probability = Math.Clamp(pr.GetDouble(), 0.0, 1.0);
                if (s.TryGetProperty("abort_on_error", out var ae) && ae.ValueKind == JsonValueKind.True)
                    row.AbortOnError = true;
                if (s.TryGetProperty("skip_on_my_domain", out var sm) && sm.ValueKind == JsonValueKind.True)
                    row.SkipOnMyDomain = true;
                if (s.TryGetProperty("skip_on_target", out var st) && st.ValueKind == JsonValueKind.True)
                    row.SkipOnTarget = true;
                if (s.TryGetProperty("only_on_target", out var ot) && ot.ValueKind == JsonValueKind.True)
                    row.OnlyOnTarget = true;
                if (s.TryGetProperty("only_on_my_domain", out var om) && om.ValueKind == JsonValueKind.True)
                    row.OnlyOnMyDomain = true;
                if (s.TryGetProperty("skip_on_blocked", out var sb) && sb.ValueKind == JsonValueKind.True)
                    row.SkipOnBlocked = true;
                if (s.TryGetProperty("only_on_blocked", out var ob) && ob.ValueKind == JsonValueKind.True)
                    row.OnlyOnBlocked = true;

                // Hydrate the typed condition object so the new
                // condition editor in the params dialog can show
                // the kind + per-kind fields. Falls back to the
                // safe default {"kind":"true"} when missing or
                // malformed so an `if` step always has SOMETHING
                // to evaluate.
                if (s.TryGetProperty("condition", out var cEl)
                    && cEl.ValueKind == JsonValueKind.Object)
                {
                    row.ConditionJson = cEl.GetRawText();
                }

                // Phase 14: extract nested branches for if/foreach so
                // the visual editor renders an indented summary
                // beneath the parent card.
                // ObservableCollection has no AddRange — use Add in loop.
                if (string.Equals(type, "if", StringComparison.OrdinalIgnoreCase))
                {
                    if (s.TryGetProperty("then", out var th) && th.ValueKind == JsonValueKind.Array)
                        foreach (var n in NestedFromArray(th, "then")) row.NestedRows.Add(n);
                    if (s.TryGetProperty("else", out var el) && el.ValueKind == JsonValueKind.Array)
                        foreach (var n in NestedFromArray(el, "else")) row.NestedRows.Add(n);
                }
                else if (string.Equals(type, "foreach", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "foreach_ad", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "while_loop", StringComparison.OrdinalIgnoreCase))
                {
                    if (s.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Array)
                        foreach (var n in NestedFromArray(b, "body")) row.NestedRows.Add(n);
                }

                // Default-expand containers that have nested steps so
                // the user sees structure on load without clicking.
                // Phase 18: also default-expand empty containers
                // (foreach/if/while) so the "+ add step" button is
                // visible right away.
                if (row.NestedRows.Count > 0
                    || row.IsIfContainer || row.IsLoopContainer)
                {
                    row.IsExpanded = true;
                }

                _steps.Add(row);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Couldn't parse the existing steps JSON. Edit in JSON view to fix.\n\n" + ex.Message,
                "Load", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Serialise one row back to a JSON object string. Phase 18:
    /// nested arrays (then/else/body) are now rebuilt from the live
    /// <see cref="StepRow.NestedRows"/> rather than preserved from
    /// <see cref="StepRow.OriginalRawJson"/>, so edits to nested
    /// steps actually round-trip on save. Other fields outside the
    /// editor's model (e.g. <c>condition</c>) ARE still copied
    /// verbatim from <c>OriginalRawJson</c>.
    /// </summary>
    private static string SerialiseStep(StepRow r)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", r.Type);

            // params
            w.WritePropertyName("params");
            using (var pdoc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(r.ParamsJson) ? "{}" : r.ParamsJson))
                pdoc.RootElement.WriteTo(w);

            if (!r.Enabled) w.WriteBoolean("enabled", false);

            // Phase 19: per-step flags. Only emit when non-default so
            // round-trip stays clean (default JSON has no extra keys).
            if (r.Probability < 1.0)
                w.WriteNumber("probability", Math.Round(r.Probability, 2));
            if (r.AbortOnError)    w.WriteBoolean("abort_on_error",    true);
            if (r.SkipOnMyDomain)  w.WriteBoolean("skip_on_my_domain", true);
            if (r.SkipOnTarget)    w.WriteBoolean("skip_on_target",    true);
            if (r.OnlyOnTarget)    w.WriteBoolean("only_on_target",    true);
            if (r.OnlyOnMyDomain)  w.WriteBoolean("only_on_my_domain", true);
            if (r.SkipOnBlocked)   w.WriteBoolean("skip_on_blocked",   true);
            if (r.OnlyOnBlocked)   w.WriteBoolean("only_on_blocked",   true);

            var typeKey = r.Type.ToLowerInvariant();
            var isIf      = typeKey == "if";
            var isLoop    = typeKey is "foreach" or "foreach_ad" or "while_loop";
            var isWhile   = typeKey == "while_loop";

            // Emit the typed condition for control-flow steps. The
            // condition editor in the typed-form dialog is the source
            // of truth here; the OriginalRawJson preservation path
            // below explicitly skips `condition` so we don't write it
            // twice.
            if (isIf || isWhile)
            {
                w.WritePropertyName("condition");
                try
                {
                    using var cd = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(r.ConditionJson)
                            ? "{\"kind\":\"true\"}"
                            : r.ConditionJson);
                    if (cd.RootElement.ValueKind == JsonValueKind.Object)
                        cd.RootElement.WriteTo(w);
                    else
                    {
                        // Garbage in ConditionJson → emit a safe
                        // default rather than a non-object literal
                        // (the runtime would treat it as "always
                        // true" anyway, but a bad shape may break
                        // strict consumers).
                        w.WriteStartObject();
                        w.WriteString("kind", "true");
                        w.WriteEndObject();
                    }
                }
                catch
                {
                    w.WriteStartObject();
                    w.WriteString("kind", "true");
                    w.WriteEndObject();
                }
            }

            // Re-build nested branches from NestedRows.
            if (isIf)
            {
                w.WritePropertyName("then");
                w.WriteStartArray();
                foreach (var n in r.NestedRows.Where(n =>
                    string.Equals(n.Group, "then", StringComparison.OrdinalIgnoreCase)))
                    SerialiseNested(w, n);
                w.WriteEndArray();
                w.WritePropertyName("else");
                w.WriteStartArray();
                foreach (var n in r.NestedRows.Where(n =>
                    string.Equals(n.Group, "else", StringComparison.OrdinalIgnoreCase)))
                    SerialiseNested(w, n);
                w.WriteEndArray();
            }
            else if (isLoop)
            {
                w.WritePropertyName("body");
                w.WriteStartArray();
                foreach (var n in r.NestedRows)
                    SerialiseNested(w, n);
                w.WriteEndArray();
            }

            // Preserve any unmanaged fields (condition, etc.) from
            // OriginalRawJson — but skip the ones we already wrote.
            if (!string.IsNullOrEmpty(r.OriginalRawJson))
            {
                try
                {
                    using var orig = JsonDocument.Parse(r.OriginalRawJson);
                    foreach (var prop in orig.RootElement.EnumerateObject())
                    {
                        var n = prop.Name.ToLowerInvariant();
                        // Skip every key the editor's model owns —
                        // Phase 19 flags get written explicitly above.
                        if (n is "type" or "params" or "enabled"
                            or "then" or "else" or "body"
                            or "condition" // owned by ConditionJson now — emitted explicitly above
                            or "probability" or "abort_on_error"
                            or "skip_on_my_domain" or "skip_on_target"
                            or "only_on_my_domain" or "only_on_target"
                            or "skip_on_blocked" or "only_on_blocked")
                            continue;
                        prop.WriteTo(w);
                    }
                }
                catch { /* OriginalRawJson invalid — emit without extras */ }
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Emit one nested step's JSON object inside an array
    /// writer. Now recursively handles nested branches — if a nested
    /// row is itself a control-flow step (if/foreach/while), its own
    /// NestedRows are emitted as then/else/body arrays.</summary>
    private static void SerialiseNested(Utf8JsonWriter w, NestedStepRow n)
    {
        w.WriteStartObject();
        w.WriteString("type", n.Type);
        w.WritePropertyName("params");
        using (var pdoc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(n.ParamsJson) ? "{}" : n.ParamsJson))
            pdoc.RootElement.WriteTo(w);
        if (!n.Enabled) w.WriteBoolean("enabled", false);

        // Emit condition for nested if / while_loop. Pre-fix: this
        // path didn't write `condition` at all, so any user-authored
        // condition on a nested if-step (like the
        // `if(ad_is_external)` inside foreach_ad in GoodMedika) was
        // silently dropped on save and the runtime fell back to the
        // ConditionEvaluator's default-true behaviour, making the
        // whole gate a no-op.
        var nKey = n.Type.ToLowerInvariant();
        if (nKey is "if" or "while_loop")
        {
            w.WritePropertyName("condition");
            try
            {
                using var cd = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(n.ConditionJson)
                        ? "{\"kind\":\"true\"}"
                        : n.ConditionJson);
                if (cd.RootElement.ValueKind == JsonValueKind.Object)
                    cd.RootElement.WriteTo(w);
                else
                {
                    w.WriteStartObject();
                    w.WriteString("kind", "true");
                    w.WriteEndObject();
                }
            }
            catch
            {
                w.WriteStartObject();
                w.WriteString("kind", "true");
                w.WriteEndObject();
            }
        }

        // Recursively write nested rows for control-flow steps.
        if (n.NestedRows.Count > 0)
        {
            if (string.Equals(n.Type, "if", StringComparison.OrdinalIgnoreCase))
            {
                var thenRows = n.NestedRows.Where(r => r.Group == "then").ToList();
                var elseRows = n.NestedRows.Where(r => r.Group == "else").ToList();
                if (thenRows.Count > 0)
                {
                    w.WriteStartArray("then");
                    foreach (var r in thenRows)
                        SerialiseNested(w, r);
                    w.WriteEndArray();
                }
                if (elseRows.Count > 0)
                {
                    w.WriteStartArray("else");
                    foreach (var r in elseRows)
                        SerialiseNested(w, r);
                    w.WriteEndArray();
                }
            }
            else if (string.Equals(n.Type, "foreach", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(n.Type, "foreach_ad", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(n.Type, "while_loop", StringComparison.OrdinalIgnoreCase))
            {
                var bodyRows = n.NestedRows.Where(r => r.Group == "body").ToList();
                if (bodyRows.Count > 0)
                {
                    w.WriteStartArray("body");
                    foreach (var r in bodyRows)
                        SerialiseNested(w, r);
                    w.WriteEndArray();
                }
            }
        }

        w.WriteEndObject();
    }

    private static IEnumerable<NestedStepRow> NestedFromArray(JsonElement arr, string group)
    {
        var first = true;
        foreach (var s in arr.EnumerateArray())
        {
            var type    = s.TryGetProperty("type",   out var t)  ? t.GetString() ?? "" : "?";
            var pJson   = s.TryGetProperty("params", out var p)  ? p.GetRawText() : "{}";
            var enabled = !s.TryGetProperty("enabled", out var en) || en.GetBoolean();
            var (catBrush, _) = StepRow.CategoryBrushesFor(type);
            var row = new NestedStepRow
            {
                Group         = group,
                Type          = type,
                TypeLabel     = type,
                Icon          = IconFor(type),
                Enabled       = enabled,
                ParamsJson    = pJson,
                ShowGroup     = first,
                CategoryBrush = catBrush,
            };
            // Hydrate condition for nested if / while_loop. Without
            // this, every nested if loaded from disk got the default
            // {"kind":"true"} and the original condition was silently
            // discarded on save (regression introduced before the
            // typed condition editor existed — the SerialiseNested
            // path didn't even emit `condition` at all).
            if (s.TryGetProperty("condition", out var cEl)
                && cEl.ValueKind == JsonValueKind.Object)
            {
                row.ConditionJson = cEl.GetRawText();
            }

            // Recursively populate nested rows for control-flow steps.
            // Each nested row may itself be a loop or if-statement with
            // its own body/then/else branches, visible in the editor tree.
            if (string.Equals(type, "if", StringComparison.OrdinalIgnoreCase))
            {
                if (s.TryGetProperty("then", out var th) && th.ValueKind == JsonValueKind.Array)
                    foreach (var n in NestedFromArray(th, "then")) row.NestedRows.Add(n);
                if (s.TryGetProperty("else", out var el) && el.ValueKind == JsonValueKind.Array)
                    foreach (var n in NestedFromArray(el, "else")) row.NestedRows.Add(n);
            }
            else if (string.Equals(type, "foreach", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(type, "foreach_ad", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(type, "while_loop", StringComparison.OrdinalIgnoreCase))
            {
                if (s.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Array)
                    foreach (var n in NestedFromArray(b, "body")) row.NestedRows.Add(n);
            }

            yield return row;
            first = false;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = (NameField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Name is required.", "Save",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Phase 21 audit fix — block save with zero steps. An empty
        // script can't run anything; failing here is friendlier than
        // failing at run-time on the next launch.
        if (_steps.Count == 0)
        {
            MessageBox.Show(this,
                "Add at least one step before saving — empty scripts can't be run.",
                "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Re-serialise — preserve nested branches (then/else/body/
        // condition) by round-tripping through the original raw JSON
        // when present. This is the Phase 14 round-trip-safety fix:
        // if a user loads a script with nested if/foreach, edits the
        // top-level params, and saves, the nested steps survive.
        var stepObjects = new List<string>(_steps.Count);
        foreach (var r in _steps)
        {
            stepObjects.Add(SerialiseStep(r));
        }
        var arr = "[" + string.Join(",", stepObjects) + "]";

        ResultExpectedEtag = _existing?.ETag;
        Result = new Script
        {
            Id          = _existing?.Id ?? 0,
            Name        = name,
            Description = (DescriptionField.Text ?? "").Trim(),
            StepsJson   = arr,
            Enabled     = EnabledCheck.IsChecked == true,
            IsDefault   = DefaultCheck.IsChecked == true,
            ETag        = _existing?.ETag ?? "",
            CreatedAt   = _existing?.CreatedAt ?? default,
            UpdatedAt   = DateTime.UtcNow,
        };
        DialogResult = true;
        Close();
    }

    private void OnJsonView(object sender, RoutedEventArgs e)
    {
        SwitchToJson = true;
        DialogResult = false;
        Close();
    }

    /// <summary>Phase 21/22: flip into graph editor. We snapshot the
    /// current list-editor state into <see cref="Result"/> (no strict
    /// validation — the user can validate after wiring the graph) so
    /// the caller has a non-null Script to feed into the converter.
    /// Without this snapshot, brand-new scripts would lose any name /
    /// description / steps the user typed before clicking Graph mode.</summary>
    private void OnGraphView(object sender, RoutedEventArgs e)
    {
        // Build a soft-validated Result snapshot. Empty name is OK
        // here — the graph editor's Save will enforce it via its
        // own validation pass before persisting.
        var name = (NameField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = "(unnamed)";

        var stepObjects = new List<string>(_steps.Count);
        foreach (var r in _steps) stepObjects.Add(SerialiseStep(r));
        var arr = "[" + string.Join(",", stepObjects) + "]";

        Result = new Script
        {
            Id          = _existing?.Id ?? 0,
            Name        = name,
            Description = (DescriptionField.Text ?? "").Trim(),
            StepsJson   = arr,
            Enabled     = EnabledCheck.IsChecked == true,
            IsDefault   = DefaultCheck.IsChecked == true,
            ETag        = _existing?.ETag ?? "",
            CreatedAt   = _existing?.CreatedAt ?? default,
            UpdatedAt   = DateTime.UtcNow,
            LayoutMode  = "list",
        };
        ResultExpectedEtag = _existing?.ETag;
        SwitchToGraph = true;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// "▶ Run on…" button. Performs the same save flow as
    /// <see cref="OnSave"/>, then sets <see cref="RequestRun"/> so the
    /// caller (ScriptsViewModel) opens a profile picker and kicks
    /// the runner. Bails if validation fails the same way Save does.
    /// </summary>
    private void OnRunOnProfile(object sender, RoutedEventArgs e)
    {
        RequestRun = true;
        OnSave(sender, e);
        // OnSave sets DialogResult=true and Closes; the caller reads
        // RequestRun + Result and dispatches the run.
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static string IconFor(string type) => type.ToLowerInvariant() switch
    {
        "navigate" or "open_url" or "visit"      => "🧭",
        "back" or "forward" or "reload"           => "◀",
        "new_tab" or "close_tab"                  => "🪟",
        "switch_tab"                              => "🔀",
        "refresh"                                 => "🔁",
        "rotate_ip"                               => "🛰",
        "click_selector" or "click" or "double_click" or "right_click" => "🖱",
        "type" or "press_key" or "fill_form"      => "⌨",
        "scroll" or "scroll_to_bottom"            => "↕",
        "hover" or "move_random"                  => "🫳",
        "save_var" or "extract_text" or "read"    => "💾",
        "execute_js"                              => "⚡",
        "http_request"                            => "🌐",
        "wait" or "dwell" or "random_delay" or "pause" => "⏲",
        "wait_for_selector" or "wait_for_url"     => "⏳",
        "if"                                      => "🔀",
        "foreach" or "foreach_ad"                 => "🔁",
        "while_loop"                              => "🔄",
        "break" or "continue"                     => "⏹",
        "parse_ads" or "catch_ads" or "click_ad"  => "📣",
        "screenshot"                              => "📷",
        "solve_captcha"                           => "🛡",
        "log"                                     => "📝",
        "open_extension_popup" or "open_extension_page" => "🧩",
        "extension_eval" or "extension_wait_for"
            or "extension_click" or "extension_fill" or "extension_close"
                                                  => "🧩",
        _                                         => "🔧",
    };

    private static string SummariseParams(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return "(no params)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            var bits = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True   => "true",
                    JsonValueKind.False  => "false",
                    JsonValueKind.Null   => "null",
                    _                    => "{…}",
                };
                if (v.Length > 32) v = v[..29] + "…";
                bits.Add($"{p.Name}={v}");
                if (bits.Count >= 4) { bits.Add("…"); break; }
            }
            return string.Join("  ·  ", bits);
        }
        catch
        {
            return "(invalid JSON)";
        }
    }

    // ─── Inner types ─────────────────────────────────────────────

    private sealed record PaletteGroup(
        string Header,
        IReadOnlyList<PaletteItem> Items,
        System.Windows.Media.Brush? HeaderDotBrush = null);

    /// <summary>One palette entry. The Tooltip parameter is also
    /// shown as the entry's Description in the palette body —
    /// gives the user the param hint without hovering.</summary>
    private sealed record PaletteItem(string Type, string Icon, string Label, string Tooltip)
    {
        public string Description => Tooltip;
    }

    /// <summary>One row in the step list. Mutable so Up/Down move
    /// and edits update the visible state without rebuilding rows.</summary>
    public sealed class StepRow : INotifyPropertyChanged
    {
        public required string Type { get; init; }
        public required string TypeLabel { get; init; }
        public required string Icon { get; init; }
        public bool Enabled { get; set; } = true;

        // ─── Phase 19: per-step flags (editor-side) ──────────────
        // Kept on the visual model so they can round-trip through
        // the visual editor without going via JSON view. The values
        // mirror <see cref="ScriptStep"/>'s flags 1:1; SerialiseStep
        // writes them at the step level (sibling to "params").

        /// <summary>0–1 probability gate. 1 = always run.</summary>
        public double Probability { get; set; } = 1.0;

        /// <summary>If true, a thrown handler aborts the whole script run.</summary>
        public bool AbortOnError { get; set; }

        /// <summary>Skip when current ad's domain matches MyDomains.</summary>
        public bool SkipOnMyDomain { get; set; }

        /// <summary>Skip when current ad's domain matches TargetDomains.</summary>
        public bool SkipOnTarget { get; set; }

        /// <summary>Run only when current ad is on a target domain.</summary>
        public bool OnlyOnTarget { get; set; }

        /// <summary>Run only when current ad is on a profile-owned domain.</summary>
        public bool OnlyOnMyDomain { get; set; }

        /// <summary>Skip when current ad's domain is in the block list.</summary>
        public bool SkipOnBlocked { get; set; }

        /// <summary>Run only when current ad is on the block list (debug-only).</summary>
        public bool OnlyOnBlocked { get; set; }

        /// <summary>True if any flag is set to a non-default value —
        /// drives a small "⚙" badge on the card so users can see at
        /// a glance that this step has advanced settings.</summary>
        public bool HasAdvancedFlags
            => Probability < 1.0 || AbortOnError
            || SkipOnMyDomain || SkipOnTarget
            || OnlyOnTarget || OnlyOnMyDomain
            || SkipOnBlocked || OnlyOnBlocked;

        /// <summary>
        /// The step's original full JSON object as loaded from the
        /// script. Round-trip preserves fields the visual editor
        /// doesn't manage (then / else / body). Empty for newly-
        /// added steps.
        ///
        /// Note: <c>condition</c> is now owned by <see cref="ConditionJson"/>
        /// and emitted from there directly. The "preserve unmanaged
        /// fields" path in <see cref="SerialiseStep"/> explicitly
        /// drops the original <c>condition</c> so we don't double-write.
        /// </summary>
        public string OriginalRawJson { get; set; } = "";

        /// <summary>
        /// JSON object for control-flow conditions
        /// (e.g. <c>{"kind":"ad_is_external"}</c>). Used by `if` and
        /// `while_loop`; ignored otherwise. Default kind is
        /// <c>true</c> so a freshly-created `if` always fires its
        /// THEN branch until the user picks a real condition.
        /// </summary>
        public string ConditionJson { get; set; } = "{\"kind\":\"true\"}";

        /// <summary>Editable nested rows (Then/Else for if; Body for
        /// foreach / foreach_ad / while_loop). Phase 18: switched
        /// from <see cref="List{T}"/> to <see cref="ObservableCollection{T}"/>
        /// so mutations from the toolbar handlers (✎/×/↑/↓ + add)
        /// trigger the inner ItemsControl to rebind without a full
        /// outer Items.Refresh() round-trip.</summary>
        public ObservableCollection<NestedStepRow> NestedRows { get; set; }
            = new ObservableCollection<NestedStepRow>();

        public bool HasNested => NestedRows.Count > 0;

        public string NestedSummary => Type.ToLowerInvariant() switch
        {
            "if"          => CountByGroup("then") + " then · " + CountByGroup("else") + " else",
            "foreach"     => $"{NestedRows.Count} body steps",
            "foreach_ad"  => $"{NestedRows.Count} body steps",
            "while_loop"  => $"{NestedRows.Count} body steps",
            _             => "",
        };

        /// <summary>
        /// Header label rendered in the nested-block container's
        /// coloured strip. Tells the user which control-flow keyword
        /// owns the block ("▼ THEN/ELSE BLOCK", "▼ FOREACH BODY", …).
        /// </summary>
        public string ContainerHeader => Type.ToLowerInvariant() switch
        {
            "if"          => "▼  IF / THEN / ELSE  BLOCK",
            "foreach"     => "▼  FOREACH  BODY",
            "foreach_ad"  => "▼  FOREACH AD  BODY",
            "while_loop"  => "▼  WHILE  BODY",
            _             => "▼  NESTED",
        };

        /// <summary>True if the container is an <c>if</c>, in which case
        /// the header should show two add-buttons (one per branch).</summary>
        public bool IsIfContainer
            => string.Equals(Type, "if", StringComparison.OrdinalIgnoreCase);

        /// <summary>Top-level toolbar's ▾ expand button is visible for
        /// any container type (even empty ones, so the user can fold
        /// a container they just added).</summary>
        public bool ShowExpandToggle => HasNested || IsIfContainer || IsLoopContainer;

        /// <summary>True for foreach / foreach_ad / while_loop — single
        /// "add step" button targets the body array.</summary>
        public bool IsLoopContainer
            => Type.Equals("foreach", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("foreach_ad", StringComparison.OrdinalIgnoreCase)
            || Type.Equals("while_loop", StringComparison.OrdinalIgnoreCase);

        /// <summary>Tag string the if-then "+ then" button passes into
        /// the OnContainerAddInside handler — encodes "row index | group".</summary>
        public string ThenAddTag => "if|then";   // resolved by parent index in handler

        /// <summary>Tag for the else add button.</summary>
        public string ElseAddTag => "if|else";

        private int CountByGroup(string g)
            => NestedRows.Count(r => string.Equals(r.Group, g, StringComparison.OrdinalIgnoreCase));

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value;
                OnChanged(nameof(IsExpanded));
                OnChanged(nameof(ShowNested)); } }
        }

        /// <summary>
        /// Container visibility: shown when expanded AND either we
        /// already have nested rows OR this step is a container type
        /// (so empty foreach/if/while still render their header with
        /// the "+ add step" button — otherwise newly-added containers
        /// look like dead nodes).
        /// </summary>
        public bool ShowNested => IsExpanded
            && (HasNested || IsIfContainer || IsLoopContainer);

        private string _paramsJson = "{}";
        public string ParamsJson
        {
            get => _paramsJson;
            set { if (_paramsJson != value) { _paramsJson = value; OnChanged(nameof(ParamsJson)); } }
        }
        private string _paramSummary = "(no params)";
        public string ParamSummary
        {
            get => _paramSummary;
            set { if (_paramSummary != value) { _paramSummary = value; OnChanged(nameof(ParamSummary)); } }
        }
        private int _index;
        public int Index
        {
            get => _index;
            set { if (_index != value) { _index = value; OnChanged(nameof(Index)); } }
        }

        private bool _isFirst;
        public bool IsFirst
        {
            get => _isFirst;
            set { if (_isFirst != value) { _isFirst = value; OnChanged(nameof(IsFirst)); OnChanged(nameof(CanMoveUp)); } }
        }
        private bool _isLast;
        public bool IsLast
        {
            get => _isLast;
            set { if (_isLast != value) { _isLast = value; OnChanged(nameof(IsLast)); OnChanged(nameof(CanMoveDown)); } }
        }
        public bool CanMoveUp   => !IsFirst;
        public bool CanMoveDown => !IsLast;

        private bool _showConnector;
        public bool ShowConnector
        {
            get => _showConnector;
            set { if (_showConnector != value) { _showConnector = value; OnChanged(nameof(ShowConnector)); } }
        }

        /// <summary>Per-category accent brush — drives the node's
        /// border + index pill background. Resolved from the
        /// dialog's category palette at row construction.</summary>
        public System.Windows.Media.Brush CategoryBrush { get; init; }
            = System.Windows.Media.Brushes.Gray;

        /// <summary>Soft variant for the node's fill — same hue at
        /// ~20% alpha, sits on top of the dotted-grid canvas.</summary>
        public System.Windows.Media.Brush CategoryBgBrush { get; init; }
            = System.Windows.Media.Brushes.Transparent;

        public static StepRow MakeDefault(string type)
        {
            var (brush, soft) = CategoryBrushesFor(type);
            // Control-flow steps need top-level fields (condition,
            // then, else, body) that aren't part of params. Stash a
            // valid skeleton in OriginalRawJson so SerialiseStep's
            // "preserve original fields" path emits a runnable shape
            // even for a freshly-created step (otherwise the runner
            // sees an `if` with no condition and silently no-ops).
            var skeleton = type.ToLowerInvariant() switch
            {
                "if"          => "{\"type\":\"if\",\"params\":{},\"condition\":{\"kind\":\"true\"},\"then\":[],\"else\":[]}",
                "foreach"     => "{\"type\":\"foreach\",\"params\":{\"items\":\"a,b,c\",\"var\":\"item\"},\"body\":[]}",
                "foreach_ad"  => "{\"type\":\"foreach_ad\",\"params\":{},\"body\":[]}",
                "while_loop"  => "{\"type\":\"while_loop\",\"params\":{\"max_iterations\":1000},\"condition\":{\"kind\":\"true\"},\"body\":[]}",
                _             => "",
            };
            var row = new StepRow
            {
                Type            = type,
                TypeLabel       = type,
                Icon            = IconForStatic(type),
                ParamsJson      = DefaultParams(type),
                ParamSummary    = "(edit params…)",
                CategoryBrush   = brush,
                CategoryBgBrush = soft,
                OriginalRawJson = skeleton,
            };
            // Containers default to expanded so the "+ add step"
            // button is visible immediately after the user adds them.
            if (row.IsIfContainer || row.IsLoopContainer)
                row.IsExpanded = true;
            return row;
        }

        /// <summary>
        /// Resolve (foreground, background) brushes for an action
        /// type. Mirrors the category palette in XAML; we keep the
        /// mapping in code so the StepRow factory can paint nodes
        /// without a converter. Public-static so the dialog's
        /// LoadSteps can call it on existing rows.
        /// </summary>
        public static (System.Windows.Media.Brush Brush,
                       System.Windows.Media.Brush Soft) CategoryBrushesFor(string type)
        {
            var app = System.Windows.Application.Current;
            object? L(string key) => app?.TryFindResource(key);
            var unknown = ((System.Windows.Media.Brush)(L("CatMisc") ?? System.Windows.Media.Brushes.Gray),
                           (System.Windows.Media.Brush)(L("CatMiscSoft") ?? System.Windows.Media.Brushes.Transparent));
            return type.ToLowerInvariant() switch
            {
                "if" or "foreach" or "foreach_ad" or "while_loop"
                    or "break" or "continue"
                    => ((System.Windows.Media.Brush)L("CatFlow")!,
                        (System.Windows.Media.Brush)L("CatFlowSoft")!),
                "navigate" or "open_url" or "visit"
                    or "back" or "forward" or "reload"
                    or "new_tab" or "close_tab"
                    or "switch_tab" or "refresh" or "rotate_ip"
                    => ((System.Windows.Media.Brush)L("CatNav")!,
                        (System.Windows.Media.Brush)L("CatNavSoft")!),
                "click_selector" or "click" or "double_click" or "right_click"
                    or "hover" or "type" or "press_key"
                    or "scroll" or "scroll_to_bottom" or "fill_form" or "move_random"
                    => ((System.Windows.Media.Brush)L("CatInteract")!,
                        (System.Windows.Media.Brush)L("CatInteractSoft")!),
                "save_var" or "extract_text" or "read" or "execute_js"
                    or "http_request"
                    => ((System.Windows.Media.Brush)L("CatData")!,
                        (System.Windows.Media.Brush)L("CatDataSoft")!),
                "parse_ads" or "catch_ads" or "click_ad"
                    => ((System.Windows.Media.Brush)L("CatAds")!,
                        (System.Windows.Media.Brush)L("CatAdsSoft")!),
                "wait" or "dwell" or "random_delay" or "pause"
                    or "wait_for_selector" or "wait_for_url"
                    => ((System.Windows.Media.Brush)L("CatTiming")!,
                        (System.Windows.Media.Brush)L("CatTimingSoft")!),
                _   => unknown,
            };
        }

        private static string IconForStatic(string type) => type.ToLowerInvariant() switch
        {
            "navigate" or "open_url" or "visit"      => "🧭",
            "back" or "forward" or "reload"           => "◀",
            "click_selector" or "click"               => "🖱",
            "type"                                    => "⌨",
            "scroll"                                  => "↕",
            "if"                                      => "🔀",
            "foreach" or "foreach_ad"                 => "🔁",
            "while_loop"                              => "🔄",
            "switch_tab"                              => "🔀",
            "refresh"                                 => "🔁",
            "rotate_ip"                               => "🛰",
            "pause"                                   => "☕",
            "read"                                    => "📖",
            "http_request"                            => "🌐",
            _                                         => "🔧",
        };

        /// <summary>Public version of <see cref="DefaultParams"/> for
        /// callers outside the StepRow class (the nested-row "+ add"
        /// flow needs to pre-populate a fresh row with sensible
        /// defaults).</summary>
        public static string DefaultParamsPublic(string type) => DefaultParams(type);

        private static string DefaultParams(string type) => type.ToLowerInvariant() switch
        {
            "navigate" or "open_url" or "visit" => "{\"url\":\"https://example.com/\"}",
            "dwell"                              => "{\"min_ms\":2000,\"max_ms\":5000}",
            "random_delay"                       => "{\"min_ms\":200,\"max_ms\":1500}",
            "click_selector" or "click" or "double_click" or "right_click" or "hover"
                                                => "{\"selector\":\"\"}",
            "type"                              => "{\"selector\":\"\",\"text\":\"\"}",
            "press_key"                         => "{\"key\":\"Enter\"}",
            "scroll"                            => "{\"seconds\":6}",
            "save_var"                          => "{\"name\":\"\",\"value\":\"\"}",
            "extract_text"                      => "{\"selector\":\"body\",\"save_as\":\"text\"}",
            "read"                              => "{\"selector\":\"body\",\"save_as\":\"text\"}",
            "execute_js"                        => "{\"code\":\"\"}",
            "wait_for_selector"                 => "{\"selector\":\"\",\"timeout_ms\":15000}",
            "wait_for_url"                      => "{\"pattern\":\"\",\"timeout_ms\":15000}",
            "log"                               => "{\"message\":\"\"}",
            "screenshot"                        => "{\"path\":\"screenshots/{{ts}}.png\"}",
            "click_ad"                          => "{\"stamp_id\":-1}",
            "solve_captcha"                     => "{\"timeout_sec\":180}",
            "fill_form"                         => "{\"fields\":{}}",
            // Phase 17 — web-parity additions
            "while_loop"                        => "{\"max_iterations\":1000}",
            "switch_tab"                        => "{\"index\":0}",
            "pause"                             => "{\"min_sec\":3,\"max_sec\":8}",
            "refresh"                           => "{\"max_attempts\":3,\"delay_min_sec\":3,\"delay_max_sec\":8}",
            "rotate_ip"                         => "{\"wait_after_sec\":4}",
            "http_request"                      => "{\"method\":\"POST\",\"url\":\"https://\",\"body\":\"{}\",\"timeout_sec\":15}",
            // Phase 19 — extensions
            "open_extension_popup"              => "{\"extension_id\":\"\",\"page\":\"popup.html\",\"timeout_sec\":15}",
            "open_extension_page"               => "{\"extension_id\":\"\",\"page\":\"options.html\",\"timeout_sec\":15}",
            "extension_eval"                    => "{\"code\":\"return document.title;\"}",
            "extension_wait_for"                => "{\"selector\":\"\",\"timeout_sec\":15}",
            "extension_click"                   => "{\"selector\":\"\",\"timeout_sec\":10}",
            "extension_fill"                    => "{\"selector\":\"\",\"value\":\"\",\"clear_first\":true}",
            "extension_close"                   => "{}",
            _                                   => "{}",
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// One nested step inside a parent's then/else/body. Phase 18:
    /// upgraded from a read-only display record to an editable item.
    /// Stores the full step JSON (so the parent's SerialiseStep can
    /// emit it back into the right branch) plus display fields.
    /// Mutates <see cref="ParamsJson"/> when the user edits params,
    /// and the parent's <see cref="StepRow.OriginalRawJson"/> is
    /// re-emitted from <see cref="StepRow.NestedRows"/> on save.
    /// </summary>
    public sealed class NestedStepRow : INotifyPropertyChanged
    {
        public required string Group     { get; set; } // "then" / "else" / "body"
        public required string Type      { get; set; }
        public required string TypeLabel { get; set; }
        public required string Icon      { get; set; }
        public bool Enabled { get; set; } = true;
        public bool ShowGroup { get; set; }

        private string _paramsJson = "{}";
        public string ParamsJson
        {
            get => _paramsJson;
            set { if (_paramsJson != value) { _paramsJson = value; OnChanged(nameof(ParamsJson)); OnChanged(nameof(Summary)); } }
        }

        /// <summary>Condition object for nested `if` / `while_loop`
        /// rows. Default <c>{"kind":"true"}</c>. Ignored for other
        /// types but kept on the row so the round-trip is lossless
        /// when the user toggles a step's type back and forth in the
        /// editor without re-creating it.</summary>
        public string ConditionJson { get; set; } = "{\"kind\":\"true\"}";

        public string Summary => SummariseParams(_paramsJson);

        /// <summary>Per-category brush for the mini-card border —
        /// drives the same colour-coded look as top-level cards.</summary>
        public System.Windows.Media.Brush CategoryBrush { get; set; }
            = System.Windows.Media.Brushes.Gray;

        /// <summary>Nested rows representing body/then/else branches.
        /// Makes NestedStepRow itself recursive — a control-flow step
        /// (foreach, if, while) can have its own nested steps, which
        /// can themselves be control-flow and have children, etc.</summary>
        public ObservableCollection<NestedStepRow> NestedRows { get; set; } = new();

        /// <summary>True if this row has any nested children to display.</summary>
        public bool HasNested => NestedRows.Count > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // Lightweight clone of ScriptVisualEditorDialog.SummariseParams
        // — kept private here so NestedStepRow re-summarises live as
        // the user edits without depending on the outer class.
        private static string SummariseParams(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return "(no params)";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
                var bits = new List<string>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == "type" || p.Name == "params" || p.Name == "enabled"
                        || p.Name == "condition" || p.Name == "then" || p.Name == "else"
                        || p.Name == "body") continue;
                    var v = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.Number => p.Value.GetRawText(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        _                    => "{…}",
                    };
                    if (v.Length > 28) v = v[..25] + "…";
                    bits.Add($"{p.Name}={v}");
                    if (bits.Count >= 3) { bits.Add("…"); break; }
                }
                return bits.Count == 0 ? "(no params)" : string.Join("  ·  ", bits);
            }
            catch { return "(invalid JSON)"; }
        }
    }
}
