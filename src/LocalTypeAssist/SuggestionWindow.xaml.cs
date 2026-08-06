using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LocalTypeAssist.Models;
using LocalTypeAssist.Services;

namespace LocalTypeAssist;

public partial class SuggestionWindow : Window
{
    private static readonly FontFamily AppFont = new("Montserrat, Segoe UI");
    private readonly List<SuggestionItem> _suggestions = new();
    private int _selectedIndex;
    private string _prefix = string.Empty;

    public event Action<string>? SuggestionClicked;

    public SuggestionWindow()
    {
        InitializeComponent();
    }

    public bool HasSuggestions => _suggestions.Count > 0 && IsVisible;

    public SuggestionItem? SelectedItem =>
        _suggestions.Count == 0 ? null : _suggestions[Math.Clamp(_selectedIndex, 0, _suggestions.Count - 1)];

    public string? SelectedSuggestion => SelectedItem?.Text;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var current = unchecked((ulong)NativeMethods
            .GetWindowLongPtr(handle, NativeMethods.GwlExstyle)
            .ToInt64());
        var flags = (ulong)(NativeMethods.WsExToolwindow | NativeMethods.WsExNoactivate);
        var updated = unchecked((long)(current | flags));
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExstyle, new IntPtr(updated));
    }

    public void SetCompletionMode(string mode)
    {
        switch (mode)
        {
            case "Space":
                FooterHintText.Text = "Пробел дополнит выбранный вариант";
                FooterKeyText.Text = "SHIFT + SPACE";
                FooterActionText.Text = "оставить как есть";
                break;
            case "Soft":
                FooterHintText.Text = "Вариант вставляется только вручную";
                FooterKeyText.Text = "TAB";
                FooterActionText.Text = "вставить";
                break;
            default:
                FooterHintText.Text = "Выбранный вариант вставится после паузы";
                FooterKeyText.Text = "SHIFT отдельно";
                FooterActionText.Text = "отменить";
                break;
        }
    }

    public void ShowSuggestions(
        IReadOnlyList<SuggestionItem> suggestions,
        string prefix,
        CaretAnchor anchor)
    {
        _suggestions.Clear();
        _suggestions.AddRange(suggestions.Take(5));
        _prefix = prefix;
        _selectedIndex = 0;

        if (_suggestions.Count == 0)
        {
            HideSuggestions();
            return;
        }

        ContextHint.Text = string.IsNullOrEmpty(prefix)
            ? "следующее слово по контексту"
            : $"продолжение «{prefix}»";
        Render();

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pointDip = fromDevice.Transform(new Point(anchor.X, anchor.Y));
        var heightDip = Math.Abs(fromDevice.Transform(new Point(0, anchor.Height)).Y);
        var workArea = SystemParameters.WorkArea;

        Left = Math.Clamp(
            pointDip.X - 18,
            workArea.Left + 8,
            workArea.Right - ActualWidth - 8);

        var below = pointDip.Y + Math.Max(18, heightDip) + 10;
        var above = pointDip.Y - ActualHeight - 12;
        Top = below + ActualHeight <= workArea.Bottom - 8
            ? below
            : Math.Max(workArea.Top + 8, above);
    }

    public void SelectIndex(int index)
    {
        if (index < 0 || index >= _suggestions.Count)
        {
            return;
        }

        _selectedIndex = index;
        Render();
    }

    public void MoveSelection(int delta)
    {
        if (_suggestions.Count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex + delta + _suggestions.Count) % _suggestions.Count;
        Render();
    }

    public void HideSuggestions()
    {
        _suggestions.Clear();
        _prefix = string.Empty;
        SuggestionPanel.Children.Clear();
        if (IsVisible)
        {
            Hide();
        }
    }

    private void Render()
    {
        SuggestionPanel.Children.Clear();

        for (var i = 0; i < _suggestions.Count; i++)
        {
            var item = _suggestions[i];
            var selected = i == _selectedIndex;

            var row = new Border
            {
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(246, 246, 246))
                    : new SolidColorBrush(Color.FromRgb(31, 31, 31)),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(246, 246, 246))
                    : new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(15, 11, 12, 11),
                Margin = new Thickness(0, i == 0 ? 0 : 5, 0, 0),
                Cursor = Cursors.Hand,
                Tag = i
            };

            if (selected)
            {
                row.Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 0,
                    Opacity = 0.24,
                    Color = Colors.White
                };
            }

            row.MouseLeftButtonDown += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                var index = (int)row.Tag;
                if (index >= 0 && index < _suggestions.Count)
                {
                    SuggestionClicked?.Invoke(_suggestions[index].Text);
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var word = new TextBlock
            {
                FontFamily = AppFont,
                FontSize = 15.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var prefixLength = item.Text.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                ? Math.Min(_prefix.Length, item.Text.Length)
                : 0;

            if (prefixLength > 0)
            {
                word.Inlines.Add(new Run(item.Text[..prefixLength])
                {
                    Foreground = selected
                        ? new SolidColorBrush(Color.FromRgb(72, 72, 72))
                        : new SolidColorBrush(Color.FromRgb(126, 126, 126)),
                    FontWeight = FontWeights.Medium
                });
            }

            word.Inlines.Add(new Run(item.Text[prefixLength..])
            {
                Foreground = selected ? Brushes.Black : Brushes.White,
                FontWeight = FontWeights.SemiBold
            });
            textStack.Children.Add(word);

            textStack.Children.Add(new TextBlock
            {
                Text = GetSourceLabel(item),
                Foreground = selected
                    ? new SolidColorBrush(Color.FromRgb(74, 74, 74))
                    : new SolidColorBrush(Color.FromRgb(104, 104, 104)),
                FontFamily = AppFont,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            });

            grid.Children.Add(textStack);

            var badge = new Border
            {
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(224, 224, 224))
                    : new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(214, 214, 214))
                    : new SolidColorBrush(Color.FromRgb(58, 58, 58)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = selected ? "TAB" : $"{i + 1}",
                    Foreground = selected
                        ? new SolidColorBrush(Color.FromRgb(42, 42, 42))
                        : new SolidColorBrush(Color.FromRgb(145, 145, 145)),
                    FontFamily = AppFont,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold
                }
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);

            row.Child = grid;
            SuggestionPanel.Children.Add(row);
        }
    }

    private static string GetSourceLabel(SuggestionItem item)
    {
        if (item.ContextCount > 0)
        {
            return "ПО КОНТЕКСТУ";
        }

        if (item.PrefixChoiceCount > 0 || item.AcceptedCount > 0)
        {
            return "ИЗ ВАШЕЙ МОДЕЛИ";
        }

        if (item.MorphologyMatched)
        {
            return "ФОРМА СЛОВА";
        }

        return "ПРОДОЛЖЕНИЕ";
    }
}
