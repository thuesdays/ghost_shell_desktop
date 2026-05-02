// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Profile create/edit modal — visual + functional match of the
/// legacy web project's "Create new profile" form: device template
/// dropdown grouped Desktop / Mobile / Tablet, preferred-language
/// picker, optional proxy, "enrich on first run" toggle, random
/// name generator.
/// </summary>
public partial class ProfileEditorDialog : Window
{
    /// <summary>Sentinel for the "Auto (random per profile)" option.</summary>
    private const string AutoTemplateId = "";

    private readonly Profile? _existing;
    public Profile? Result { get; private set; }

    public ProfileEditorDialog(Profile? existing, IReadOnlyList<ProxyOption> proxyOptions)
    {
        InitializeComponent();

        _existing = existing;
        BuildTemplateCombo();
        BuildLanguageCombo();
        BuildProxyCombo(proxyOptions);

        if (existing is not null)
        {
            // ─── Edit mode ───
            TitleText.Text         = $"Edit profile — {existing.Name}";
            NameField.Text         = existing.Name;
            NameField.IsEnabled    = false; // primary key
            GroupField.Text        = existing.GroupName ?? "";
            NoteField.Text         = existing.Note ?? "";
            // Phase 20 — load CSV domain lists.
            MyDomainsField.Text     = existing.MyDomainsCsv ?? "";
            TargetDomainsField.Text = existing.TargetDomainsCsv ?? "";
            IsReadyField.IsChecked = existing.IsReady;
            EnrichField.IsChecked  = existing.EnrichOnFirstRun;
            TemplateCombo.SelectedValue = existing.TemplateId ?? AutoTemplateId;
            LanguageCombo.SelectedValue = existing.Language    ?? "uk-UA";
            if (!string.IsNullOrEmpty(existing.ProxySlug))
                ProxyCombo.SelectedValue = existing.ProxySlug;
        }
        else
        {
            // ─── Create mode ───
            TitleText.Text          = "Create new profile";
            NameField.Text          = SuggestNextProfileName();
            TemplateCombo.SelectedValue = AutoTemplateId;
            LanguageCombo.SelectedValue = "uk-UA";
            ProxyCombo.SelectedIndex    = 0; // "(none)"
            EnrichField.IsChecked   = true;
            NameField.Focus();
            NameField.SelectAll();
        }
    }

    // ─── Combo builders ────────────────────────────────────────────

    /// <summary>
    /// Build the template combo with section-header rows in front of
    /// each form-factor group (mimics the &lt;optgroup&gt; rendering of
    /// the web select). Each item is a DTO with Id (for SelectedValue)
    /// + Label + an optional Header line shown above the first row of
    /// a new group.
    /// </summary>
    private void BuildTemplateCombo()
    {
        var items = new List<TemplateOption>
        {
            new() { Id = AutoTemplateId, Label = "Auto (random from all)" },
        };

        AppendGroup(items, "💻 DESKTOP / LAPTOP",
            DeviceTemplateCatalog.All.Where(t => t.FormFactor == FormFactor.Desktop));
        AppendGroup(items, "📱 MOBILE",
            DeviceTemplateCatalog.All.Where(t => t.FormFactor == FormFactor.Mobile));
        AppendGroup(items, "📑 TABLET",
            DeviceTemplateCatalog.All.Where(t => t.FormFactor == FormFactor.Tablet));

        TemplateCombo.ItemsSource = items;
    }

    private static void AppendGroup(
        List<TemplateOption> items, string header, IEnumerable<DeviceTemplate> source)
    {
        var first = true;
        foreach (var t in source)
        {
            items.Add(new TemplateOption
            {
                Id     = t.Id,
                Label  = t.ToLabel(),
                Header = first ? header : "",
            });
            first = false;
        }
    }

