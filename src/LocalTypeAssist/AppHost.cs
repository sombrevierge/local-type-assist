using System.Diagnostics;
using System.Drawing;
using System.Windows;
using LocalTypeAssist.Models;
using LocalTypeAssist.Services;
using Forms = System.Windows.Forms;

namespace LocalTypeAssist;

public sealed class AppHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly LocalLearningStore _store;
    private readonly MorphologyService _morphology;
    private readonly SuggestionWindow _suggestionWindow;
    private readonly PersonalMlScorer _mlScorer;
    private readonly SuggestionEngine _engine;
    private readonly TypingController _typingController;
    private readonly KeyboardHook _keyboardHook;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly MainWindow _mainWindow;
    private LearningLibraryWindow? _learningLibraryWindow;
    private bool _disposed;
    private bool _standaloneShiftCandidate;

    public AppHost()
    {
        _settings = AppSettings.Load();
        _store = new LocalLearningStore();
        _store.SwitchProfile(_settings.ActiveProfile);
        _morphology = new MorphologyService();
        _suggestionWindow = new SuggestionWindow();
        _mlScorer = new PersonalMlScorer(_store.ProfileName);
        _engine = new SuggestionEngine(_store, _morphology, _settings, _mlScorer);
        _typingController = new TypingController(_settings, _store, _engine, _morphology, _suggestionWindow);
        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyDown += HandleGlobalKey;
        _keyboardHook.KeyUp += HandleGlobalKeyUp;

        _mainWindow = new MainWindow(this);
        _store.Changed += (_, _) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _mainWindow.RefreshState();
            if (_learningLibraryWindow?.IsVisible == true)
            {
                _learningLibraryWindow.RequestRefresh();
            }
        });
        _morphology.StateChanged += (_, _) =>
        {
            // Words learned while the initial morphology index is still loading must
            // also be expanded into their local families as soon as the module is ready.
            if (_morphology.IsReady)
            {
                _morphology.EnsureWordsIndexed(_store.GetKnownWordsSnapshot());
            }

            Application.Current.Dispatcher.InvokeAsync(_mainWindow.RefreshState);
        };
        _morphology.Initialize(_store.GetSeedWordsSnapshot().Concat(_store.GetKnownWordsSnapshot()));

        _trayIcon = CreateTrayIcon();
    }

    public AppSettings Settings => _settings;
    public LocalLearningStore Store => _store;
    public MorphologyService Morphology => _morphology;
    public PersonalMlScorer MlScorer => _mlScorer;

    public void Start(bool background)
    {
        _keyboardHook.Start();
        _trayIcon.Visible = true;
        RefreshUi();

        if (!background)
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
        _mainWindow.RefreshState();
    }

    public void HideMainWindow() => _mainWindow.Hide();

    public void ToggleEnabled() => SetEnabled(!_settings.Enabled);

    public void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        _settings.Save();
        if (!enabled)
        {
            _typingController.Reset();
        }

        RefreshUi();
    }

    public void SaveSuggestionSettings(
        int minPrefix,
        int maxSuggestions,
        bool learnTypedWords,
        bool morphologyEnabled,
        bool semanticSuggestionsEnabled,
        bool personalMlEnabled,
        bool autoCompleteShortWords,
        bool shiftCancelsCompletion,
        string completionMode,
        string genderPreference,
        int delayMs,
        int autoCompleteMinPrefix)
    {
        _settings.MinPrefixLength = Math.Clamp(minPrefix, 1, 6);
        _settings.MaxSuggestions = Math.Clamp(maxSuggestions, 1, 5);
        _settings.LearnTypedWords = learnTypedWords;
        _settings.MorphologyEnabled = morphologyEnabled;
        _settings.SemanticSuggestionsEnabled = semanticSuggestionsEnabled;
        _settings.PersonalMlEnabled = personalMlEnabled;
        _settings.AutoCompleteShortWords = autoCompleteShortWords;
        _settings.ShiftCancelsCompletion = shiftCancelsCompletion;
        _settings.CompletionMode = completionMode;
        _settings.GenderPreference = genderPreference;
        _settings.AutoCompleteDelayMs = Math.Clamp(delayMs, 250, 2000);
        _settings.AutoCompleteMinPrefix = Math.Clamp(autoCompleteMinPrefix, 1, 12);
        _settings.Save();
        _typingController.Reset();
        RefreshUi();
    }

    public void SetAutoStart(bool enabled)
    {
        AutoStartService.SetEnabled(enabled);
        _settings.AutoStart = enabled;
        _settings.Save();
        RefreshUi();
    }

    public void SwitchProfile(string profileName)
    {
        var safeName = LocalLearningStore.SanitizeProfileName(profileName);
        _typingController.Reset();
        _store.SwitchProfile(safeName);
        _mlScorer.SwitchProfile(safeName);
        _settings.ActiveProfile = safeName;
        _settings.Save();
        _morphology.EnsureWordsIndexed(_store.GetKnownWordsSnapshot());
        _learningLibraryWindow?.RequestRefresh();
        RefreshUi();
    }

    public (int Words, int Tokens) ImportCorpus(IEnumerable<string> paths)
    {
        var result = _store.ImportCorpus(paths);
        _store.SaveNow();
        _morphology.EnsureWordsIndexed(_store.GetKnownWordsSnapshot());
        return result;
    }

    public void ResetProfile()
    {
        _typingController.Reset();
        _store.ResetActiveProfile();
        _mlScorer.Reload();
        _learningLibraryWindow?.RequestRefresh();
        RefreshUi();
    }

    public void ShowLearningLibrary()
    {
        try
        {
            if (_learningLibraryWindow is null)
            {
                _learningLibraryWindow = new LearningLibraryWindow(this, _mainWindow.IsVisible ? _mainWindow : null);
                _learningLibraryWindow.Closed += (_, _) => _learningLibraryWindow = null;
            }

            if (!_learningLibraryWindow.IsVisible)
            {
                _learningLibraryWindow.Show();
            }

            _learningLibraryWindow.Activate();
            _learningLibraryWindow.RefreshData();
        }
        catch (Exception exception)
        {
            AppLog.Error("Opening learning library failed.", exception);
            try
            {
                _learningLibraryWindow?.Close();
            }
            catch
            {
                // Ignore secondary cleanup failures.
            }

            _learningLibraryWindow = null;
            MessageBox.Show(
                $"Не удалось открыть библиотеку обучения.\n\n{exception.Message}\n\nЛог: {AppLog.LogPath}",
                "Local Type Assist",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public async Task<string> TrainPersonalModelAsync()
    {
        _store.SaveNow();
        var result = await MlTrainingService.TrainAsync(_store.ProfileName);
        _mlScorer.Reload();
        RefreshUi();
        return result;
    }

    public void ReloadPersonalModel(bool invalidate = false)
    {
        if (invalidate)
        {
            var path = AppSettings.GetMlModelPath(_store.ProfileName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stale optional ML file is less important than keeping the UI responsive.
            }
        }

        _mlScorer.Reload();
        RefreshUi();
    }

    public void RetryMorphology()
    {
        _typingController.Reset();
        _morphology.Retry(_store.GetSeedWordsSnapshot().Concat(_store.GetKnownWordsSnapshot()));
        RefreshUi();
    }

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppSettings.DataRoot,
            UseShellExecute = true
        });
    }

    public void PrepareForExit() => _mainWindow.PrepareForExit();

    public void ExitApplication()
    {
        PrepareForExit();
        Application.Current.Shutdown();
    }

    private bool HandleGlobalKey(GlobalKeyEvent key)
    {
        if (IsShiftKey(key.VirtualKey))
        {
            // Shift only cancels a completion when it is pressed and released by itself.
            // Using Shift together with a letter, number or punctuation is normal typing.
            _standaloneShiftCandidate = _settings.ShiftCancelsCompletion && _settings.CompletionMode == "Immediate";
            return false;
        }

        if (_standaloneShiftCandidate)
        {
            _standaloneShiftCandidate = false;
        }

        if (key.Control && key.Alt && key.VirtualKey == NativeMethods.VkSpace)
        {
            ToggleEnabled();
            return true;
        }

        if (_typingController.ShouldAutoComplete(key) && _typingController.AutoCompleteWithDelimiter(key))
        {
            return true;
        }

        if (_typingController.IsSuggestionCommand(key))
        {
            return _typingController.HandleSuggestionCommand(key);
        }

        return _typingController.Handle(key);
    }

    private bool HandleGlobalKeyUp(GlobalKeyEvent key)
    {
        if (!IsShiftKey(key.VirtualKey))
        {
            return false;
        }

        var shouldCancel = _standaloneShiftCandidate &&
                           _settings.ShiftCancelsCompletion &&
                           _settings.CompletionMode == "Immediate";
        _standaloneShiftCandidate = false;
        if (shouldCancel)
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                _typingController.CancelCurrentCompletionAndResume();
            }
            else
            {
                dispatcher.Invoke(_typingController.CancelCurrentCompletionAndResume);
            }
        }

        return false;
    }

    private static bool IsShiftKey(int virtualKey) => virtualKey is
        NativeMethods.VkShift or NativeMethods.VkLShift or NativeMethods.VkRShift;

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var icon = SystemIcons.Application;
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                icon = Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
            }
        }
        catch
        {
            // Keep the built-in fallback icon.
        }

        var tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Local Type Assist",
            Visible = false
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ShowMainWindow));
        menu.Items.Add("Включить / пауза", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ToggleEnabled));
        menu.Items.Add("Библиотека обучения", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ShowLearningLibrary));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Application.Current.Dispatcher.InvokeAsync(ExitApplication));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Application.Current.Dispatcher.InvokeAsync(ShowMainWindow);
        return tray;
    }

    private void RefreshUi()
    {
        _trayIcon.Text = !_settings.Enabled
            ? "Local Type Assist — пауза"
            : _settings.CompletionMode == "Training"
                ? "Local Type Assist — обучение"
                : "Local Type Assist — активно";
        _mainWindow.RefreshState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        try { _learningLibraryWindow?.Close(); } catch { }
        _suggestionWindow.HideSuggestions();
        _typingController.Dispose();
        _keyboardHook.Dispose();
        _morphology.Dispose();
        _store.Dispose();
        _settings.Save();
        GC.SuppressFinalize(this);
    }
}
