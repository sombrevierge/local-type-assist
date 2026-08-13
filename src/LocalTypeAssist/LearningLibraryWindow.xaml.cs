using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalTypeAssist.Models;
using LocalTypeAssist.Services;

namespace LocalTypeAssist;

public partial class LearningLibraryWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<LearningWordView> _allWords = Array.Empty<LearningWordView>();
    private bool _controlsReady;
    private bool _refreshing;
    private bool _refreshAgain;

    public LearningLibraryWindow(AppHost host, Window? owner = null)
    {
        _host = host;
        InitializeComponent();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshData();
        };

        // Never use Application.Current.MainWindow here: the application creates a
        // suggestion popup before the actual settings window, so WPF can treat an
        // unshown popup as MainWindow. Assigning an unshown Window as Owner throws.
        // AppHost passes the real settings window only when it is currently visible.
        if (owner?.IsVisible == true && !ReferenceEquals(owner, this))
        {
            try
            {
                Owner = owner;
            }
            catch (InvalidOperationException exception)
            {
                // Ownership is only a UX nicety. The library must still open even if
                // WPF rejects an owner because its presentation state changed.
                AppLog.Info($"Learning library owner was skipped: {exception.Message}");
            }
        }

        _controlsReady = true;
        Loaded += (_, _) => RefreshData();
        Closed += (_, _) => _refreshTimer.Stop();
    }

    public void RequestRefresh()
    {
        if (!_controlsReady)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RequestRefresh);
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void RefreshData()
    {
        if (!_controlsReady)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshData);
            return;
        }

        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            _allWords = _host.Store.GetLearningWordsSnapshot();
            CorrectionsGrid.ItemsSource = _host.Store.GetCorrectionsSnapshot();
            ApplyFilter();

            var likelyErrors = _allWords.Count(x => x.LikelyError);
            var needsReview = _allWords.Count(x => x.NeedsReview);
            SummaryText.Text =
                $"Профиль «{_host.Store.ProfileName}» • {_allWords.Count:N0} записей • " +
                $"{_host.Store.LearningEventCount:N0} событий v7 • {needsReview:N0} на проверку • " +
                $"{likelyErrors:N0} вероятных ошибок";
            RefreshMlStatus();
        }
        catch (Exception exception)
        {
            AppLog.Error("Learning library refresh failed.", exception);
            SummaryText.Text = "Не удалось прочитать библиотеку обучения. Ошибка записана в локальный лог.";
            MlStatusText.Text = $"Ошибка библиотеки: {exception.Message}";
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                RequestRefresh();
            }
        }
    }

    private void RefreshMlStatus()
    {
        try
        {
            var status = _host.MlScorer.GetStatus();
            MlStatusText.Text = status.Available
                ? $"{status.Message} Обучена: {status.TrainedAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}."
                : status.Message + " Для первого обучения нужны и положительные, и отрицательные сигналы.";
        }
        catch (Exception exception)
        {
            AppLog.Error("ML status refresh failed in learning library.", exception);
            MlStatusText.Text = "Статус ML временно недоступен.";
        }
    }

    private void ApplyFilter()
    {
        if (!_controlsReady)
        {
            return;
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var onlyErrors = LikelyErrorsOnlyCheckBox.IsChecked == true;
        var filtered = _allWords
            .Where(x => !onlyErrors || x.NeedsReview)
            .Where(x => query.Length == 0 || x.Word.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        WordsGrid.ItemsSource = filtered;
        FilterHintText.Text = $"Показано {filtered.Length:N0} из {_allWords.Count:N0}";
    }

    private LearningWordView[] SelectedWords() =>
        WordsGrid.SelectedItems.OfType<LearningWordView>().ToArray();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_controlsReady)
        {
            ApplyFilter();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_controlsReady)
        {
            ApplyFilter();
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedWords();
        if (selected.Length == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить обучение для {selected.Length} выбранных слов? Это удалит их персональные частоты, контексты и события.",
            "Удаление обучения",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _host.Store.DeleteLearnedWords(selected.Select(item => item.Word));
            _host.ReloadPersonalModel(invalidate: true);
            RefreshData();
        }
        catch (Exception exception)
        {
            AppLog.Error("Deleting selected learning words failed.", exception);
            MessageBox.Show(this, exception.Message, "Не удалось удалить данные", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleTrusted_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedWords();
        if (selected.Length == 0)
        {
            return;
        }

        try
        {
            var makeTrusted = !selected.All(x => x.Trusted);
            foreach (var item in selected)
            {
                _host.Store.SetWordTrusted(item.Word, makeTrusted);
            }
            RefreshData();
        }
        catch (Exception exception)
        {
            AppLog.Error("Changing trusted learning flags failed.", exception);
            MessageBox.Show(this, exception.Message, "Не удалось изменить данные", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleBlocked_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedWords();
        if (selected.Length == 0)
        {
            return;
        }

        try
        {
            var block = !selected.All(x => x.Blocked);
            foreach (var item in selected)
            {
                _host.Store.SetWordBlocked(item.Word, block);
            }
            RefreshData();
        }
        catch (Exception exception)
        {
            AppLog.Error("Changing blocked learning flags failed.", exception);
            MessageBox.Show(this, exception.Message, "Не удалось изменить данные", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PurgeLikelyErrors_Click(object sender, RoutedEventArgs e)
    {
        var count = _allWords.Count(x => x.LikelyError);
        if (count == 0)
        {
            MessageBox.Show(this, "Сейчас нет слов, которые модель уверенно считает вероятными ошибками.", "Очистка");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить {count} вероятных ошибок? Критерий осторожный: слово не из стартового словаря, не подтверждалось обучением и имеет сигнал исправления.",
            "Очистить вероятные ошибки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var removed = _host.Store.PurgeLikelyErrors();
            _host.ReloadPersonalModel(invalidate: true);
            RefreshData();
            MessageBox.Show(this, $"Удалено {removed:N0} записей.", "Очистка завершена");
        }
        catch (Exception exception)
        {
            AppLog.Error("Purging likely learning errors failed.", exception);
            MessageBox.Show(this, exception.Message, "Не удалось очистить данные", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TrainMl_Click(object sender, RoutedEventArgs e)
    {
        TrainMlButton.IsEnabled = false;
        MlStatusText.Text = "Обучаю персональный ML-reranker локально…";
        try
        {
            var result = await _host.TrainPersonalModelAsync();
            MlStatusText.Text = result;
            RefreshMlStatus();
        }
        catch (Exception exception)
        {
            AppLog.Error("Personal ML training failed from learning library.", exception);
            MlStatusText.Text = exception.Message;
        }
        finally
        {
            TrainMlButton.IsEnabled = true;
        }
    }
}
