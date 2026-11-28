// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using UeBar.App.ClipHistory;
using UeBar.App.Infrastructure;
using UeBar.App.Interop;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Modules;
using UeBar.Sdk;

namespace UeBar.App.ContentActions;

/// <summary>
/// Hotkey "Send marked text to …" (P3-1, default Win+Alt+C): copies the marking of the window in front (the clipboard
/// gets its old content back at once), then shows the list of content actions for it at the text cursor. The window
/// it came from is the target of "Paste" in the answer window. Content goes nowhere until the user picks an action.
/// </summary>
internal sealed class SelectionAction
{
    public const string HotkeyName = "content.selection";

    private readonly Dispatcher _dispatcher;
    private readonly ContentActionService _actions;
    private readonly ClipboardWatcher _watcher;
    private readonly IUeLogger _log;
    private int _running;

    public SelectionAction(Dispatcher dispatcher, ContentActionService actions, ClipboardWatcher watcher, IUeLogger log)
    {
        _dispatcher = dispatcher;
        _actions = actions;
        _watcher = watcher;
        _log = log;
    }

    /// <summary>Registers the hotkey from the settings (empty = none).</summary>
    public void Bind(GlobalHotkeys hotkeys, UeBar.App.Settings.GeneralSettingsStore general)
    {
        _hotkeys = hotkeys;
        _general = general;
        hotkeys.Bind(HotkeyName, general.Current.SelectionHotkey, Run);
    }

    private GlobalHotkeys? _hotkeys;
    private UeBar.App.Settings.GeneralSettingsStore? _general;

    public (HotkeyRegistration Result, string Message) HotkeyStatus =>
        _hotkeys?.Set.StatusOf(HotkeyName) ?? (HotkeyRegistration.Invalid, "");

    public string CurrentHotkey => _general?.Current.SelectionHotkey ?? "";

    /// <summary>New hotkey – only taken if it works; empty = none.</summary>
    public (HotkeyRegistration Result, string Message) ChangeHotkey(string text)
    {
        if (_hotkeys is null || _general is null)
        {
            return (HotkeyRegistration.Invalid, "");
        }

        if (text.Length == 0)
        {
            _general.Update(g => g.SelectionHotkey = "");
            _hotkeys.Bind(HotkeyName, "", Run);
            return HotkeyStatus;
        }

        var result = _hotkeys.Set.TryChange(HotkeyName, text, Run);
        if (result.Result == HotkeyRegistration.Registered && Hotkey.TryParse(text) is { } hotkey)
        {
            _general.Update(g => g.SelectionHotkey = hotkey.Text);
            _log.Info(null, $"Hotkey send marked text: {hotkey.Text}");
        }

        return result;
    }

    /// <summary>Short notices ("Nothing marked").</summary>
    public Action<string>? Notice { get; set; }

    /// <summary>The hotkey was pressed (UI thread).</summary>
    public void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            if (_actions.GetActions(ContentKinds.Text).Count == 0)
            {
                Notice?.Invoke(Localization.Loc.Get("Selection_NoActions"));
                return;
            }

            var target = PasteService.TargetOf(Win32.GetForegroundWindow());
            if (target is null)
            {
                Notice?.Invoke(Localization.Loc.Get("Selection_NoWindow"));
                return;
            }

            SelectionCopyResult result;
            using (_watcher.PauseReporting())
            {
                result = await SelectionCopier.CopyAsync(new WindowsSelectionIo(), (delay, ct) => Task.Delay(delay, ct), CancellationToken.None);
            }

            _log.Info(null, $"Marked text: {result.Outcome}{(result.Text is { } t ? $", {t.Length} chars" : "")}, clipboard restored {result.Restored}{(result.WaitedForKeys ? ", waited for the hotkey's keys" : "")}");
            if (result.Outcome != SelectionCopyOutcome.Text || result.Text is not { } text)
            {
                Notice?.Invoke(Localization.Loc.Get(result.Outcome == SelectionCopyOutcome.Failed ? "Selection_Failed" : "Selection_Nothing"));
                return;
            }

            await _actions.ChooseAsync(null, new ContentItem { Text = text, SourceTitle = target.Title }, new ContentActionStart
            {
                Origin = ContentOrigin.Selection,
                PasteTarget = target,
                Placement = ChoicePlacement.Caret,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error(null, "Send marked text failed", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>The real clipboard and keyboard (UI thread – the clipboard needs STA).</summary>
    internal sealed class WindowsSelectionIo : ISelectionIo
    {
        private static readonly uint HistoryFormat = ClipInterop.RegisterClipboardFormat(ClipboardPrivacy.HistoryFormat);

        public uint Sequence => ClipInterop.GetClipboardSequenceNumber();

        public bool ModifiersDown() =>
            ClipInterop.IsDown(ClipInterop.VK_LWIN) || ClipInterop.IsDown(ClipInterop.VK_RWIN) || ClipInterop.IsDown(ClipInterop.VK_MENU)
            || ClipInterop.IsDown(ClipInterop.VK_SHIFT) || ClipInterop.IsDown(ClipInterop.VK_CONTROL);

        public object? Snapshot() => Copy(Retry(Clipboard.GetDataObject));

        public bool Restore(object? snapshot)
        {
            var data = snapshot as DataObject ?? new DataObject();

            // Windows' own history saw the content once already – the restored copy does not count again.
            data.SetData(ClipboardPrivacy.HistoryFormat, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
            try
            {
                return Retry(() =>
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                });
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                return false;
            }
        }

        public bool SendCopy() => ClipInterop.Send(
            ClipInterop.Key(ClipInterop.VK_CONTROL, false),
            ClipInterop.Key(VkC, false),
            ClipInterop.Key(VkC, true),
            ClipInterop.Key(ClipInterop.VK_CONTROL, true));

        public string? ReadText()
        {
            try
            {
                return Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                return null;
            }
        }

        private const int VkC = 0x43;

        /// <summary>A copy of every format that can be read – the original object belongs to the app that copied it.</summary>
        private static DataObject? Copy(IDataObject? source)
        {
            if (source is null)
            {
                return null;
            }

            var copy = new DataObject();
            var any = false;
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                if (format == ClipboardPrivacy.HistoryFormat)
                {
                    continue;
                }

                try
                {
                    if (source.GetData(format, autoConvert: false) is { } value && value is string or MemoryStream or string[] or System.Windows.Media.Imaging.BitmapSource)
                    {
                        copy.SetData(format, value is MemoryStream stream ? new MemoryStream(stream.ToArray()) : value, autoConvert: false);
                        any = true;
                    }
                }
                catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
                {
                    // a format only its app can give (e.g. delayed rendering) – left out
                }
            }

            return any ? copy : null;
        }

        private static T Retry<T>(Func<T> read)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return read();
                }
                catch (COMException) when (attempt < 5)
                {
                    Thread.Sleep(20 * attempt); // another app holds the clipboard for a moment
                }
            }
        }

        // The history format of Windows is read elsewhere; kept here so the format is registered once.
        internal static uint HistoryFormatId => HistoryFormat;
    }
}
