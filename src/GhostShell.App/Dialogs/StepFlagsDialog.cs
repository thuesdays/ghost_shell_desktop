// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 19. Modal dialog for editing the per-step flags shared by
/// every action: probability gate, abort-on-error, plus the four
/// ad-domain filters (skip_on_my_domain, skip_on_target,
/// only_on_target, only_on_my_domain).
///
/// Code-only Window — keeps the dialog minimal and avoids registering
/// another XAML pair in the project file. Visual style is pulled from
/// the active theme via dynamic resource lookups so dark/light theming
/// stays consistent.
/// </summary>
public sealed class StepFlagsDialog : Window
{
    public bool   Saved { get; private set; }
    public double Probability { get; private set; }
    public bool   AbortOnError { get; private set; }
    public bool   SkipOnMyDomain { get; private set; }
    public bool   SkipOnTarget { get; private set; }
    public bool   OnlyOnTarget { get; private set; }
    public bool   OnlyOnMyDomain { get; private set; }
    public bool   SkipOnBlocked { get; private set; }
    public bool   OnlyOnBlocked { get; private set; }

    private readonly Slider   _prob;
    private readonly TextBlock _probLabel;
    private readonly CheckBox _abort;
    private readonly CheckBox _skipMy;
    private readonly CheckBox _skipTg;
    private readonly CheckBox _onlyTg;
    private readonly CheckBox _onlyMy;
    private readonly CheckBox _skipBlocked;
    private readonly CheckBox _onlyBlocked;

    public StepFlagsDialog(
        string actionLabel,
        double probability,
        bool abortOnError,
        bool skipOnMyDomain,
        bool skipOnTarget,
        bool onlyOnTarget,
        bool onlyOnMyDomain,
        bool skipOnBlocked = false,
        bool onlyOnBlocked = false)
    {
        Title = $"⚙ Step settings — {actionLabel}";
        Width = 480;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new TextBlock
        {
            Text = "Advanced step settings",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var sub = new TextBlock
        {
            Text = "Probability gate, error policy, and per-ad domain filters. Domain filters only fire inside a foreach_ad body.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        header.Children.Add(title);
        header.Children.Add(sub);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetRow(body, 1);

        // Probability slider
        var probRow = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var probHead = new Grid();
        probHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        probHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var probName = new TextBlock { Text = "Probability gate", FontWeight = FontWeights.SemiBold, FontSize = 12 };
        probName.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Grid.SetColumn(probName, 0);
        _probLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Menlo"),
            FontSize = 12,
        };
        _probLabel.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        Grid.SetColumn(_probLabel, 1);
        probHead.Children.Add(probName);
        probHead.Children.Add(_probLabel);
        var probDesc = new TextBlock
        {
            Text = "Roll a die before each invocation. 100% = always; 30% = run on roughly 1 in 3 visits.",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        };
        probDesc.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _prob = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = false,
            Value = Math.Clamp(probability, 0.0, 1.0) * 100,
        };
        _prob.ValueChanged += (_, _) => _probLabel.Text = $"{(int)_prob.Value}%";
        _probLabel.Text = $"{(int)_prob.Value}%";
        probRow.Children.Add(probHead);
        probRow.Children.Add(probDesc);
        probRow.Children.Add(_prob);
        body.Children.Add(probRow);

        _abort = MakeFlagRow(
            "Abort on error",
            "Throw → halts the whole script run instead of moving to the next step. Off = errors are logged and the runner continues.",
            abortOnError);
        body.Children.Add(_abort.Parent as FrameworkElement ?? _abort);

        // Domain-filter section header
        var sect = new TextBlock
        {
            Text = "AD-DOMAIN FILTERS",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 14, 0, 6),
        };
        sect.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(sect);

        _skipMy = MakeFlagRow(
            "Skip on my domain",
            "Skip when the current ad lives on a profile-owned domain (don't self-click).",
            skipOnMyDomain);
        body.Children.Add(_skipMy.Parent as FrameworkElement ?? _skipMy);

        _skipTg = MakeFlagRow(
            "Skip on target domain",
            "Skip when the current ad is on one of the profile's target domains.",
            skipOnTarget);
        body.Children.Add(_skipTg.Parent as FrameworkElement ?? _skipTg);

        _onlyTg = MakeFlagRow(
            "Only on target domain",
            "Run ONLY for ads that point at one of the profile's target domains.",
            onlyOnTarget);
        body.Children.Add(_onlyTg.Parent as FrameworkElement ?? _onlyTg);

        _onlyMy = MakeFlagRow(
            "Only on my domain",
            "Run ONLY for ads on a profile-owned domain (rare; mostly for self-tests).",
            onlyOnMyDomain);
        body.Children.Add(_onlyMy.Parent as FrameworkElement ?? _onlyMy);

        _skipBlocked = MakeFlagRow(
            "Skip on blocked domain",
            "Skip when the current ad is on the block list (domains to ignore entirely).",
            skipOnBlocked);
        body.Children.Add(_skipBlocked.Parent as FrameworkElement ?? _skipBlocked);

        _onlyBlocked = MakeFlagRow(
            "Only on blocked domain",
            "Run ONLY for ads on the block list (debug-only; rare usage).",
            onlyOnBlocked);
        body.Children.Add(_onlyBlocked.Parent as FrameworkElement ?? _onlyBlocked);

        var bodyScroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);

        // Buttons
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "Save", MinWidth = 100, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "ButtonPrimary");
        ok.Click += (_, _) => Save();
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;
    }

    private void Save()
    {
        Probability    = Math.Clamp(_prob.Value / 100.0, 0.0, 1.0);
        AbortOnError   = _abort.IsChecked == true;
        SkipOnMyDomain = _skipMy.IsChecked == true;
        SkipOnTarget   = _skipTg.IsChecked == true;
        OnlyOnTarget   = _onlyTg.IsChecked == true;
        OnlyOnMyDomain = _onlyMy.IsChecked == true;
        SkipOnBlocked  = _skipBlocked.IsChecked == true;
        OnlyOnBlocked  = _onlyBlocked.IsChecked == true;
        Saved = true;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Render one labelled checkbox row with description text. Returns
    /// the CheckBox so the caller can read IsChecked later; the
    /// surrounding wrapper is set as the CheckBox's parent so callers
    /// adding to a panel can use either.
    /// </summary>
    private static CheckBox MakeFlagRow(string label, string desc, bool initial)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = initial,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        cb.SetResourceReference(CheckBox.ForegroundProperty, "Text");
        var d = new TextBlock
        {
            Text = desc,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 2, 0, 0),
        };
        d.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        wrap.Children.Add(cb);
        wrap.Children.Add(d);
        // Hack so caller can do `body.Children.Add(cb.Parent as FrameworkElement ?? cb)`
        // and get the wrapper while still holding the CheckBox reference.
        // Setting Tag is enough to keep references alive.
        cb.Tag = wrap;
        // Reparent: we WANT the wrapper added to the body, not the
        // CheckBox itself. Unwrap by leaving cb out of any panel here
        // and let the caller fish out cb.Parent. But Parent is set
        // automatically when `wrap.Children.Add(cb)` ran above.
        return cb;
    }
}
