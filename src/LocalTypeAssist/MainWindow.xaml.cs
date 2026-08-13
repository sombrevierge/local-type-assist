using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalTypeAssist;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private bool _initializing;
    private bool _allowClose;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            Hide();
        };
        Loaded += (_, _) => RefreshState();
    }

    public void PrepareForExit() => _allowClose = true;

    public void RefreshState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshState);
            return;
        }

        _initializing = true;
        try
        {
            var settings = _host.Settings;
            var trainingMode = settings.CompletionMode == "Training";
            StatusLabel.Text = !settings.Enabled
                ? "●  ПАУЗА"
                : trainingMode
                    ? "⚡  ОБУЧЕНИЕ"
                    : "⚡  АКТИВНО";
            StatusLabel.Foreground = new SolidColorBrush(settings.Enabled
                ? Color.FromRgb(105, 105, 105)
                : Color.FromRgb(125, 125, 125));
            SummaryLabel.Text = !settings.Enabled
                ? "Подсказки\nна паузе"
                : trainingMode
                    ? "Локальное\nобучение"
                    : "Локальное\nавтодополнение";
            ToggleButton.Content = settings.Enabled
                ? "Поставить на паузу"
                : trainingMode ? "Включить обучение" : "Включить подсказки";

            ProfileTextBox.Text = _host.Store.ProfileName;
            ProfileHint.Text = $"Личная база: {_host.Store.LearnedWordCount:N0} • словарь: {_host.Store.SeedWordCount:N0} • контекстов: {_host.Store.LearnedContextCount:N0} • событий v7: {_host.Store.LearningEventCount:N0}";
            ProgressText.Text = $"{_host.Store.TotalObservations:N0} наблюдений";

            SelectComboValue(MinPrefixCombo, settings.MinPrefixLength.ToString());
            SelectComboValue(MaxSuggestionsCombo, settings.MaxSuggestions.ToString());
            SelectComboByTag(CompletionModeCombo, settings.CompletionMode);
            SelectComboByTag(GenderCombo, settings.GenderPreference);
            SelectComboByTag(AutoPrefixCombo, settings.AutoCompleteMinPrefix.ToString());
            DelaySlider.Value = settings.AutoCompleteDelayMs;
            DelayValueText.Text = $"{settings.AutoCompleteDelayMs} мс";
            LearnTypedCheckBox.IsChecked = settings.LearnTypedWords;
            MorphologyCheckBox.IsChecked = settings.MorphologyEnabled;
            SemanticCheckBox.IsChecked = settings.SemanticSuggestionsEnabled;
            PersonalMlCheckBox.IsChecked = settings.PersonalMlEnabled;
            ShortWordsCheckBox.IsChecked = settings.AutoCompleteShortWords;
            ShiftCancelCheckBox.IsChecked = settings.ShiftCancelsCompletion;
            AutoStartCheckBox.IsChecked = settings.AutoStart;

            BehaviorHintText.Text = settings.CompletionMode switch
            {
                "Space" => "Пробел дополняет выбранный вариант. Shift+Space оставляет слово ровно таким, как вы его напечатали; отменять уже вставленное слово не нужно.",
                "Training" => "Никаких подсказок и вставок: программа только изучает ваши слова, формы и последовательности до пяти слов.",
                "Soft" => "Показывается список; вставка выполняется только по Tab, стрелке вправо или клику.",
                _ => "Лучший вариант вставляется после паузы. Отдельное нажатие Shift отменяет последнюю автоматическую вставку."
            };
            var delayedMode = settings.CompletionMode == "Immediate";
            var suggestionsEnabled = settings.CompletionMode != "Training";
            AutoPrefixGrid.IsEnabled = delayedMode;
            DelayGrid.IsEnabled = delayedMode;
            ShiftCancelCheckBox.IsEnabled = delayedMode;
            ShortWordsCheckBox.IsEnabled = delayedMode || settings.CompletionMode == "Space";
            MinPrefixCombo.IsEnabled = suggestionsEnabled;
            MaxSuggestionsCombo.IsEnabled = suggestionsEnabled;
            SemanticCheckBox.IsEnabled = suggestionsEnabled;
            LearnTypedCheckBox.IsChecked = trainingMode ? true : settings.LearnTypedWords;
            LearnTypedCheckBox.IsEnabled = !trainingMode;
            TrainingModeCard.Visibility = trainingMode ? Visibility.Visible : Visibility.Collapsed;

            MorphologyStatusText.Text = _host.Morphology.Failed
                ? "Морфология недоступна: " + _host.Morphology.LastError
                : _host.Morphology.IsReady
                    ? $"Морфология готова: {_host.Morphology.IndexedFormCount:N0} словоформ в локальном индексе."
                    : "Морфология загружается локально… Первичная индексация может занять несколько секунд.";

            var mlStatus = _host.MlScorer.GetStatus();
            MlStatusText.Text = mlStatus.Available
                ? $"{mlStatus.Message} Последнее обучение: {mlStatus.TrainedAtUtc?.ToLocalTime():dd.MM HH:mm}."
                : "ML пока не обучена. Откройте библиотеку после накопления исправлений и запустите локальное обучение.";

            Dispatcher.InvokeAsync(() =>
            {
                var container = ProgressGlow.Parent as FrameworkElement;
                var available = container?.ActualWidth ?? 360;
                var progress = Math.Clamp(Math.Log10(_host.Store.TotalObservations + 1) / 4.0, 0.08, 1.0);
                ProgressGlow.Width = Math.Max(56, available * progress);
            });
        }
        finally
        {
            _initializing = false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => _host.HideMainWindow();
    private void Hide_Click(object sender, RoutedEventArgs e) => _host.HideMainWindow();
    private void Toggle_Click(object sender, RoutedEventArgs e) => _host.ToggleEnabled();

    private void SwitchProfile_Click(object sender, RoutedEventArgs e)
    {
        _host.SwitchProfile(ProfileTextBox.Text);
        FooterStatus.Text = $"Открыт профиль «{_host.Store.ProfileName}».";
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || !IsLoaded)
        {
            return;
        }

        SaveSettingsFromControls();
    }

    private void DelaySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || !IsLoaded)
        {
            return;
        }

        DelayValueText.Text = $"{(int)DelaySlider.Value} мс";
        SaveSettingsFromControls();
    }

    private void SaveSettingsFromControls()
    {
        var minPrefix = GetComboValue(MinPrefixCombo, 1);
        var maxSuggestions = GetComboValue(MaxSuggestionsCombo, 5);
        var completionMode = GetComboTag(CompletionModeCombo, "Immediate");
        var gender = GetComboTag(GenderCombo, "Auto");
        var autoPrefix = int.TryParse(GetComboTag(AutoPrefixCombo, "3"), out var parsed) ? parsed : 3;
        var delay = (int)Math.Round(DelaySlider.Value);

        _host.SaveSuggestionSettings(
            minPrefix,
            maxSuggestions,
            completionMode == "Training" || LearnTypedCheckBox.IsChecked == true,
            MorphologyCheckBox.IsChecked == true,
            SemanticCheckBox.IsChecked == true,
            PersonalMlCheckBox.IsChecked == true,
            ShortWordsCheckBox.IsChecked == true,
            ShiftCancelCheckBox.IsChecked == true,
            completionMode,
            gender,
            delay,
            autoPrefix);
        FooterStatus.Text = "Настройки сохранены.";
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || !IsLoaded)
        {
            return;
        }

        try
        {
            _host.SetAutoStart(AutoStartCheckBox.IsChecked == true);
            FooterStatus.Text = AutoStartCheckBox.IsChecked == true
                ? "Автозапуск включён."
                : "Автозапуск выключен.";
        }
        catch (Exception exception)
        {
            _initializing = true;
            AutoStartCheckBox.IsChecked = _host.Settings.AutoStart;
            _initializing = false;
            FooterStatus.Text = exception.Message;
        }
    }

    private async void ImportCorpus_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Текстовые файлы|*.txt;*.md;*.csv;*.log;*.json|Все файлы|*.*",
            Title = "Выберите локальные файлы для обучения"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        FooterStatus.Text = "Строю локальную модель слов, префиксов и контекстов до пяти слов…";
        try
        {
            var fileNames = dialog.FileNames.ToArray();
            var result = await Task.Run(() => _host.ImportCorpus(fileNames));
            RefreshState();
            FooterStatus.Text = result.Tokens == 0
                ? "Не найдено подходящих слов или файл слишком большой."
                : $"Готово: {result.Tokens:N0} словоупотреблений, {result.Words:N0} уникальных слов.";
        }
        catch (Exception exception)
        {
            FooterStatus.Text = "Ошибка импорта: " + exception.Message;
        }
    }

    private void OpenLearningLibrary_Click(object sender, RoutedEventArgs e)
    {
        _host.ShowLearningLibrary();
        FooterStatus.Text = "Открыта библиотека обучения.";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        _host.OpenDataFolder();
        FooterStatus.Text = "Открыта локальная папка профилей.";
    }

    private void RetryMorphology_Click(object sender, RoutedEventArgs e)
    {
        _host.RetryMorphology();
        FooterStatus.Text = "Морфологический модуль перезапускается локально…";
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"Удалить всё обучение профиля «{_host.Store.ProfileName}»? Стартовый словарь останется.",
            "Сброс профиля",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _host.ResetProfile();
        FooterStatus.Text = "Профиль очищен.";
    }

    private static int GetComboValue(ComboBox comboBox, int fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item &&
               int.TryParse(item.Content?.ToString(), out var value)
            ? value
            : fallback;
    }

    private static string GetComboTag(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;

    private static void SelectComboValue(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboByTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }
}
