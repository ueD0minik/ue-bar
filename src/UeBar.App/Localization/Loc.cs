// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using UeBar.Sdk;
using UeBar.Bar;
using UeBar.Core.Localization;

namespace UeBar.App.Localization;

/// <summary>UI texts of the app (<c>UeBar.App/Resources/Strings*.resx</c>, R1).</summary>
public static class Loc
{
    public static TextCatalog T { get; } = TextCatalog.For(typeof(Loc).Assembly, "UeBar.App", () => UiCultureSource.Shared.Current);

    public static string Get(string key) => T[key];

    public static string F(string key, params object?[] args) => T.Format(key, args);

    /// <summary>Current UI culture (texts only).</summary>
    public static CultureInfo Culture => UiCultureSource.Shared.Current;

    /// <summary>Culture for numbers, dates and times: always the Windows region, independent of the UI language.</summary>
    public static CultureInfo FormatCulture => CultureInfo.CurrentCulture;

    private static bool _wpfLanguageSet;

    /// <summary>
    /// Applies the language setting: resolves the UI culture and sets the thread/default UI cultures.
    /// Formats stay with the Windows region (CurrentCulture is not touched); WPF elements get the
    /// region's language once per process (WPF does not allow more). Returns
    /// true if the culture changed (then <see cref="UiCultureSource.Changed"/> was raised).
    /// </summary>
    public static bool Apply(string? languageSetting)
    {
        var culture = UiLanguages.Resolve(languageSetting, SystemUiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (!_wpfLanguageSet)
        {
            _wpfLanguageSet = true;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(SystemFormatCulture.IetfLanguageTag)));
        }

        var changed = UiCultureSource.Shared.Set(culture);
        if (changed)
        {
            LocSource.Instance.Refresh();
        }

        return changed;
    }

    /// <summary>Windows display language of the user before the app changed anything ("system").</summary>
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    /// <summary>Regional format of the user (Windows region, e.g. de-AT) – used for all numbers and dates.</summary>
    private static readonly CultureInfo SystemFormatCulture = CultureInfo.CurrentCulture;

    /// <summary>Name of the language "system" resolves to ("Deutsch" or "English").</summary>
    public static string SystemLanguageName =>
        UiLanguages.Resolve(UiLanguages.System, SystemUiCulture).TwoLetterISOLanguageName == UiLanguages.German ? "Deutsch" : "English";

    /// <summary>Display name of a language setting value in the current UI language.</summary>
    public static string LanguageName(string setting) => setting switch
    {
        UiLanguages.German => "Deutsch",
        UiLanguages.English => "English",
        _ => Get("Language_System"),
    };

    /// <summary>Key names of hotkeys etc. come from the bar logic's resources.</summary>
    public static string KeyName(string part) => BarText.KeyName(part);
}

/// <summary>
/// Source for XAML bindings to texts: <c>{l:T Key}</c> binds to <c>LocSource.Instance[Key]</c>,
/// so every bound text updates when the language changes (R1).
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    public string this[string key] => Loc.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>XAML: <c>Text="{l:T Panel_Search}"</c> – a localized text that follows language switches.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
