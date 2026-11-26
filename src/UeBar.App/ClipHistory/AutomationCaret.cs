// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using UeBar.App.Interop;
using UeBar.Bar;

namespace UeBar.App.ClipHistory;

/// <summary>
/// The text cursor through accessibility (P2-6-Fix 1) – for apps that do not use the system caret: Chromium and
/// Electron (Chrome, Edge, the Claude app, Obsidian, Discord …), WinUI/XAML (Explorer's address bar, Windows search,
/// Settings) and WebView2. Only reads, never moves the focus. Called by <see cref="CaretLocator"/> on its own thread
/// with a time limit, only when the choice popup opens.
/// </summary>
internal static class AutomationCaret
{
    private const uint ObjidCaret = 0xFFFFFFF8;

    /// <summary>Steps for the debug log (what answered, never any text).</summary>
    public static Action<string>? Trace { get; set; }
    private static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>The caret of the window in front: the caret object (MSAA), else the text pattern of the focused element (UIA).</summary>
    public static PixelRect? Find(IntPtr window, IntPtr focus)
    {
        return FromCaretObject(focus != IntPtr.Zero ? focus : window) ?? FromTextPattern();
    }

    /// <summary>Chromium and Electron publish the caret as an accessible object of the focused window.</summary>
    private static PixelRect? FromCaretObject(IntPtr hwnd)
    {
        var iid = IidAccessible;
        var hr = AccessibleObjectFromWindow(hwnd, ObjidCaret, ref iid, out var accessible);
        Trace?.Invoke($"caret object: hr 0x{hr:X8}, {(accessible is null ? "none" : accessible is Accessibility.IAccessible ? "accessible" : "other")}");
        if (hr != 0 || accessible is not Accessibility.IAccessible caret)
        {
            return null;
        }

        try
        {
            caret.accLocation(out var x, out var y, out var width, out var height, 0);
            Trace?.Invoke($"caret object: size {width}×{height}");
            return height > 0 ? new PixelRect(x, y, Math.Max(1, width), height) : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or NotImplementedException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(caret);
        }
    }

    /// <summary>UI Automation: the selection (an empty one is the caret) of the focused text element.</summary>
    private static PixelRect? FromTextPattern()
    {
        try
        {
            // UWP apps (Settings) draw in another process than their frame window – only ue Bar itself is skipped; the
            // caller checks that the position lies on the window in front.
            var element = AutomationElement.FocusedElement;
            Trace?.Invoke($"focused element: {(element is null ? "none" : $"{element.Current.ControlType.ProgrammaticName}, own process {element.Current.ProcessId == Environment.ProcessId}")}");
            if (element is not { } focused || focused.Current.ProcessId == Environment.ProcessId)
            {
                return null; // the focus is somewhere else (or in ue Bar)
            }

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern text
                && text.GetSelection() is [var selection, ..])
            {
                if (Rect(selection) is { } direct)
                {
                    return direct with { Width = 1 }; // the start of the selection
                }

                // An empty range often has no rectangle: take the character after the caret – or the one before it.
                var next = selection.Clone();
                next.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
                if (Rect(next) is { } after)
                {
                    return after with { Width = 1 };
                }

                var previous = selection.Clone();
                previous.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
                if (Rect(previous) is { } before)
                {
                    return new PixelRect(before.Right, before.Y, 1, before.Height);
                }
            }

            // A text field without a readable caret (some search boxes): under the field's text line.
            var bounds = focused.Current.BoundingRectangle;
            var type = focused.Current.ControlType;
            if ((type == ControlType.Edit || type == ControlType.ComboBox) && !bounds.IsEmpty && bounds.Height is > 4 and < 120)
            {
                return new PixelRect((int)bounds.X + 8, (int)bounds.Y, 1, (int)bounds.Height);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            // The app answered badly – "not found".
            Trace?.Invoke($"text pattern: {ex.GetType().Name}");
        }

        return null;
    }

    private static PixelRect? Rect(TextPatternRange range)
    {
        var rects = range.GetBoundingRectangles();
        foreach (var r in rects)
        {
            if (!r.IsEmpty && r.Height > 0)
            {
                return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), (int)Math.Round(r.Height));
            }
        }

        return null;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? accessible);
}