    private void BuildLanguageCombo()
    {
        // Curated language list. Same set the web project ships with;
        // extras can be appended later without breaking existing data
        // (Language is a free-form string column).
        LanguageCombo.ItemsSource = new[]
        {
            new LanguageOption("uk-UA", "Ukrainian (uk-UA)"),
            new LanguageOption("ru-RU", "Russian (ru-RU)"),
            new LanguageOption("en-US", "English (en-US)"),
            new LanguageOption("en-GB", "English UK (en-GB)"),
            new LanguageOption("pl-PL", "Polish (pl-PL)"),
            new LanguageOption("de-DE", "German (de-DE)"),
            new LanguageOption("fr-FR", "French (fr-FR)"),
            new LanguageOption("es-ES", "Spanish (es-ES)"),
            new LanguageOption("pt-BR", "Portuguese BR (pt-BR)"),
            new LanguageOption("ja-JP", "Japanese (ja-JP)"),
        };
    }

    private void BuildProxyCombo(IReadOnlyList<ProxyOption> proxyOptions)
    {
        var options = new List<ProxyOption> { ProxyOption.None() };
        options.AddRange(
            proxyOptions.OrderBy(o => o.DisplayName,
                StringComparer.OrdinalIgnoreCase));
        ProxyCombo.ItemsSource = options;
    }

    // ─── Random name ──────────────────────────────────────────────

    private void OnRandomName(object sender, RoutedEventArgs e)
    {
        // Cheap pronounceable suggestion — same shape as the web
        // dashboard's 🎲 button. Scopes to readable hex so the user
        // can still type-correct after.
        var rnd = new Random();
        var n   = rnd.Next(1, 100);
        NameField.Text = $"profile_{n:00}";
        NameField.Focus();
        NameField.SelectAll();
    }

    /// <summary>
    /// Suggest "profile_NN" where NN is the smallest unused integer.
    /// Without an enumerated list we just pick a high random — caller
    /// will see if it conflicts on Save and try again.
    /// </summary>
    private static string SuggestNextProfileName()
        => $"profile_{new Random().Next(2, 99):00}";

    // ─── Save / cancel ────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameField.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Name is required.");
            NameField.Focus();
            return;
        }
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Name contains characters that aren't valid in a folder name.");
            NameField.Focus();
            return;
        }

        var templateId = TemplateCombo.SelectedValue as string;
        if (string.IsNullOrEmpty(templateId)) templateId = null; // null = auto

        var pickedSlug = (ProxyCombo.SelectedItem as ProxyOption)?.Slug;
        var proxySlug  = string.IsNullOrWhiteSpace(pickedSlug) ? null : pickedSlug;

        Result = new Profile
        {
            Name             = name,
            GroupName        = NullIfBlank(GroupField.Text),
            TemplateId       = templateId,
            Language         = LanguageCombo.SelectedValue as string,
            ProxySlug        = proxySlug,
            IsReady          = IsReadyField.IsChecked == true,
            EnrichOnFirstRun = EnrichField.IsChecked  == true,
            Note             = NullIfBlank(NoteField.Text),
            // Phase 20 — write CSV domain lists. Blank → null so the
            // runtime sees no policy at all (filters pass through).
            MyDomainsCsv     = NullIfBlank(MyDomainsField.Text),
            TargetDomainsCsv = NullIfBlank(TargetDomainsField.Text),
            CreatedAt        = _existing?.CreatedAt ?? default,
            UpdatedAt        = default,
            LastRunAt        = _existing?.LastRunAt,
            RunCount         = _existing?.RunCount ?? 0,
            // Preserve the existing profile's salts + script binding
            // (the editor doesn't expose them, but we'd lose them on
            // save without copying through).
            FpRegenSalt      = _existing?.FpRegenSalt,
            FpNoiseSalt      = _existing?.FpNoiseSalt,
            AssignedScriptId = _existing?.AssignedScriptId,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string msg)
    {
        ErrorText.Text       = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ─── DTOs for combos ──────────────────────────────────────────

    private sealed class TemplateOption
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string Header { get; init; } = "";
        public Visibility HeaderVisibility =>
            string.IsNullOrEmpty(Header) ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed record LanguageOption(string Tag, string Display);
}
