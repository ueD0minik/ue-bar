// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UeBar.App.Localization;
using UeBar.Core.Modules;
using UeBar.Sdk;
using static UeBar.App.Settings.SettingsUi;

namespace UeBar.App.ContentActions;

/// <summary>
/// The answer window of content actions (P3-1): drawn by the core in the bar design, so plugins need no WPF. Shows the
/// content in short, the conversation (Markdown, selectable), the answer as it comes in, a switcher (e.g. the model) and
/// a field for follow-ups; the answer can be copied, pinned or pasted into the window that was in front. Everything
/// lives in memory: closing, "New chat" and the idle time of the provider forget the conversation. Hidden from screen
/// captures like every window of the app (P2-7).
/// </summary>
internal sealed class ResponseWindow : Window
{
    private readonly ContentActionSession _session;
    private readonly ContentActionUi _ui;
    private readonly StackPanel _turns = new();
    private readonly ScrollViewer _scroller;
    private readonly RichTextBox _answer = MarkdownView.Create();
    private readonly Border _answerCard;
    private readonly TextBlock _question;
    private readonly TextBlock _status;
    private readonly TextBlock _error;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly ComboBox _options;
    private readonly StackPanel _answerButtons;
    private readonly Button _stop;
    private readonly Button _paste;
    private readonly FrameworkElement _preview;
    private readonly DispatcherTimer _idle;
    private bool _renderPending;
    private bool _updatingOptions;
    private int _shownTurns;
    private string _shownAnswer = "";

