// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;

namespace UeBar.App.Bar.Views;

/// <summary>
/// Wrapper around a <see cref="TextBlock"/>: at most <see cref="MaxLines"/> lines high while not
/// expanded (M6-Fix 2: long Assist answers). <see cref="IsClamped"/> tells whether something is cut
/// off – then the view shows "more".
/// </summary>
internal sealed class ClampedText : Decorator
{
    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(ClampedText), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(ClampedText), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyPropertyKey IsClampedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsClamped), typeof(bool), typeof(ClampedText), new PropertyMetadata(false));

    public static readonly DependencyProperty IsClampedProperty = IsClampedKey.DependencyProperty;

    public ClampedText()
    {
        ClipToBounds = true;
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>The text is longer than <see cref="MaxLines"/> lines (independent of expanding).</summary>
    public bool IsClamped => (bool)GetValue(IsClampedProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var full = child.DesiredSize;
        var limit = double.PositiveInfinity;
        if (child is TextBlock text)
        {
            var lineHeight = double.IsNaN(text.LineHeight) || text.LineHeight <= 0 ? text.FontFamily.LineSpacing * text.FontSize : text.LineHeight;
            limit = Math.Ceiling(lineHeight * Math.Max(1, MaxLines));
        }

        var clamped = full.Height > limit + 1;
        if (IsClamped != clamped)
        {
            SetValue(IsClampedKey, clamped);
        }

        return clamped && !IsExpanded ? new Size(full.Width, Math.Min(limit, constraint.Height)) : full;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        // Arrange the child at full height – only what fits into the (limited) area is visible.
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, Child.DesiredSize.Height)));
        return arrangeSize;
    }
}
