// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UeBar.App.ContentActions;
using UeBar.App.Infrastructure;
using UeBar.App.Settings;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Settings;

// Real windows and the real clipboard: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UeBar.App.Tests;

/// <summary>
/// P3-1 with real Windows (category "Desktop"): the app's own dialog with the keyboard, "hide from screen captures"
/// on that dialog, and the clipboard put back after "Send marked text to …". Keys are given to the window directly –
/// no real key presses, the mouse is never moved. The clipboard test keeps its texts out of the Windows history and
/// gives the clipboard its old content back.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class DesktopTests
{
    [Theory]
    [InlineData(Key.Enter, false, true)]   // Enter = the focused button (Yes)
    [InlineData(Key.Escape, false, false)] // Esc = No
    [InlineData(Key.Enter, true, false)]   // a dangerous question starts on No
    [InlineData(Key.Tab, false, false)]    // Tab to "No", then Enter
    public void Own_dialog_answers_the_keyboard(Key key, bool safeDefault, bool expected)
    {
        var answer = Wpf.Run(() =>
        {
            var dialog = new UeDialog("Test?", "ue Bar", DialogKind.Question, DialogButtons.YesNo, safeDefault, null, null);
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (key == Key.Tab)
                {
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Press(dialog, Key.Enter);
                }
                else
                {
                    Press(dialog, key);
                }
            });
            dialog.ShowDialog();
            return dialog.Answer;
        });

        Assert.Equal(expected ? DialogAnswer.Confirmed : DialogAnswer.Declined, answer);
    }

    [Fact]
    public void Own_dialog_is_hidden_from_screen_captures()
    {
        var (expected, actual) = Wpf.Run(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "uebar-desktop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var settings = new SettingsService(Path.Combine(folder, "settings.json"), null, NullLogger.Instance);
            settings.Load();
            using var protection = new CaptureProtection(Dispatcher.CurrentDispatcher, new GeneralSettingsStore(settings), NullLogger.Instance);
            protection.Start(); // default since 1.7.0: on
            var dialog = new UeDialog("Info", null, DialogKind.Info, DialogButtons.Ok, false, null, null) { ShowActivated = false, Left = -2000, Top = -2000 };
            uint affinity = 0;
            dialog.ContentRendered += (_, _) =>
            {
                GetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(dialog).Handle, out affinity);
                dialog.Close();
            };
            dialog.ShowDialog();
            Directory.Delete(folder, recursive: true);
            return (protection.IsAvailable ? protection.ExpectedAffinity : 0u, affinity);
        });

        Assert.Equal(expected, actual);
        Assert.NotEqual(0u, actual);
    }

    [Fact]
    public void Marked_text_copy_puts_the_old_clipboard_back_and_keeps_it_out_of_the_history()
    {
        var (before, after, marked) = Wpf.Run(() =>
        {
            var io = new SelectionAction.WindowsSelectionIo();
            var users = io.Snapshot(); // the real content of whoever runs the tests
            try
            {
                SetPrivate("ue Bar Testtext alt");
                var saved = io.Snapshot();
                SetPrivate("ue Bar Testtext markiert");
                var copied = io.ReadText();
                Assert.True(io.Restore(saved));
                var back = Clipboard.GetText();
                var history = Clipboard.GetData(ClipboardPrivacy.HistoryFormat) is MemoryStream stream ? BitConverter.ToInt32(stream.ToArray()) : -1;
                Assert.Equal(0, history);
                return ("ue Bar Testtext alt", back, copied);
            }
            finally
            {
                io.Restore(users);
            }
        });

        Assert.Equal(before, after);
        Assert.Equal("ue Bar Testtext markiert", marked);
    }

    /// <summary>A test text that neither the Windows history nor clipboard tools take (test rule since 28.11.2026).</summary>
    private static void SetPrivate(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(ClipboardPrivacy.HistoryFormat, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
        data.SetData(ClipboardPrivacy.ExcludeFormat, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
        Clipboard.SetDataObject(data, copy: true);
    }

    private static void Press(Window window, Key key)
    {
        var target = Keyboard.FocusedElement as IInputElement ?? window;
        var source = PresentationSource.FromVisual(window)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}

/// <summary>Runs code on an STA thread with the app's resources loaded (one Application per test process).</summary>
internal static class Wpf
{
    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static Application? _app;

    public static T Run<T>(Func<T> work)
    {
        lock (Gate)
        {
            if (_dispatcher is null)
            {
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    // A plain Application with the app's resources – the real App would start everything in OnStartup.
                    var app = new Application();
                    foreach (var dictionary in new[] { "Themes/BarTheme.xaml", "Bar/Views/ElementViews.xaml" })
                    {
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/ueBar;component/{dictionary}") });
                    }

                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing a test window must not end the app
                    _app = app;
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                { IsBackground = true, Name = "WPF test" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait();
            }
        }

        Exception? error = null;
        T result = default!;
        _dispatcher!.Invoke(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    throw new InvalidOperationException($"No Application (created {_app is not null}, dispatcher shut down {_dispatcher!.HasShutdownStarted})");
                }

                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }
}
