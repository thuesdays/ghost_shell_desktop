// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostShell.Core.Models;
using GhostShell.Runtime.Scripts;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 21 — graph-mode script editor.
///
/// Free-form canvas where each <see cref="ScriptStep"/> lives at a
/// (X,Y) position and edges between nodes drive runtime traversal.
/// On Save serialises Nodes+Edges into the script's NodesJson /
/// EdgesJson columns. List-mode editor (<see cref="ScriptVisualEditorDialog"/>)
/// is unaffected.
/// </summary>
public partial class ScriptGraphEditorDialog : Window
{
    public Script? Result { get; private set; }
    public string? ResultExpectedEtag { get; private set; }
    public bool SwitchToJson { get; private set; }

    private readonly Script _existing;
    private readonly Dictionary<string, NodeView> _nodes =
        new(StringComparer.Ordinal);
    private readonly List<EdgeView> _edges = new();

    /// <summary>Visible item models powering the palette ItemsControl.</summary>
    private ObservableCollection<PaletteItem> _palette = new();
    private List<PaletteItem> _allPalette = new();

    public ScriptGraphEditorDialog(Script existing)
    {
        InitializeComponent();
        _existing = existing;
        TitleText.Text = $"Graph editor — {existing.Name}";

        BuildPalette();
        LoadExistingGraph();
        UpdateStatus();
    }

    // ─── Palette ──────────────────────────────────────────────────

    private void BuildPalette()
    {
        // Pull the same palette catalogue the list-mode editor uses
        // so both modes ship the same actions. Keeping a separate
        // copy here lets the graph editor evolve its UX without
        // touching the list editor.
        var items = new List<PaletteItem>
        {
            new("if",        "🔀", "If / then / else", "branches via condition"),
            new("foreach",   "🔁", "Foreach",          "iterate items[]"),
            new("foreach_ad","📣", "Foreach ad",       "iterate parsed ads"),
            new("while_loop","🔄", "While loop",       "condition + body"),
            new("break",     "⏹",  "Break",            ""),
            new("continue",  "⏭",  "Continue",         ""),
            new("navigate",  "🧭", "Navigate",         "url"),
            new("back",      "◀",  "Back",             ""),
            new("forward",   "▶",  "Forward",          ""),
            new("reload",    "↻",  "Reload",           ""),
            new("new_tab",   "➕",  "New tab",          "url"),
            new("close_tab", "✖",  "Close tab",        ""),
            new("switch_tab","🔀", "Switch tab",       "index"),
            new("dwell",     "⏲", "Dwell ms",          "min/max"),
            new("pause",     "☕", "Pause sec",         "min/max"),
            new("random_delay","⌛","Random delay",     "min/max ms"),
            new("wait_for_selector","🔎","Wait selector","selector"),
            new("wait_for_url","🔗","Wait URL",         "pattern"),
            new("click_selector","🖱","Click",          "selector"),
            new("hover",     "🫳", "Hover",            "selector"),
            new("type",      "⌨", "Type",             "selector / text"),
            new("press_key", "⌨", "Press key",        "key"),
            new("scroll",    "↕",  "Scroll",           "seconds"),
            new("scroll_to_bottom","↓","Scroll bottom",""),
            new("save_var",  "💾", "Save var",         "name / value"),
            new("extract_text","📋","Extract text",    "selector / save_as"),
            new("read",      "📖", "Read text",        "selector / save_as"),
            new("execute_js","⚡", "Execute JS",       "code"),
            new("http_request","🌐","HTTP",            "method / url"),
            new("parse_ads", "📣", "Parse ads",        ""),
            new("click_ad",  "🎯", "Click ad",         ""),
            new("solve_captcha","🛡","Solve captcha",  "timeout"),
            new("screenshot","📷", "Screenshot",       "path"),
            new("log",       "📝", "Log",              "message"),
            new("rotate_ip", "🛰", "Rotate IP",        "wait sec"),
            new("refresh",   "🔁", "Refresh",          "max attempts"),
            new("open_extension_popup","🧩","Ext popup", "extension_id"),
            new("open_extension_page", "🧩","Ext page",  "extension_id / page"),
            new("extension_eval","⚡", "Ext eval",      "code"),
            new("extension_wait_for","⏳","Ext wait",   "selector"),
            new("extension_click","🖱","Ext click",    "selector"),
            new("extension_fill", "⌨","Ext fill",     "selector / value"),
            new("extension_close","✖", "Ext close",    ""),
        };
        _allPalette = items;
        _palette = new ObservableCollection<PaletteItem>(items);
        PaletteList.ItemsSource = _palette;
    }

    private void OnPaletteFilterChanged(object sender, TextChangedEventArgs e)
    {
        var n = (PaletteFilterField.Text ?? "").Trim();
        _palette.Clear();
        foreach (var p in _allPalette)
        {
            if (n.Length == 0
                || p.Type.Contains(n, StringComparison.OrdinalIgnoreCase)
                || p.Label.Contains(n, StringComparison.OrdinalIgnoreCase)
                || p.Tooltip.Contains(n, StringComparison.OrdinalIgnoreCase))
                _palette.Add(p);
        }
    }

