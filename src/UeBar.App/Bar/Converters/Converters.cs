// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace UeBar.App.Bar.Converters;

/// <summary>Icon name (from the module) → character in "Segoe Fluent Icons".</summary>
public sealed class IconGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IconGlyphs.Get(value as string ?? parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Mirrors a module collection (which can be changed on any thread) into a
/// collection that is only changed on the UI thread. That keeps WPF binding thread-safe.
/// </summary>
public sealed class UiMirrorConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<object, UiMirror> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable source and INotifyCollectionChanged
            ? Cache.GetValue(source, s => new UiMirror((IEnumerable)s))
            : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class UiMirror : ObservableCollection<object>
{
    private readonly IEnumerable _source;

    public UiMirror(IEnumerable source)
    {
        _source = source;
        Resync();
        ((INotifyCollectionChanged)source).CollectionChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(Resync);
    }

    private void Resync()
    {
        object[] snapshot;
        try
        {
            snapshot = [.. _source.Cast<object>()];
        }
        catch (InvalidOperationException)
        {
            // The source was just changed on another thread – try again right away.
            Application.Current?.Dispatcher.BeginInvoke(Resync);
            return;
        }

        Clear();
        foreach (var item in snapshot)
        {
            Add(item);
        }
    }
}

/// <summary>true if all values are equal (e.g. option ID == selected ID).</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>(value, decimals) → formatted number in the UI culture.</summary>
public sealed class NumberMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not double number)
        {
            return "";
        }

        var decimals = values.Length > 1 && values[1] is int d ? Math.Clamp(d, 0, 4) : 0;
        return number.ToString("F" + decimals, CultureInfo.CurrentCulture);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s)) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible if all values are true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible if the text is empty (placeholder in text boxes).</summary>
public sealed class EmptyTextToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