    public ResponseWindow(ContentActionSession session, ContentActionUi ui)
    {
        _session = session;
        _ui = ui;
        Title = $"{session.Action.Action.Label} · {session.Action.ModuleName}";
        Width = 600;
        Height = 520;
        MinWidth = 380;
        MinHeight = 260;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        Topmost = true;
        Icon = UeBar.App.Settings.AppIcon.ImageSource;
        FontFamily = (FontFamily)Application.Current.FindResource("BarFont");
        Foreground = Res("TextBrush");
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // preview
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // conversation
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // input

        // Header: icon, title, switcher, new chat, close – drag to move.
        var header = new Grid { Margin = new Thickness(18, 12, 10, 6), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not ComboBox)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Style = StyleOf("IconText"),
            Text = Glyph(session.Action.Action.Icon) ?? "",
            FontSize = 16,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("AccentBrush"),
        });
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        _options = new ComboBox
        {
            Style = StyleOf("DarkComboBox"),
            MinWidth = 130,
            Height = 30,
            Margin = new Thickness(8, 0, 4, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = Loc.Get("Response_OptionTip"),
        };
        _options.SelectionChanged += (_, _) => OnOptionChosen();
        Grid.SetColumn(_options, 2);
        header.Children.Add(_options);

        var newChat = IconButton("", Loc.Get("Response_NewChat"), NewChat);
        Grid.SetColumn(newChat, 3);
        header.Children.Add(newChat);
        var close = IconButton("", Loc.Get("Response_Close"), Close);
        Grid.SetColumn(close, 4);
        header.Children.Add(close);
        root.Children.Add(header);

        _preview = Preview(session.Content);
        Grid.SetRow(_preview, 1);
        root.Children.Add(_preview);

        // Conversation: earlier turns, then the current question and answer.
        var conversation = new StackPanel { Margin = new Thickness(18, 4, 18, 8) };
        conversation.Children.Add(_turns);
        _question = QuestionBubble("");
        conversation.Children.Add(_question);
        _status = new TextBlock { Style = StyleOf("SubText"), Margin = new Thickness(2, 6, 0, 4), Visibility = Visibility.Collapsed };
        _answerCard = new Border { Child = _answer, Margin = new Thickness(0, 4, 0, 0) };
        conversation.Children.Add(_answerCard);
        conversation.Children.Add(_status);
        _error = new TextBlock { Foreground = Res("AlertBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 4), Visibility = Visibility.Collapsed };
        conversation.Children.Add(_error);

        _answerButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 2) };
        var replaces = session.Action.Action.ReplacesContent;
        _paste = Small(Loc.Get("Response_Paste"), "", () => _ = PasteAsync(), accent: replaces);
        _paste.Visibility = session.Start.PasteTarget is null ? Visibility.Collapsed : Visibility.Visible;
        _paste.ToolTip = Loc.Get("Response_PasteTip");
        var copy = Small(Loc.Get("Response_Copy"), "", () => _ = CopyAsync(), accent: replaces && session.Start.PasteTarget is null);
        copy.ToolTip = Loc.Get("Response_CopyTip");
        var pin = Small(Loc.Get("Response_Pin"), "", Pin);
        _stop = Small(Loc.Get("Response_Stop"), "", () => _session.Cancel());
        if (replaces)
        {
            _answerButtons.Children.Add(_paste);
            _answerButtons.Children.Add(copy);
        }
        else
        {
            _answerButtons.Children.Add(copy);
            _answerButtons.Children.Add(_paste);
        }

        _answerButtons.Children.Add(pin);
        _answerButtons.Children.Add(_stop);
        conversation.Children.Add(_answerButtons);

        _scroller = new ScrollViewer
        {
            Content = conversation,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        _scroller.Resources[typeof(ScrollBar)] = StyleOf("DarkScrollBar");
        Grid.SetRow(_scroller, 2);
        root.Children.Add(_scroller);

        // Follow-up field: Enter sends, Shift+Enter is a new line.
        var inputGrid = new Grid { Margin = new Thickness(14, 4, 14, 14) };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = new TextBox
        {
            Style = StyleOf("EditTextBox"),
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            MinHeight = 38,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 9, 12, 9),
            FontSize = 14,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_input, "ResponseInput");
        _input.PreviewKeyDown += OnInputKey;
        _input.TextChanged += (_, _) =>
        {
            _placeholder!.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _session.Touch();
        };
        _placeholder = new TextBlock
        {
            Text = Loc.Get(session.Action.Action.AsksQuestion && !session.HasStarted ? "Response_AskPlaceholder" : "Response_FollowUpPlaceholder"),
            Style = StyleOf("SubText"),
            IsHitTestVisible = false,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(_placeholder);
        var send = IconButton("", Loc.Get("Response_Send"), Send);
        send.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(send, 1);
        inputGrid.Children.Add(send);
        Grid.SetRow(inputGrid, 3);
        root.Children.Add(inputGrid);

        var panel = (Color)Application.Current.FindResource("PanelColor");
        panel.A = 0xFA;
        Content = new Border
        {
            Background = new SolidColorBrush(panel),
            BorderBrush = Res("GripBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Child = root,
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_session.IsRunning)
                {
                    _session.Cancel();
                }
                else
                {
                    Close();
                }
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                _ = CopyAsync();
            }
        };

        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _idle.Tick += (_, _) => CheckIdle();
        Loaded += (_, _) =>
        {
            _idle.Start();
            Activate();
            _input.Focus();
        };
        Closed += (_, _) =>
        {
            _idle.Stop();
            _session.Changed -= OnSessionChanged;
            _session.Dispose();
            _answer.Document = new System.Windows.Documents.FlowDocument();
            _turns.Children.Clear();
            _ui.Closed(this);
        };

        _session.Changed += OnSessionChanged;
        Update();
    }

    public ContentActionSession Session => _session;

    /// <summary>Puts the window on the monitor under the mouse, a bit above the middle.</summary>
    public void PlaceNearMouse()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        Left = (area.Left / scale) + ((area.Width / scale) - Width) / 2;
        Top = (area.Top / scale) + Math.Max(24, ((area.Height / scale) - Height) / 3);
    }

    // ------------------------------------------------------------------ updates

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;

        // Coalesces the many small pieces of a streamed answer into one redraw per frame – no timer.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _renderPending = false;
            if (IsLoaded || IsVisible)
            {
                Update();
            }
        });
    }

    private void Update()
    {
        var turns = _session.Turns;
        if (turns.Count != _shownTurns)
        {
            _turns.Children.Clear();
            foreach (var turn in turns)
            {
                if (turn.Question is { Length: > 0 } q)
                {
                    _turns.Children.Add(QuestionBubble(q));
                }

                var view = MarkdownView.Create();
                MarkdownView.Render(view, turn.Answer);
                _turns.Children.Add(new Border { Child = view, Margin = new Thickness(0, 4, 0, 12) });
            }

            _shownTurns = turns.Count;
        }

        var question = _session.CurrentQuestion;
        _question.Text = question ?? "";
        _question.Visibility = string.IsNullOrEmpty(question) ? Visibility.Collapsed : Visibility.Visible;

        var answer = _session.CurrentAnswer;
        if (!string.Equals(answer, _shownAnswer, StringComparison.Ordinal))
        {
            MarkdownView.Render(_answer, answer);
            _shownAnswer = answer;
        }

        _answerCard.Visibility = answer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var status = _session.IsRunning ? _session.Status ?? Loc.Get("Response_Working") : null;
        _status.Text = status ?? "";
        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;

        _error.Text = _session.Error is { } error ? ErrorText(error) : "";
        _error.Visibility = _session.Error is null ? Visibility.Collapsed : Visibility.Visible;

        _answerButtons.Visibility = _session.HasStarted && (answer.Length > 0 || _session.IsRunning) ? Visibility.Visible : Visibility.Collapsed;
        _stop.Visibility = _session.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _answerButtons.Children.OfType<Button>().Where(b => b != _stop))
        {
            button.IsEnabled = !_session.IsRunning && answer.Length > 0;
        }

        _preview.Visibility = _session.Content.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        UpdateOptions();
        if (_session.IsRunning)
        {
            _scroller.ScrollToEnd();
        }
    }

    private void UpdateOptions()
    {
        var options = _session.Options;
        _updatingOptions = true;
        try
        {
            var ids = _options.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
            if (!ids.SequenceEqual(options.Select(o => o.Id)))
            {
                _options.Items.Clear();
                foreach (var option in options)
                {
                    _options.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id, Style = StyleOf("DarkComboBoxItem") });
                }
            }

            _options.SelectedItem = _options.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _session.SelectedOption);
            _options.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _options.IsEnabled = !_session.IsRunning;
        }
        finally
        {
            _updatingOptions = false;
        }
    }

    private static string ErrorText(string error) => error switch
    {
        "timeout" => Loc.Get("Response_Timeout"),
        "module" => Loc.Get("Response_ModuleGone"),
        "error" => Loc.Get("Response_Error"),
        _ => error,
    };

    // ------------------------------------------------------------------ actions

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var caret = _input.CaretIndex;
            _input.Text = _input.Text.Insert(caret, Environment.NewLine);
            _input.CaretIndex = caret + Environment.NewLine.Length;
        }
    }

    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 && _session.HasStarted)
        {
            return;
        }

        _input.Clear();
        _placeholder.Text = Loc.Get("Response_FollowUpPlaceholder");
        _ = _session.SendAsync(text.Length == 0 ? null : text);
    }

    private void OnOptionChosen()
    {
        if (_updatingOptions || _options.SelectedItem is not ComboBoxItem { Tag: string id } || id == _session.SelectedOption)
        {
            return;
        }

        _ = _session.SelectOptionAsync(id);
    }

    private void NewChat()
    {
        _session.Clear();
        _shownTurns = -1;
        _input.Clear();
        _placeholder.Text = Loc.Get("Response_FollowUpPlaceholder");
        Update();
        _input.Focus();
    }

    private async Task CopyAsync()
    {
        var text = _session.ResultText;
        if (text.Length > 0)
        {
            var ok = await _ui.CopyAsync(text);
            _ui.Toast(ok ? Loc.Get("Response_Copied") : Loc.Get("Response_CopyFailed"));
        }
    }

    private async Task PasteAsync()
    {
        var text = _session.ResultText;
        if (text.Length == 0 || _session.Start.PasteTarget is not { } target)
        {
            return;
        }

        // The window goes out of the way first, so the target gets the focus back.
        Hide();
        var ok = await _ui.PasteAsync(text, target);
        if (ok)
        {
            Close();
        }
        else
        {
            Show();
            _ui.Toast(Loc.Get("Response_PasteFailed"));
        }
    }

    private void Pin()
    {
        var text = _session.ResultText;
        if (text.Length > 0)
        {
            _ui.Toast(_ui.Pin(text, _session.Action.Action.Label) ? Loc.Get("Response_Pinned") : Loc.Get("Response_PinFailed"));
        }
    }

    private void CheckIdle()
    {
        if (!_session.IsRunning && DateTimeOffset.UtcNow - _session.LastUsed >= _session.IdleTimeout)
        {
            Close();
        }
    }

    // ------------------------------------------------------------------ pieces

    private FrameworkElement Preview(ContentItem content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 18, 6) };
        if (content.ImagePng is { Length: > 0 } png && Thumbnail(png) is { } image)
        {
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new Image { Source = image, MaxHeight = 56, MaxWidth = 120, Stretch = Stretch.Uniform },
            });
        }

        var summary = content.Text is { Length: > 0 } text ? Shorten(text)
            : content.Files is { Count: > 0 } files ? string.Join(", ", files.Select(System.IO.Path.GetFileName))
            : content.ImagePng is { Length: > 0 } ? Loc.Get("Response_Image")
            : "";
        row.Children.Add(new TextBlock
        {
            Text = summary,
            Style = StyleOf("SubText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return Loc.F("Response_Quote", line.Length <= 140 ? line : line[..137] + "…");
    }

    private static BitmapImage? Thumbnail(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new System.IO.MemoryStream(png);
            image.DecodePixelHeight = 112;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static TextBlock QuestionBubble(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        Foreground = Res("SubTextBrush"),
        FontStyle = FontStyles.Italic,
        Margin = new Thickness(0, 6, 0, 2),
    };

    private static string? Glyph(string? icon) =>
        icon is { Length: > 2 } && icon.StartsWith("U+", StringComparison.OrdinalIgnoreCase) && int.TryParse(icon[2..], System.Globalization.NumberStyles.HexNumber, null, out var code)
            ? char.ConvertFromUtf32(code)
            : null;

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Style = StyleOf("BarButton"),
            Width = 34,
            Height = 34,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip,
            Background = Brushes.Transparent,
            Content = new TextBlock { Style = StyleOf("IconText"), Text = glyph, FontSize = 14 },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button Small(string text, string glyph, Action click, bool accent = false)
    {
        var button = Button(text, click, accent, glyph);
        button.Height = 30;
        button.Margin = new Thickness(0, 0, 8, 0);
        return button;
    }
}