    private void OnPaletteItemDrop(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string actionType) return;
        // Drop the new node near the centre of the visible canvas
        // viewport. A small jitter avoids stacking when the user
        // adds multiple of the same action.
        var cx = CanvasScroll.HorizontalOffset + (CanvasScroll.ViewportWidth  / 2) - 90;
        var cy = CanvasScroll.VerticalOffset   + (CanvasScroll.ViewportHeight / 2) - 30;
        var jitter = Random.Shared.Next(0, 40);
        AddNode(actionType, cx + jitter, cy + jitter,
            id: NewNodeId(),
            paramsJson: DefaultParamsFor(actionType),
            enabled: true);
        UpdateStatus();
    }

    // ─── Node operations ──────────────────────────────────────────

    private string NewNodeId()
    {
        // Stable scheme: n1, n2, n3, … picking the first unused
        // index. Keeps node ids short + readable in JSON.
        for (var i = 1; i < 10000; i++)
        {
            var id = "n" + i;
            if (!_nodes.ContainsKey(id)) return id;
        }
        return "n_" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>Build a node card + register it in the index +
    /// drop it on the canvas at the given position.</summary>
    private NodeView AddNode(string type, double x, double y,
        string id, string paramsJson, bool enabled)
    {
        var nv = new NodeView
        {
            Id          = id,
            Type        = type,
            ParamsJson  = paramsJson,
            Enabled     = enabled,
            X           = x,
            Y           = y,
        };
        var visual = BuildNodeVisual(nv);
        nv.Visual = visual;
        Canvas.SetLeft(visual, x);
        Canvas.SetTop(visual, y);
        NodesLayer.Children.Add(visual);
        _nodes[id] = nv;
        EmptyHint.Visibility = Visibility.Collapsed;
        return nv;
    }

    /// <summary>Construct the WPF UI for one node. Header strip with
    /// the action label, a small body with a param summary, plus an
    /// input port (top) and output port (bottom).</summary>
    /// <summary>
    /// Phase 22 hot-fix — graph editor was crashing with NRE because
    /// <see cref="ScriptVisualEditorDialog.StepRow.CategoryBrushesFor"/>
    /// returns null brushes when the theme resource lookup misses
    /// (e.g. when the editor opens before the App-level resource
    /// dictionary is fully merged). Falling back to a neutral Gray
    /// keeps the canvas usable and the cast on the next line safe.
    /// </summary>
    private static (Brush brush, Color color) ResolveCategory(string type)
    {
        var (b, _) = ScriptVisualEditorDialog.StepRow.CategoryBrushesFor(type);
        if (b is SolidColorBrush sc) return (sc, sc.Color);
        // Resource lookup missed (or returned a non-solid brush) —
        // pick a sensible fallback so glow/shadow effects still render.
        var fallback = (Color)ColorConverter.ConvertFromString("#FF8B8B8B");
        return (new SolidColorBrush(fallback), fallback);
    }

    /// <summary>Theme resource lookup with a hex-color fallback.
    /// Returns a SolidColorBrush + its Color so callers can attach
    /// the value to <see cref="DropShadowEffect.Color"/> safely.</summary>
    private static (Brush brush, Color color) ResolveBrush(string key, string fallbackHex)
    {
        var b = Application.Current?.TryFindResource(key) as Brush;
        if (b is SolidColorBrush sc) return (sc, sc.Color);
        var fb = (Color)ColorConverter.ConvertFromString(fallbackHex);
        return (new SolidColorBrush(fb), fb);
    }

    private Border BuildNodeVisual(NodeView nv)
    {
        var (catBrush, catColor) = ResolveCategory(nv.Type);
        var card = new Border
        {
            Width = 180,
            CornerRadius = new CornerRadius(6),
            BorderBrush = catBrush,
            BorderThickness = new Thickness(2),
            Background = (Brush)Application.Current.Resources["BgRaised"],
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.45,
            },
            Cursor = Cursors.SizeAll,
            Tag = nv,
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new Border
        {
            Background = catBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(10, 5, 10, 5),
        };
        var hgrid = new Grid();
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = $"{IconFor(nv.Type)}  {nv.Type}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        var idBadge = new TextBlock
        {
            Text = nv.Id,
            Foreground = Brushes.White,
            Opacity = 0.7,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["FontMono"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(idBadge, 1);
        hgrid.Children.Add(label);
        hgrid.Children.Add(idBadge);
        header.Child = hgrid;
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Body — params summary + delete button
        var body = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = SummariseParams(nv.ParamsJson),
            Foreground = (Brush)Application.Current.Resources["TextMuted"],
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 38,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nv.SummaryText = summary;
        Grid.SetColumn(summary, 0);
        body.Children.Add(summary);

        // Edit + Delete buttons in a small horizontal stack on the
        // right of the body. Phase 22 hot-fix — node deletion was
        // missing entirely; users were stuck with whatever they'd
        // dropped onto the canvas.
        var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
        var btnEdit = new Button
        {
            Content = "✎",
            Width = 22, Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            Tag = nv,
            ToolTip = "Edit params / condition",
        };
        btnEdit.Click += OnNodeEditParams;
        var btnDelete = new Button
        {
            Content = "×",
            Width = 22, Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            Tag = nv,
            ToolTip = "Delete this node and all its connecting edges",
            Foreground = (Brush)Application.Current.Resources["ErrBrush"],
            FontWeight = FontWeights.Bold,
        };
        btnDelete.Click += OnNodeDelete;
        btnStack.Children.Add(btnEdit);
        btnStack.Children.Add(btnDelete);
        Grid.SetColumn(btnStack, 1);
        body.Children.Add(btnStack);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        card.Child = grid;

        // ── Input port (top centre) ──
        // Phase 22: bigger (18px), brighter, with a glowing
        // DropShadow so it pops against the dark grid background.
        var inPort = new Ellipse
        {
            Width = 18, Height = 18,
            Fill = Brushes.White,
            Stroke = catBrush,
            StrokeThickness = 3,
            Cursor = Cursors.Cross,
            Tag = (nv, "in"),
            ToolTip = "INPUT — drop an edge here to wire the previous node into this one",
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = catColor,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.9,
            },
        };
        inPort.MouseLeftButtonUp   += OnPortMouseUp;
        inPort.MouseEnter          += OnPortHoverEnter;
        inPort.MouseLeave          += OnPortHoverLeave;

        // ── Output port (bottom centre) ──
        var outPort = new Ellipse
        {
            Width = 18, Height = 18,
            Fill = catBrush,
            Stroke = Brushes.White,
            StrokeThickness = 3,
            Cursor = Cursors.Cross,
            Tag = (nv, "out"),
            ToolTip = "OUTPUT — drag from here to another node's INPUT to wire an edge",
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = catColor,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 1.0,
            },
        };
        outPort.MouseLeftButtonDown += OnPortMouseDown;
        outPort.MouseEnter          += OnPortHoverEnter;
        outPort.MouseLeave          += OnPortHoverLeave;

        // For if-nodes the output port becomes a then/else pair.
        // We spawn two ports labelled T and E with edge labels.
        if (string.Equals(nv.Type, "if", StringComparison.OrdinalIgnoreCase))
        {
            outPort.Visibility = Visibility.Collapsed;
            // Phase 22 hot-fix — same defensive pattern: never assume
            // the theme resources are SolidColorBrush'es. Falls back
            // to canonical green/amber if lookup misses.
            var (okBrush,   okColor)   = ResolveBrush("OkBrush",   "#FF22C55E");
            var (warnBrush, warnColor) = ResolveBrush("WarnBrush", "#FFF59E0B");
            var thenPort = new Ellipse
            {
                Width = 18, Height = 18,
                Fill = okBrush,
                Stroke = Brushes.White,
                StrokeThickness = 3,
                Cursor = Cursors.Cross,
                Tag = (nv, "then"),
                ToolTip = "THEN branch — drag to wire the path taken when condition is TRUE",
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = okColor,
                    BlurRadius = 10, ShadowDepth = 0, Opacity = 1.0,
                },
            };
            thenPort.MouseLeftButtonDown += OnPortMouseDown;
            thenPort.MouseEnter          += OnPortHoverEnter;
            thenPort.MouseLeave          += OnPortHoverLeave;
            var elsePort = new Ellipse
            {
                Width = 18, Height = 18,
                Fill = warnBrush,
                Stroke = Brushes.White,
                StrokeThickness = 3,
                Cursor = Cursors.Cross,
                Tag = (nv, "else"),
                ToolTip = "ELSE branch — drag to wire the path taken when condition is FALSE",
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = warnColor,
                    BlurRadius = 10, ShadowDepth = 0, Opacity = 1.0,
                },
            };
            elsePort.MouseLeftButtonDown += OnPortMouseDown;
            elsePort.MouseEnter          += OnPortHoverEnter;
            elsePort.MouseLeave          += OnPortHoverLeave;
            nv.ThenPort = thenPort;
            nv.ElsePort = elsePort;
        }

        nv.InPort = inPort;
        nv.OutPort = outPort;

        // Wire drag-on-card-body for repositioning. Mouse-down on the
        // header/body starts a drag; the canvas's mouse-move tracks
        // the drag delta and translates the card.
        card.MouseLeftButtonDown += OnNodeMouseDown;

        return card;
    }

    private string DefaultParamsFor(string type) => type.ToLowerInvariant() switch
    {
        "navigate" or "open_url" or "visit" => "{\"url\":\"https://example.com/\"}",
        "dwell"             => "{\"min_ms\":2000,\"max_ms\":5000}",
        "random_delay"      => "{\"min_ms\":200,\"max_ms\":1500}",
        "click_selector" or "click" or "hover"
                            => "{\"selector\":\"\"}",
        "type"              => "{\"selector\":\"\",\"text\":\"\"}",
        "press_key"         => "{\"key\":\"Enter\"}",
        "scroll"            => "{\"seconds\":6}",
        "save_var"          => "{\"name\":\"\",\"value\":\"\"}",
        "extract_text" or "read"
                            => "{\"selector\":\"body\",\"save_as\":\"text\"}",
        "execute_js"        => "{\"code\":\"\"}",
        "wait_for_selector" => "{\"selector\":\"\",\"timeout_ms\":15000}",
        "wait_for_url"      => "{\"pattern\":\"\",\"timeout_ms\":15000}",
        "log"               => "{\"message\":\"\"}",
        "screenshot"        => "{\"path\":\"screenshots/{{ts}}.png\"}",
        "click_ad"          => "{\"stamp_id\":-1}",
        "solve_captcha"     => "{\"timeout_sec\":180}",
        "while_loop"        => "{\"max_iterations\":1000}",
        "switch_tab"        => "{\"index\":0}",
        "pause"             => "{\"min_sec\":3,\"max_sec\":8}",
        "refresh"           => "{\"max_attempts\":3,\"delay_min_sec\":3,\"delay_max_sec\":8}",
        "rotate_ip"         => "{\"wait_after_sec\":4}",
        "http_request"      => "{\"method\":\"POST\",\"url\":\"https://\",\"body\":\"{}\",\"timeout_sec\":15}",
        _                   => "{}",
    };

    // ─── Node drag (move on canvas) ───────────────────────────────

    private NodeView? _draggingNode;
    private Point _dragOffset;

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Ellipse) return; // port click — handled elsewhere
        if (e.OriginalSource is Button)  return; // ✎ click — handled elsewhere
        if (sender is not Border card || card.Tag is not NodeView nv) return;
        // Phase 22: Connect tool intercepts node clicks before the
        // drag-to-reposition handler.
        if (_connectMode)
        {
            OnNodeConnectClick(nv);
            e.Handled = true;
            return;
        }
        _draggingNode = nv;
        var p = e.GetPosition(GraphCanvas);
        _dragOffset = new Point(p.X - nv.X, p.Y - nv.Y);
        card.CaptureMouse();
        e.Handled = true;
    }

    // ─── Edge creation (port → port drag) ─────────────────────────

    private NodeView? _edgeFrom;
    private string? _edgeFromKind; // "out" | "then" | "else"
    private Point _rubberStart;

    // ─── Phase 22 — Connect tool ──────────────────────────────────
    //
    // When the Connect toggle is checked, two-click wiring takes over:
    // first clicked node = source, second = target, edge appears. For
    // if-source nodes the user gets a small popup asking THEN or ELSE.
    // This is the no-drag fallback for users who can't reliably hit
    // the 18-px ports.

    private bool _connectMode;
    private NodeView? _connectFrom;

    private void OnConnectToolChecked(object sender, RoutedEventArgs e)
    {
        _connectMode = true;
        _connectFrom = null;
        StatusText.Text = "🔗 Connect tool ON — click a SOURCE node, then click a TARGET node";
        // Switch every node card's cursor to a crosshair so the user
        // sees connect-mode is live.
        foreach (var nv in _nodes.Values)
            if (nv.Visual is not null) nv.Visual.Cursor = Cursors.Cross;
    }

    private void OnConnectToolUnchecked(object sender, RoutedEventArgs e)
    {
        _connectMode = false;
        _connectFrom = null;
        UpdateStatus();
        foreach (var nv in _nodes.Values)
            if (nv.Visual is not null) nv.Visual.Cursor = Cursors.SizeAll;
        ClearConnectFromHighlight();
    }

    /// <summary>
    /// Routed via OnNodeMouseDown when Connect tool is active. Records
    /// the source on first click, completes the edge on second click.
    /// </summary>
    private void OnNodeConnectClick(NodeView nv)
    {
        if (_connectFrom is null)
        {
            _connectFrom = nv;
            HighlightConnectFrom(nv);
            StatusText.Text = $"🔗 Source: {nv.Id} ({nv.Type}). Now click the TARGET node…";
            return;
        }
        if (ReferenceEquals(_connectFrom, nv))
        {
            // Click same node twice → cancel.
            ClearConnectFromHighlight();
            _connectFrom = null;
            StatusText.Text = "🔗 Connect tool ON — pick a SOURCE";
            return;
        }
        // Decide label. For if-source we ask which branch; for loop-
        // source default to "body" (user can rewire to "next" later
        // by drawing a second edge).
        string? label = null;
        var srcType = _connectFrom.Type.ToLowerInvariant();
        if (srcType == "if")
        {
            var resp = MessageBox.Show(this,
                "Wire as THEN branch?\n\nYes = THEN (condition true)\nNo  = ELSE (condition false)",
                "Connect — pick branch",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (resp == MessageBoxResult.Cancel) { ClearConnectFromHighlight(); _connectFrom = null; return; }
            label = resp == MessageBoxResult.Yes ? "then" : "else";
        }
        else if (srcType is "foreach" or "foreach_ad" or "while_loop")
        {
            var resp = MessageBox.Show(this,
                "Wire as BODY (loop iterates) or NEXT (after loop completes)?\n\nYes = BODY\nNo  = NEXT",
                "Connect — pick edge kind",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (resp == MessageBoxResult.Cancel) { ClearConnectFromHighlight(); _connectFrom = null; return; }
            label = resp == MessageBoxResult.Yes ? "body" : "next";
        }
        AddEdge(_connectFrom.Id, nv.Id, label);
        ClearConnectFromHighlight();
        _connectFrom = null;
        UpdateStatus();
    }

    private void HighlightConnectFrom(NodeView nv)
    {
        if (nv.Visual is null) return;
        nv.Visual.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Yellow,
            BlurRadius = 24,
            ShadowDepth = 0,
            Opacity = 1.0,
        };
    }

    private void ClearConnectFromHighlight()
    {
        if (_connectFrom?.Visual is { } v)
        {
            v.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.45,
            };
        }
    }

    private void OnPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse el || el.Tag is not ValueTuple<NodeView, string> tag) return;
        _edgeFrom = tag.Item1;
        _edgeFromKind = tag.Item2;
        // Centre of the 18px port = (9, 9).
        _rubberStart = el.TranslatePoint(new Point(9, 9), GraphCanvas);
        RubberBand.Visibility = Visibility.Visible;
        GraphCanvas.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Phase 22: hover feedback — port grows + shadow widens
    /// so the user can see exactly where the connection will land.</summary>
    private void OnPortHoverEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Ellipse el) return;
        el.Width = 24; el.Height = 24;
        // Re-position so the centre stays put after the size change.
        // Ports live on NodesLayer at (Canvas.Left, Canvas.Top); the
        // node-port positioner resets these on the next move.
        var l = Canvas.GetLeft(el);
        var t = Canvas.GetTop(el);
        Canvas.SetLeft(el, l - 3);
        Canvas.SetTop(el,  t - 3);
        if (el.Effect is System.Windows.Media.Effects.DropShadowEffect ds)
            ds.BlurRadius = 16;
    }

    private void OnPortHoverLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Ellipse el) return;
        el.Width = 18; el.Height = 18;
        var l = Canvas.GetLeft(el);
        var t = Canvas.GetTop(el);
        Canvas.SetLeft(el, l + 3);
        Canvas.SetTop(el,  t + 3);
        if (el.Effect is System.Windows.Media.Effects.DropShadowEffect ds)
            ds.BlurRadius = 10;
    }

    private void OnPortMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Mouse-up on an INPUT port. If we're mid-drag from an output
        // port, finalise the edge.
        if (_edgeFrom is null || _edgeFromKind is null) return;
        if (sender is not Ellipse el || el.Tag is not ValueTuple<NodeView, string> tag) return;
        var (target, kind) = tag;
        if (kind != "in")
        {
            ResetRubber();
            return;
        }
        if (ReferenceEquals(target, _edgeFrom))
        {
            // Self-edge — allow but warn via validator.
        }
        AddEdge(_edgeFrom!.Id, target.Id, _edgeFromKind == "out" ? null : _edgeFromKind);
        ResetRubber();
        e.Handled = true;
        UpdateStatus();
    }

    private void ResetRubber()
    {
        _edgeFrom = null;
        _edgeFromKind = null;
        RubberBand.Visibility = Visibility.Collapsed;
        GraphCanvas.ReleaseMouseCapture();
    }

    // ─── Canvas-level mouse tracking ──────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click on empty canvas — nothing to do; node/port handlers
        // fire first via routed events.
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(GraphCanvas);

        // Node drag
        if (_draggingNode is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var x = Math.Max(0, p.X - _dragOffset.X);
            var y = Math.Max(0, p.Y - _dragOffset.Y);
            _draggingNode.X = x;
            _draggingNode.Y = y;
            Canvas.SetLeft(_draggingNode.Visual!, x);
            Canvas.SetTop(_draggingNode.Visual!, y);
            UpdateNodePorts(_draggingNode);
            // Refresh any edges touching this node.
            foreach (var edge in _edges)
                if (edge.From == _draggingNode.Id || edge.To == _draggingNode.Id)
                    UpdateEdgeGeometry(edge);
        }

        // Edge rubber-band
        if (_edgeFrom is not null)
        {
            var pf = new PathFigure { StartPoint = _rubberStart };
            // Cubic bezier, control points pull vertically so the
            // line curves like the saved edges.
            var dy = Math.Max(40, Math.Abs(p.Y - _rubberStart.Y) / 2);
            pf.Segments.Add(new BezierSegment(
                new Point(_rubberStart.X, _rubberStart.Y + dy),
                new Point(p.X,           p.Y - dy),
                p, true));
            var pg = new PathGeometry();
            pg.Figures.Add(pf);
            RubberBand.Data = pg;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingNode is not null)
        {
            _draggingNode.Visual?.ReleaseMouseCapture();
            _draggingNode = null;
        }
        if (_edgeFrom is not null)
        {
            // Phase 22 hot-fix — drop-anywhere-on-the-card.
            // Hit-test under the cursor and walk up the visual tree
            // until we find the Border that's tagged with a NodeView.
            // This means the user can drop on the node BODY, not just
            // the 18px input port — much easier to hit.
            var p = e.GetPosition(NodesLayer);
            var hit = VisualTreeHelper.HitTest(NodesLayer, p);
            var target = FindNodeViewAt(hit?.VisualHit);
            if (target is not null && !ReferenceEquals(target, _edgeFrom))
            {
                AddEdge(_edgeFrom!.Id, target.Id,
                    _edgeFromKind == "out" ? null : _edgeFromKind);
                ResetRubber();
                UpdateStatus();
                e.Handled = true;
                return;
            }
            // Released on empty canvas — cancel the in-flight edge.
            ResetRubber();
        }
    }

    /// <summary>Walk up the visual tree from <paramref name="hit"/> until
    /// we find a <see cref="Border"/> whose <c>Tag</c> is a NodeView,
    /// or we run out of ancestors. Used so dropping a rubber-band edge
    /// on any part of a node card (not just the tiny input port) is
    /// treated as a drop on that node's input.</summary>
    private NodeView? FindNodeViewAt(DependencyObject? hit)
    {
        var cur = hit;
        while (cur is not null)
        {
            if (cur is Border b && b.Tag is NodeView nv) return nv;
            // Ports are Ellipses tagged with (NodeView, kind) — accept
            // those too so a drop on the input port still works.
            if (cur is Ellipse el && el.Tag is ValueTuple<NodeView, string> portTag)
                return portTag.Item1;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void OnCanvasRightUp(object sender, MouseButtonEventArgs e)
    {
        // Right-click on an edge → delete it. We use a hit-test on
        // the EdgesLayer's children since edges are Path instances
        // with HitTestVisible=true.
        var p = e.GetPosition(EdgesLayer);
        var hit = VisualTreeHelper.HitTest(EdgesLayer, p);
        if (hit?.VisualHit is Path path && path.Tag is EdgeView edge)
        {
            RemoveEdge(edge);
            UpdateStatus();
            e.Handled = true;
        }
    }

    // ─── Port positioning ─────────────────────────────────────────

    private void UpdateNodePorts(NodeView nv)
    {
        if (nv.Visual is null) return;
        var w = nv.Visual.Width;
        var h = nv.Visual.ActualHeight > 0 ? nv.Visual.ActualHeight : 80;
        // Phase 22 hot-fix: position ports FULLY OUTSIDE the card.
        // Previously their centre sat on the card edge → 9-px overlap
        // intercepted clicks meant for drag-to-reposition. Now the
        // port's bottom (input) or top (output) just kisses the card
        // edge with no overlap. Card body fully reclaims its hit-test
        // area for drag.
        const double size = 18;        // port diameter
        const double halfX = size / 2; // centre-X offset
        const double gap   = 1;        // 1-px breathing room between port and card
        // Input port (top centre, fully above the card)
        if (nv.InPort is { } inP && !NodesLayer.Children.Contains(inP))
        {
            NodesLayer.Children.Add(inP);
        }
        if (nv.InPort is not null)
        {
            Canvas.SetLeft(nv.InPort, nv.X + w / 2 - halfX);
            Canvas.SetTop(nv.InPort,  nv.Y - size - gap);
            Panel.SetZIndex(nv.InPort, 100);
        }
        // Output ports (bottom centre, fully below the card)
        var isIf = string.Equals(nv.Type, "if", StringComparison.OrdinalIgnoreCase);
        if (!isIf)
        {
            if (nv.OutPort is { } oP && !NodesLayer.Children.Contains(oP))
                NodesLayer.Children.Add(oP);
            if (nv.OutPort is not null)
            {
                Canvas.SetLeft(nv.OutPort, nv.X + w / 2 - halfX);
                Canvas.SetTop(nv.OutPort,  nv.Y + h + gap);
                Panel.SetZIndex(nv.OutPort, 100);
            }
        }
        else
        {
            if (nv.ThenPort is { } tP && !NodesLayer.Children.Contains(tP))
                NodesLayer.Children.Add(tP);
            if (nv.ElsePort is { } eP && !NodesLayer.Children.Contains(eP))
                NodesLayer.Children.Add(eP);
            if (nv.ThenPort is not null)
            {
                Canvas.SetLeft(nv.ThenPort, nv.X + w / 3 - halfX);
                Canvas.SetTop(nv.ThenPort,  nv.Y + h + gap);
                Panel.SetZIndex(nv.ThenPort, 100);
            }
            if (nv.ElsePort is not null)
            {
                Canvas.SetLeft(nv.ElsePort, nv.X + 2 * w / 3 - halfX);
                Canvas.SetTop(nv.ElsePort,  nv.Y + h + gap);
                Panel.SetZIndex(nv.ElsePort, 100);
            }
        }
    }

    // ─── Edges ────────────────────────────────────────────────────

    private EdgeView? _selectedEdge;

    private void AddEdge(string from, string to, string? label)
    {
        // De-dupe — silently drop a duplicate edge.
        if (_edges.Any(x => x.From == from && x.To == to
                            && string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase)))
            return;

        var path = new Path
        {
            Stroke = (Brush)Application.Current.Resources["Accent"],
            StrokeThickness = 3,        // Phase 22: thicker edges, easier to click
            Cursor = Cursors.Hand,
        };
        var edge = new EdgeView { From = from, To = to, Label = label, Visual = path };
        path.Tag = edge;
        // Phase 22: left-click selects the edge (Delete key removes it).
        path.MouseLeftButtonDown += OnEdgeMouseDown;
        EdgesLayer.Children.Add(path);
        _edges.Add(edge);
        UpdateEdgeGeometry(edge);
    }

    private void OnEdgeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Path p || p.Tag is not EdgeView edge) return;
        // Clear previous selection visual
        if (_selectedEdge?.Visual is Path prev)
        {
            prev.StrokeThickness = 3;
            prev.Effect = null;
        }
        _selectedEdge = edge;
        p.StrokeThickness = 5;
        // Pull the glow colour from the stroke if it's a solid brush;
        // fall back to accent if not (defensive, mirrors ResolveBrush).
        var glowColor = (p.Stroke as SolidColorBrush)?.Color
            ?? (Color)ColorConverter.ConvertFromString("#FF60A5FA");
        p.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = glowColor,
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 1.0,
        };
        StatusText.Text = $"Edge selected: {edge.From} → {edge.To}"
            + (string.IsNullOrEmpty(edge.Label) ? " (press Delete to remove)" : $" [{edge.Label}] (press Delete to remove)");
        e.Handled = true;
    }

    private void RemoveEdge(EdgeView e)
    {
        if (e.Visual is not null) EdgesLayer.Children.Remove(e.Visual);
        _edges.Remove(e);
        if (ReferenceEquals(_selectedEdge, e)) _selectedEdge = null;
    }

    private void UpdateEdgeGeometry(EdgeView edge)
    {
        if (!_nodes.TryGetValue(edge.From, out var f)) return;
        if (!_nodes.TryGetValue(edge.To,   out var t)) return;
        if (f.Visual is null || t.Visual is null) return;

        var fw = f.Visual.Width;
        var tw = t.Visual.Width;
        var fh = f.Visual.ActualHeight > 0 ? f.Visual.ActualHeight : 80;
        // Output port position depends on label for if-nodes.
        Point start;
        if (string.Equals(f.Type, "if", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(edge.Label, "then", StringComparison.OrdinalIgnoreCase))
                start = new Point(f.X + fw / 3, f.Y + fh);
            else if (string.Equals(edge.Label, "else", StringComparison.OrdinalIgnoreCase))
                start = new Point(f.X + 2 * fw / 3, f.Y + fh);
            else
                start = new Point(f.X + fw / 2, f.Y + fh);
        }
        else
        {
            start = new Point(f.X + fw / 2, f.Y + fh);
        }
        var end = new Point(t.X + tw / 2, t.Y);

        var dy = Math.Max(40, Math.Abs(end.Y - start.Y) / 2);
        var pg = new PathGeometry();
        var pf = new PathFigure { StartPoint = start };
        pf.Segments.Add(new BezierSegment(
            new Point(start.X, start.Y + dy),
            new Point(end.X,   end.Y - dy),
            end, true));
        pg.Figures.Add(pf);
        if (edge.Visual is Path p) p.Data = pg;

        // Colour-code labelled edges so then/else read at a glance.
        if (edge.Visual is Path pp)
        {
            pp.Stroke = string.Equals(edge.Label, "then", StringComparison.OrdinalIgnoreCase)
                ? (Brush)Application.Current.Resources["OkBrush"]
                : string.Equals(edge.Label, "else", StringComparison.OrdinalIgnoreCase)
                    ? (Brush)Application.Current.Resources["WarnBrush"]
                    : (Brush)Application.Current.Resources["Accent"];
        }
    }

    // ─── Node param edit ──────────────────────────────────────────

    private void OnNodeEditParams(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not NodeView nv) return;
        // Phase 22 hot-fix — if / while_loop nodes route to the
        // ConditionBuilderDialog because their meaningful payload is
        // the condition tree, not params. Other nodes use the
        // existing typed-form dialog.
        var typeKey = nv.Type.ToLowerInvariant();
        if (typeKey is "if" or "while_loop")
        {
            var cdlg = new ConditionBuilderDialog(nv.ConditionJson) { Owner = this };
            if (cdlg.ShowDialog() != true || cdlg.Result is null) return;
            nv.ConditionJson = cdlg.Result;
            if (nv.SummaryText is not null)
                nv.SummaryText.Text = SummariseCondition(nv.ConditionJson);
            return;
        }
        // Phase 70 — pass current Probability so the inline slider on the
        // dialog's params form is positioned correctly; write the slider's
        // result back so saving the dialog also persists the new value.
        var dlg = new ScriptStepParamsTypedDialog(nv.Type, nv.ParamsJson, nv.Probability) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        nv.ParamsJson = dlg.Result;
        nv.Probability = dlg.Probability;
        if (nv.SummaryText is not null)
            nv.SummaryText.Text = SummariseParams(nv.ParamsJson);
    }

    /// <summary>One-line summary of a condition JSON for the node card.
    /// Shows the kind + a tiny hint of the first param, e.g.
    /// <c>"var_equals: name=country"</c>.</summary>
    private static string SummariseCondition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no condition)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            var kind = doc.RootElement.TryGetProperty("kind", out var k)
                ? k.GetString() ?? "?" : "?";
            var bits = new List<string> { kind };
            if (doc.RootElement.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in p.EnumerateObject())
                {
                    var v = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString() ?? ""
                        : kv.Value.GetRawText();
                    if (v.Length > 18) v = v[..15] + "…";
                    bits.Add($"{kv.Name}={v}");
                    if (bits.Count >= 2) break;
                }
            }
            return string.Join("  ·  ", bits);
        }
        catch { return "(invalid)"; }
    }

    /// <summary>
    /// Phase 22 hot-fix — delete a node + every edge that touches it.
    /// Confirmation dialog so accidental clicks don't nuke careful
    /// graph wiring.
    /// </summary>
    private void OnNodeDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not NodeView nv) return;
        var touching = _edges.Count(x => x.From == nv.Id || x.To == nv.Id);
        var msg = touching > 0
            ? $"Delete node '{nv.Id}' ({nv.Type}) and {touching} connecting edge(s)?"
            : $"Delete node '{nv.Id}' ({nv.Type})?";
        if (MessageBox.Show(this, msg, "Delete node",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        RemoveNode(nv);
        UpdateStatus();
    }

    /// <summary>Remove a node + every edge attached to it from the
    /// canvas and the in-memory model. Safe to call at any time —
    /// clears selection state if the deleted node was selected.</summary>
    private void RemoveNode(NodeView nv)
    {
        // Detach every edge touching this node.
        var dead = _edges.Where(e => e.From == nv.Id || e.To == nv.Id).ToList();
        foreach (var e in dead) RemoveEdge(e);

        // Remove ports + visual.
        if (nv.InPort   is not null) NodesLayer.Children.Remove(nv.InPort);
        if (nv.OutPort  is not null) NodesLayer.Children.Remove(nv.OutPort);
        if (nv.ThenPort is not null) NodesLayer.Children.Remove(nv.ThenPort);
        if (nv.ElsePort is not null) NodesLayer.Children.Remove(nv.ElsePort);
        if (nv.Visual   is not null) NodesLayer.Children.Remove(nv.Visual);

        _nodes.Remove(nv.Id);
        if (ReferenceEquals(_connectFrom, nv)) _connectFrom = null;
    }

    // ─── Existing-graph load ──────────────────────────────────────

    private void LoadExistingGraph()
    {
        if (string.IsNullOrWhiteSpace(_existing.NodesJson)) return;
        // Reuse the runtime parser so node JSON shape stays in sync.
        var parsed = GraphTraverser.Parse(_existing.NodesJson, _existing.EdgesJson);
        if (parsed is null) return;

        // Phase 22 hot-fix — capture each node's `condition` field
        // separately as raw JSON. ParseStep doesn't currently surface
        // the condition tree (it builds a ScriptCondition?), so we
        // re-parse the original JSON to grab it verbatim.
        Dictionary<string, string>? conditionByNodeId = null;
        if (!string.IsNullOrWhiteSpace(_existing.NodesJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(_existing.NodesJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    conditionByNodeId = new(StringComparer.Ordinal);
                    foreach (var n in doc.RootElement.EnumerateArray())
                    {
                        if (n.ValueKind != JsonValueKind.Object) continue;
                        var id = n.TryGetProperty("id", out var idEl)
                            ? idEl.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(id)) continue;
                        if (n.TryGetProperty("condition", out var c)
                            && c.ValueKind == JsonValueKind.Object)
                            conditionByNodeId[id] = c.GetRawText();
                    }
                }
            }
            catch { /* malformed — drop, no conditions hydrated */ }
        }

        foreach (var n in parsed.Nodes)
        {
            // Reconstruct params JSON for the editor — the parser
            // dropped it into a Dictionary; we need it back as JSON.
            var paramsJson = SerialiseParams(n.Step.Params);
            var nv = AddNode(n.Type, n.X, n.Y, n.Id, paramsJson, n.Step.Enabled);
            // Phase 21 audit fix #5 — hydrate per-step flags from the
            // ScriptStep that GraphTraverser.ParseStep already built.
            nv.Probability    = n.Step.Probability;
            nv.AbortOnError   = n.Step.AbortOnError;
            nv.SkipOnMyDomain = n.Step.SkipOnMyDomain;
            nv.SkipOnTarget   = n.Step.SkipOnTarget;
            nv.OnlyOnTarget   = n.Step.OnlyOnTarget;
            nv.OnlyOnMyDomain = n.Step.OnlyOnMyDomain;
            if (conditionByNodeId is not null
                && conditionByNodeId.TryGetValue(n.Id, out var cj))
            {
                nv.ConditionJson = cj;
                if (nv.SummaryText is not null)
                    nv.SummaryText.Text = SummariseCondition(cj);
            }
        }
        foreach (var e in parsed.Edges)
            AddEdge(e.From, e.To, e.Label);

        // Defer port + edge updates until layout pass — ActualHeight
        // is 0 before measure/arrange runs.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var nv in _nodes.Values) UpdateNodePorts(nv);
            foreach (var edge in _edges) UpdateEdgeGeometry(edge);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string SerialiseParams(IReadOnlyDictionary<string, object?> dict)
    {
        if (dict.Count == 0) return "{}";
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var kv in dict)
            {
                w.WritePropertyName(kv.Key);
                if (kv.Value is JsonElement je) je.WriteTo(w);
                else if (kv.Value is null) w.WriteNullValue();
                else w.WriteStringValue(kv.Value.ToString());
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ─── Save / cancel ────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Validate first; block save on Errors, surface Warnings.
        var (nodesJson, edgesJson) = SerialiseGraph();
        var parsed = GraphTraverser.Parse(nodesJson, edgesJson);
        if (parsed is null)
        {
            MessageBox.Show(this, "Internal error serialising graph.",
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var issues = GraphValidator.Validate(parsed);
        var errors = issues.Where(i => i.Level == GraphValidator.Severity.Error).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "Cannot save — fix these issues first:\n\n• "
                + string.Join("\n• ", errors.Select(x => x.Message)),
                "Validation", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var warnings = issues.Where(i => i.Level == GraphValidator.Severity.Warning).ToList();
        if (warnings.Count > 0)
        {
            var resp = MessageBox.Show(this,
                "Save with these warnings?\n\n• "
                + string.Join("\n• ", warnings.Select(x => x.Message)),
                "Validation", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (resp != MessageBoxResult.OK) return;
        }

        Result = _existing with
        {
            LayoutMode = "graph",
            NodesJson  = nodesJson,
            EdgesJson  = edgesJson,
            UpdatedAt  = DateTime.UtcNow,
        };
        ResultExpectedEtag = _existing.ETag;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnBack(object sender, RoutedEventArgs e) => OnCancel(sender, e);

    private void OnJsonView(object sender, RoutedEventArgs e)
    {
        SwitchToJson = true;
        DialogResult = false;
        Close();
    }

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        var (nodesJson, edgesJson) = SerialiseGraph();
        var parsed = GraphTraverser.Parse(nodesJson, edgesJson);
        if (parsed is null)
        {
            MessageBox.Show(this, "Internal error serialising graph.",
                "Validate", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var issues = GraphValidator.Validate(parsed);
        if (issues.Count == 0)
        {
            MessageBox.Show(this, "✓ No issues found.",
                "Validate", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var msg = string.Join("\n", issues.Select(i =>
            $"[{i.Level}] {i.Code}: {i.Message}"));
        MessageBox.Show(this, msg, "Validate",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc cancels in-flight rubber-band edge OR exits Connect tool.
            if (_edgeFrom is not null) { ResetRubber(); e.Handled = true; return; }
            if (_connectMode)
            {
                ConnectToolToggle.IsChecked = false;
                e.Handled = true;
                return;
            }
            if (_selectedEdge is not null)
            {
                ClearSelectedEdge();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete && _selectedEdge is not null)
        {
            // Phase 22 — Delete key removes the selected edge.
            RemoveEdge(_selectedEdge);
            _selectedEdge = null;
            UpdateStatus();
            e.Handled = true;
        }
    }

    private void ClearSelectedEdge()
    {
        if (_selectedEdge?.Visual is Path p)
        {
            p.StrokeThickness = 3;
            p.Effect = null;
        }
        _selectedEdge = null;
    }

    /// <summary>
    /// Phase 22 — Ctrl+Wheel zooms the canvas. Without Ctrl the
    /// scroll-viewer keeps its native scroll behaviour (pan up/down).
    /// </summary>
    private void OnCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var step = e.Delta > 0 ? 0.1 : -0.1;
        var newScale = Math.Clamp(CanvasScale.ScaleX + step, 0.4, 2.5);
        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;
        e.Handled = true;
    }

    private (string nodesJson, string edgesJson) SerialiseGraph()
    {
        // Nodes
        using var nMs = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(nMs))
        {
            w.WriteStartArray();
            foreach (var nv in _nodes.Values)
            {
                w.WriteStartObject();
                w.WriteString("id",   nv.Id);
                w.WriteString("type", nv.Type);
                w.WriteNumber("x", Math.Round(nv.X));
                w.WriteNumber("y", Math.Round(nv.Y));
                if (!nv.Enabled) w.WriteBoolean("enabled", false);
                // Phase 21 audit fix #5 — emit per-step flags when non-
                // default so round-trip preserves probability + abort
                // policy + ad-domain filters. Mirrors SerialiseStep in
                // ScriptVisualEditorDialog.
                if (nv.Probability < 1.0)
                    w.WriteNumber("probability", Math.Round(nv.Probability, 2));
                if (nv.AbortOnError)   w.WriteBoolean("abort_on_error",    true);
                if (nv.SkipOnMyDomain) w.WriteBoolean("skip_on_my_domain", true);
                if (nv.SkipOnTarget)   w.WriteBoolean("skip_on_target",    true);
                if (nv.OnlyOnTarget)   w.WriteBoolean("only_on_target",    true);
                if (nv.OnlyOnMyDomain) w.WriteBoolean("only_on_my_domain", true);
                w.WritePropertyName("params");
                using (var pdoc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(nv.ParamsJson) ? "{}" : nv.ParamsJson))
                    pdoc.RootElement.WriteTo(w);
                // Phase 22 hot-fix — emit `condition` for nodes that
                // have one. Round-trip the existing condition tree
                // through GraphTraverser → ConditionEvaluator unchanged.
                if (!string.IsNullOrWhiteSpace(nv.ConditionJson))
                {
                    try
                    {
                        using var cdoc = JsonDocument.Parse(nv.ConditionJson);
                        w.WritePropertyName("condition");
                        cdoc.RootElement.WriteTo(w);
                    }
                    catch { /* malformed condition — drop silently */ }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        var nodesJson = Encoding.UTF8.GetString(nMs.ToArray());

        // Edges
        using var eMs = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(eMs))
        {
            w.WriteStartArray();
            foreach (var ev in _edges)
            {
                w.WriteStartObject();
                w.WriteString("from", ev.From);
                w.WriteString("to",   ev.To);
                if (!string.IsNullOrEmpty(ev.Label))
                    w.WriteString("label", ev.Label);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        var edgesJson = Encoding.UTF8.GetString(eMs.ToArray());
        return (nodesJson, edgesJson);
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"{_nodes.Count} nodes · {_edges.Count} edges";
        EmptyHint.Visibility = _nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static string IconFor(string type) => type.ToLowerInvariant() switch
    {
        "navigate" or "open_url" or "visit" => "🧭",
        "back" or "forward" or "reload"      => "◀",
        "new_tab" or "close_tab"             => "🪟",
        "switch_tab"                         => "🔀",
        "click_selector" or "click" or "double_click" or "right_click" => "🖱",
        "type" or "press_key" or "fill_form" => "⌨",
        "scroll" or "scroll_to_bottom"       => "↕",
        "hover" or "move_random"             => "🫳",
        "save_var" or "extract_text" or "read" => "💾",
        "execute_js"                         => "⚡",
        "http_request"                       => "🌐",
        "wait" or "dwell" or "random_delay" or "pause" => "⏲",
        "wait_for_selector" or "wait_for_url" => "⏳",
        "if"                                 => "🔀",
        "foreach" or "foreach_ad"            => "🔁",
        "while_loop"                         => "🔄",
        "break" or "continue"                => "⏹",
        "parse_ads" or "click_ad"            => "📣",
        "screenshot"                         => "📷",
        "solve_captcha"                      => "🛡",
        "log"                                => "📝",
        _                                    => "🔧",
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
                    _                    => "{…}",
                };
                if (v.Length > 22) v = v[..19] + "…";
                bits.Add($"{p.Name}={v}");
                if (bits.Count >= 2) { bits.Add("…"); break; }
            }
            return string.Join("  ·  ", bits);
        }
        catch { return "(invalid)"; }
    }

    private sealed record PaletteItem(string Type, string Icon, string Label, string Tooltip);

    /// <summary>One node's editor-side state. Visual is the on-canvas
    /// Border; ports are separate Ellipse children of NodesLayer
    /// because they need to live outside the card's clipping region.</summary>
    private sealed class NodeView
    {
        public required string Id { get; init; }
        public required string Type { get; init; }
        public required string ParamsJson { get; set; }
        public required bool Enabled { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public Border? Visual { get; set; }
        public Ellipse? InPort { get; set; }
        public Ellipse? OutPort { get; set; }
        public Ellipse? ThenPort { get; set; }
        public Ellipse? ElsePort { get; set; }
        public TextBlock? SummaryText { get; set; }

        // Phase 21 audit fix #5: per-step flags must round-trip in
        // graph mode too. Mirror the StepRow flags from list mode so
        // saved graphs honour probability + abort policy + domain
        // filters identically to list mode.
        public double Probability { get; set; } = 1.0;
        public bool AbortOnError { get; set; }
        public bool SkipOnMyDomain { get; set; }
        public bool SkipOnTarget { get; set; }
        public bool OnlyOnTarget { get; set; }
        public bool OnlyOnMyDomain { get; set; }

        /// <summary>
        /// Phase 22 hot-fix — for <c>if</c> / <c>while_loop</c> nodes,
        /// stores the condition tree as raw JSON. Edited via
        /// <see cref="ConditionBuilderDialog"/>; serialised back into
        /// the node's JSON object as the <c>condition</c> sibling of
        /// <c>params</c>. Null for non-conditional nodes.
        /// </summary>
        public string? ConditionJson { get; set; }
    }

    private sealed class EdgeView
    {
        public required string From { get; init; }
        public required string To { get; init; }
        public string? Label { get; set; }
        public Path? Visual { get; set; }
    }
}
