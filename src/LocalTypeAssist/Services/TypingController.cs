using System.Windows.Threading;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class TypingController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly LocalLearningStore _store;
    private readonly SuggestionEngine _engine;
    private readonly MorphologyService _morphology;
    private readonly SuggestionWindow _window;
    private readonly System.Threading.Timer _autoCompleteTimer;
    private readonly System.Threading.Timer _editorResyncTimer;
    private readonly Queue<string> _contextWords = new();

    private string _currentWord = string.Empty;
    private PendingAcceptance? _pendingAcceptance;
    private IReadOnlyList<SuggestionItem> _currentSuggestions = Array.Empty<SuggestionItem>();
    private IntPtr _lastForeground = IntPtr.Zero;
    private string _lastFocusToken = string.Empty;
    private long _revision;
    private long _scheduledRevision;
    private string _scheduledWord = string.Empty;
    private IntPtr _scheduledForeground = IntPtr.Zero;
    private string _scheduledFocusToken = string.Empty;
    private bool _suppressSuggestionsForCurrentWord;
    private bool _showingNextWordSuggestions;
    private bool _autoCompletionSuppressedUntilBoundary;
    private bool _needsEditorResync;
    private long _resyncRevision;
    private IntPtr _resyncForeground = IntPtr.Zero;

    // In Immediate mode the suffix is physically inserted with the caret left at the end.
    // Shift or Backspace can still cancel the last automatic suffix.
    private string _selectedPrefix = string.Empty;
    private string _selectedSuggestion = string.Empty;
    private int _selectedSuffixLength;
    private bool _selectedWasReplacement;
    private readonly HashSet<string> _rejectedSuggestionsForCurrentToken = new(StringComparer.OrdinalIgnoreCase);

    public TypingController(
        AppSettings settings,
        LocalLearningStore store,
        SuggestionEngine engine,
        MorphologyService morphology,
        SuggestionWindow window)
    {
        _settings = settings;
        _store = store;
        _engine = engine;
        _morphology = morphology;
        _window = window;
        _window.SuggestionClicked += suggestion => AcceptSuggestion(suggestion, selectInsertedSuffix: false);
        _autoCompleteTimer = new System.Threading.Timer(_ => OnAutoCompleteTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _editorResyncTimer = new System.Threading.Timer(_ => OnEditorResyncTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private bool HasSelectedInsertion => _selectedSuffixLength > 0 && _selectedSuggestion.Length > 0;
    private bool IsTrainingMode => _settings.CompletionMode == "Training";
    private bool IsSpaceMode => _settings.CompletionMode == "Space";

    public bool IsSuggestionCommand(GlobalKeyEvent key)
    {
        if (IsTrainingMode)
        {
            return false;
        }

        if (HasSelectedInsertion)
        {
            return key.VirtualKey is NativeMethods.VkTab or NativeMethods.VkRight;
        }

        if (!_window.HasSuggestions)
        {
            return false;
        }

        return key.VirtualKey is NativeMethods.VkTab or NativeMethods.VkRight or
            NativeMethods.VkUp or NativeMethods.VkDown;
    }

    public bool HandleSuggestionCommand(GlobalKeyEvent key)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            Reset();
            _lastForeground = foreground;
            return false;
        }

        if (HasSelectedInsertion && key.VirtualKey is NativeMethods.VkTab or NativeMethods.VkRight)
        {
            // The caret is already at the end of the inserted word.
            ClearSelectedInsertion(keepSuggestion: true);
            _window.HideSuggestions();
            return true;
        }

        if (!_window.HasSuggestions)
        {
            return false;
        }

        if (key.VirtualKey == NativeMethods.VkDown)
        {
            CancelIdleCompletion();
            _window.MoveSelection(1);
            ScheduleIdleCompletion();
            return true;
        }

        if (key.VirtualKey == NativeMethods.VkUp)
        {
            CancelIdleCompletion();
            _window.MoveSelection(-1);
            ScheduleIdleCompletion();
            return true;
        }

        if (key.VirtualKey is NativeMethods.VkTab or NativeMethods.VkRight)
        {
            AcceptSelectedSuggestion(selectInsertedSuffix: false);
            return true;
        }

        return false;
    }

    public bool ShouldAutoComplete(GlobalKeyEvent key)
    {
        if (!_settings.Enabled || IsTrainingMode)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var focus = PrivacyGuard.GetFocusSnapshot();
        if (foreground != _lastForeground || focus.Ignore)
        {
            Reset();
            _lastForeground = foreground;
            _lastFocusToken = focus.Token;
            return false;
        }

        if (!string.Equals(focus.Token, _lastFocusToken, StringComparison.Ordinal))
        {
            _lastFocusToken = focus.Token;
            SoftResetForFocusChange();
            return false;
        }

        if (IsSpaceMode)
        {
            // Normal Space accepts the selected completion. Shift+Space always keeps
            // the text exactly as typed and inserts a literal space.
            return !key.Shift &&
                   key.VirtualKey == NativeMethods.VkSpace &&
                   key.Text.Any(char.IsWhiteSpace) &&
                   _currentWord.Length > 0 &&
                   _window.HasSuggestions;
        }

        // In delayed mode a delimiter only confirms a suffix that was already
        // inserted by the idle timer.
        if (_settings.CompletionMode != "Immediate" || !HasSelectedInsertion)
        {
            return false;
        }

        if (key.VirtualKey is NativeMethods.VkReturn or NativeMethods.VkTab || string.IsNullOrEmpty(key.Text))
        {
            return false;
        }

        return key.Text.All(ch => !IsWordCharacter(ch));
    }

    public bool AutoCompleteWithDelimiter(GlobalKeyEvent key)
    {
        if (IsSpaceMode)
        {
            var selectedItem = _window.SelectedItem;
            var suggestion = selectedItem?.Text;
            if (string.IsNullOrWhiteSpace(suggestion) ||
                suggestion.Length <= _currentWord.Length ||
                !suggestion.StartsWith(_currentWord, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var exactStat = _store.GetWordStat(_currentWord);
            var exactIsKnown = _store.GetSeedRank(_currentWord) != int.MaxValue || exactStat.TypedCount > 0;
            if (!_settings.AutoCompleteShortWords &&
                (exactIsKnown || _currentWord.Length <= 2) &&
                selectedItem is not null &&
                selectedItem.PrefixChoiceCount == 0 &&
                selectedItem.ContextCount == 0)
            {
                return false;
            }

            CancelIdleCompletion();
            var prefix = _currentWord;
            var suffix = suggestion[prefix.Length..];
            InputInjector.TypeText(suffix + key.Text);
            _currentWord = suggestion;
            _pendingAcceptance = new PendingAcceptance(suggestion, prefix, _contextWords.ToArray());
            ClearSelectedInsertion(keepSuggestion: true);
            FinalizeCurrentWord(clearContext: false);
            QueueNextWordSuggestions();
            return true;
        }

        if (!HasSelectedInsertion)
        {
            return false;
        }

        var delimiter = key.Text;
        var endSentence = delimiter.Any(ch => ch is '.' or '!' or '?' or '\n' or '\r');
        var showNext = !endSentence && delimiter.Any(char.IsWhiteSpace);

        CancelIdleCompletion();
        InputInjector.TypeText(delimiter);
        _currentWord = _selectedSuggestion;
        ClearSelectedInsertion(keepSuggestion: true);
        FinalizeCurrentWord(endSentence);
        if (showNext)
        {
            QueueNextWordSuggestions();
        }

        return true;
    }

    public bool Handle(GlobalKeyEvent key)
    {
        if (!_settings.Enabled)
        {
            Reset();
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var focus = PrivacyGuard.GetFocusSnapshot();
        if (foreground != _lastForeground)
        {
            Reset();
            _lastForeground = foreground;
            _lastFocusToken = focus.Token;
        }
        else if (!string.Equals(focus.Token, _lastFocusToken, StringComparison.Ordinal))
        {
            // Chromium can briefly replace the focused UI Automation element while the
            // same contenteditable field remains active. Do not erase cancellation state;
            // resynchronise the actual word from the editor instead.
            _lastFocusToken = focus.Token;
            SoftResetForFocusChange();
        }

        if (focus.Ignore)
        {
            Reset();
            return false;
        }

        _revision++;
        CancelIdleCompletion();

        if (key.HasCommandModifier)
        {
            Reset();
            return false;
        }

        if (HasSelectedInsertion)
        {
            if (key.VirtualKey is NativeMethods.VkBack or NativeMethods.VkEscape)
            {
                if (key.VirtualKey == NativeMethods.VkBack)
                {
                    RejectSuggestionForCurrentToken(_selectedSuggestion, _selectedPrefix);
                }
                UndoSelectedInsertion();
                _pendingAcceptance = null;
                _autoCompletionSuppressedUntilBoundary = true;
                _suppressSuggestionsForCurrentWord = key.VirtualKey == NativeMethods.VkEscape;
                ScheduleEditorResync();
                if (!_suppressSuggestionsForCurrentWord)
                {
                    QueueSuggestionRefresh();
                }
                return true;
            }

            if (!string.IsNullOrEmpty(key.Text) && key.Text.Any(IsWordCharacter))
            {
                // The caret is at the end of the completed word. A new letter continues
                // from that position; Shift remains the explicit cancellation gesture.
                ClearSelectedInsertion(keepSuggestion: true);
                ProcessText(key.Text, deferRefresh: true);
                return false;
            }

            if (IsNavigationKey(key.VirtualKey))
            {
                // Navigation keeps the inserted word; stop tracking the temporary selection.
                ClearSelectedInsertion(keepSuggestion: true);
                ResetContextOnly();
                return false;
            }
        }

        if (_window.HasSuggestions && key.VirtualKey == NativeMethods.VkEscape)
        {
            _window.HideSuggestions();
            _suppressSuggestionsForCurrentWord = true;
            _autoCompletionSuppressedUntilBoundary = true;
            return false;
        }

        if (key.VirtualKey == NativeMethods.VkBack)
        {
            if (_currentWord.Length > 0)
            {
                if (_pendingAcceptance is not null &&
                    string.Equals(_currentWord, _pendingAcceptance.SuggestedWord, StringComparison.OrdinalIgnoreCase))
                {
                    RejectSuggestionForCurrentToken(
                        _pendingAcceptance.SuggestedWord,
                        _pendingAcceptance.Prefix);
                    _pendingAcceptance = null;
                }

                _currentWord = _currentWord[..^1];
                _showingNextWordSuggestions = false;
                _needsEditorResync = true;

                if (!_suppressSuggestionsForCurrentWord)
                {
                    UpdateSuggestions();
                }
                ScheduleEditorResync();
            }
            else
            {
                // We can no longer know the previous word after Backspace crosses a word
                // boundary. Drop stale context instead of ranking by text that was deleted.
                _window.HideSuggestions();
                _showingNextWordSuggestions = false;
                _pendingAcceptance = null;
                _currentSuggestions = Array.Empty<SuggestionItem>();
                _contextWords.Clear();
                _needsEditorResync = true;
                ScheduleEditorResync();
            }

            return false;
        }

        if (IsNavigationKey(key.VirtualKey))
        {
            Reset();
            return false;
        }

        if (key.VirtualKey == NativeMethods.VkTab)
        {
            FinalizeCurrentWord(clearContext: true);
            return false;
        }

        if (key.VirtualKey == NativeMethods.VkReturn)
        {
            FinalizeCurrentWord(clearContext: true);
            return false;
        }

        if (string.IsNullOrEmpty(key.Text))
        {
            return false;
        }

        ProcessText(key.Text, deferRefresh: false);
        return false;
    }

    public void Reset()
    {
        _revision++;
        CancelIdleCompletion();
        _currentWord = string.Empty;
        _contextWords.Clear();
        _pendingAcceptance = null;
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _suppressSuggestionsForCurrentWord = false;
        _showingNextWordSuggestions = false;
        _autoCompletionSuppressedUntilBoundary = false;
        _needsEditorResync = false;
        ClearSelectedInsertion(keepSuggestion: true);
        ClearRejectedSuggestions();
        _editorResyncTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _window.HideSuggestions();
    }

    private void ProcessText(string text, bool deferRefresh)
    {
        var containsWordCharacter = text.Any(IsWordCharacter);
        if (containsWordCharacter && _needsEditorResync)
        {
            TryResyncCurrentWordFromEditor(updateSuggestions: false);
            _needsEditorResync = false;
        }

        var showNextAfterInput = false;
        foreach (var character in text)
        {
            if (IsWordCharacter(character))
            {
                if (_showingNextWordSuggestions)
                {
                    _showingNextWordSuggestions = false;
                    _window.HideSuggestions();
                }
                _currentWord += character;
            }
            else
            {
                var endSentence = character is '.' or '!' or '?' or '\n' or '\r';
                var hadWord = _currentWord.Length > 0;
                FinalizeCurrentWord(endSentence);
                showNextAfterInput = hadWord && !endSentence && char.IsWhiteSpace(character);
            }
        }

        if (_currentWord.Length > 0)
        {
            if (IsTrainingMode || _suppressSuggestionsForCurrentWord)
            {
                _currentSuggestions = Array.Empty<SuggestionItem>();
                _window.HideSuggestions();
            }
            else if (deferRefresh)
            {
                QueueSuggestionRefresh();
            }
            else
            {
                UpdateSuggestions();
            }
        }
        else if (showNextAfterInput && !IsTrainingMode)
        {
            QueueNextWordSuggestions();
        }

        if (containsWordCharacter)
        {
            // Debounced UI Automation read repairs state after browser DOM/focus churn
            // without querying the editor synchronously for every keystroke.
            ScheduleEditorResync();
        }
    }

    private void QueueSuggestionRefresh()
    {
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (_currentWord.Length > 0 && !IsTrainingMode && !_suppressSuggestionsForCurrentWord)
                {
                    UpdateSuggestions();
                }
                else
                {
                    _window.HideSuggestions();
                }
            }));
    }

    private void QueueNextWordSuggestions()
    {
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(UpdateNextWordSuggestions));
    }

    private void UpdateNextWordSuggestions()
    {
        CancelIdleCompletion();
        if (IsTrainingMode ||
            !_settings.SemanticSuggestionsEnabled ||
            _currentWord.Length > 0 ||
            _contextWords.Count == 0 ||
            _suppressSuggestionsForCurrentWord)
        {
            return;
        }

        var suggestions = _engine.Suggest(string.Empty, _contextWords.ToArray(), _settings.MaxSuggestions);
        _currentSuggestions = suggestions;
        if (suggestions.Count == 0)
        {
            _window.HideSuggestions();
            _showingNextWordSuggestions = false;
            return;
        }

        _showingNextWordSuggestions = true;
        _window.SetCompletionMode(_settings.CompletionMode);
        _window.ShowSuggestions(suggestions, string.Empty, CaretLocator.GetBestAnchor());
    }

    private void AcceptSelectedSuggestion(bool selectInsertedSuffix)
    {
        var suggestion = _window.SelectedSuggestion;
        if (!string.IsNullOrEmpty(suggestion))
        {
            AcceptSuggestion(suggestion, selectInsertedSuffix);
        }
        else
        {
            _window.HideSuggestions();
        }
    }

    private void AcceptSuggestion(string suggestion, bool selectInsertedSuffix)
    {
        if (string.IsNullOrWhiteSpace(suggestion) ||
            string.Equals(suggestion, _currentWord, StringComparison.OrdinalIgnoreCase))
        {
            _window.HideSuggestions();
            return;
        }

        CancelIdleCompletion();
        var prefix = _currentWord;
        var isPrefixCompletion = prefix.Length == 0 ||
                                 suggestion.Length > prefix.Length &&
                                 suggestion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var insertedLength = 0;
        var wasReplacement = false;

        if (prefix.Length == 0)
        {
            InputInjector.TypeText(suggestion);
            insertedLength = suggestion.Length;
        }
        else if (isPrefixCompletion)
        {
            var suffix = suggestion[prefix.Length..];
            InputInjector.TypeText(suffix);
            insertedLength = suffix.Length;
        }
        else
        {
            // Typo replacement was removed in v6.4. Never destructively rewrite text
            // when a candidate does not begin with the complete word before the caret.
            _window.HideSuggestions();
            return;
        }

        _currentWord = suggestion;
        _pendingAcceptance = new PendingAcceptance(suggestion, prefix, _contextWords.ToArray());
        _showingNextWordSuggestions = false;

        if (selectInsertedSuffix)
        {
            _selectedPrefix = prefix;
            _selectedSuggestion = suggestion;
            _selectedSuffixLength = insertedLength;
            _selectedWasReplacement = wasReplacement;
        }
        else
        {
            ClearSelectedInsertion(keepSuggestion: true);
            ClearRejectedSuggestions();
            _autoCompletionSuppressedUntilBoundary = false;
        }

        _window.HideSuggestions();
    }

    private void UpdateSuggestions()
    {
        _revision++;
        CancelIdleCompletion();
        _showingNextWordSuggestions = false;

        if (IsTrainingMode ||
            _suppressSuggestionsForCurrentWord ||
            _currentWord.Length < _settings.MinPrefixLength ||
            _currentWord.Length > 64)
        {
            _currentSuggestions = Array.Empty<SuggestionItem>();
            _window.HideSuggestions();
            return;
        }

        var suggestions = _engine.Suggest(_currentWord, _contextWords.ToArray(), _settings.MaxSuggestions + 8)
            .Where(item => !IsRejectedForCurrentToken(item.Text))
            .Take(_settings.MaxSuggestions)
            .ToArray();
        _currentSuggestions = suggestions;
        if (suggestions.Length == 0)
        {
            _window.HideSuggestions();
            return;
        }

        _window.SetCompletionMode(_settings.CompletionMode);
        _window.ShowSuggestions(suggestions, _currentWord, CaretLocator.GetBestAnchor());
        ScheduleIdleCompletion();
    }

    private void ScheduleIdleCompletion()
    {
        if (_settings.CompletionMode != "Immediate" ||
            HasSelectedInsertion ||
            _showingNextWordSuggestions ||
            _suppressSuggestionsForCurrentWord ||
            _autoCompletionSuppressedUntilBoundary ||
            _currentSuggestions.Count == 0 ||
            _currentWord.Length < _settings.AutoCompleteMinPrefix)
        {
            return;
        }

        var first = _window.SelectedItem ?? _currentSuggestions[0];
        if (first.Text.Length <= _currentWord.Length ||
            !first.Text.StartsWith(_currentWord, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var exactStat = _store.GetWordStat(_currentWord);
        var exactIsKnown = _store.GetSeedRank(_currentWord) != int.MaxValue || exactStat.TypedCount > 0;

        if (exactIsKnown && !_settings.AutoCompleteShortWords &&
            first.PrefixChoiceCount == 0 && first.ContextCount == 0)
        {
            return;
        }

        if (_currentWord.Length <= 2 && !_settings.AutoCompleteShortWords &&
            first.PrefixChoiceCount == 0 && first.ContextCount == 0)
        {
            return;
        }

        // V6 intentionally completes the best visible candidate after idle time.
        // The user can press Shift at any moment to cancel it for the current word.
        _scheduledRevision = _revision;
        _scheduledWord = _currentWord;
        _scheduledForeground = _lastForeground;
        _scheduledFocusToken = _lastFocusToken;
        _autoCompleteTimer.Change(_settings.AutoCompleteDelayMs, Timeout.Infinite);
    }

    private void OnAutoCompleteTimer()
    {
        var revision = _scheduledRevision;
        var word = _scheduledWord;
        var foreground = _scheduledForeground;
        var focusToken = _scheduledFocusToken;

        _window.Dispatcher.InvokeAsync(() =>
        {
            if (revision != _revision ||
                string.IsNullOrEmpty(word) ||
                !string.Equals(word, _currentWord, StringComparison.Ordinal) ||
                NativeMethods.GetForegroundWindow() != foreground ||
                HasSelectedInsertion ||
                _suppressSuggestionsForCurrentWord ||
                _autoCompletionSuppressedUntilBoundary ||
                _showingNextWordSuggestions)
            {
                return;
            }

            var focus = PrivacyGuard.GetFocusSnapshot();
            if (focus.Ignore || !string.Equals(focus.Token, focusToken, StringComparison.Ordinal))
            {
                return;
            }

            AcceptSelectedSuggestion(selectInsertedSuffix: true);
        });
    }

    private void FinalizeCurrentWord(bool clearContext)
    {
        if (_currentWord.Length >= 1)
        {
            var contextSnapshot = _contextWords.ToArray();
            _store.RecordCompletedWord(
                _currentWord,
                contextSnapshot,
                _pendingAcceptance,
                _settings.LearnTypedWords || IsTrainingMode,
                IsTrainingMode);

            if (_settings.LearnTypedWords || IsTrainingMode)
            {
                LearnGenderSignal(_currentWord, contextSnapshot);
                // Make a newly learned word and its grammatical family available without
                // waiting for an application restart or a profile switch.
                _morphology.EnsureWordsIndexed(new[] { _currentWord });
            }
            _contextWords.Enqueue(LocalLearningStore.NormalizeWord(_currentWord));
            while (_contextWords.Count > 5)
            {
                _contextWords.Dequeue();
            }
        }

        _currentWord = string.Empty;
        _pendingAcceptance = null;
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _suppressSuggestionsForCurrentWord = false;
        _showingNextWordSuggestions = false;
        _autoCompletionSuppressedUntilBoundary = false;
        _needsEditorResync = false;
        ClearSelectedInsertion(keepSuggestion: true);
        ClearRejectedSuggestions();
        _window.HideSuggestions();

        if (clearContext)
        {
            _contextWords.Clear();
        }
    }

    private void LearnGenderSignal(string word, IReadOnlyList<string> context)
    {
        if (!context.TakeLast(3).Contains("я", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var analysis = _morphology.AnalyzeBest(word);
        if (analysis is null ||
            !analysis.Tense.Contains("Past", StringComparison.OrdinalIgnoreCase) &&
            !analysis.Pos.Contains("Adjective", StringComparison.OrdinalIgnoreCase) &&
            !analysis.Pos.Contains("Participle", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (analysis.Gender is "Feminine" or "Masculine" or "Neuter")
        {
            _store.RecordGenderSignal(analysis.Gender);
        }
    }

    public void CancelCurrentCompletionAndResume()
    {
        if (_settings.CompletionMode != "Immediate")
        {
            return;
        }

        CancelIdleCompletion();
        _autoCompletionSuppressedUntilBoundary = true;

        if (HasSelectedInsertion)
        {
            RejectSuggestionForCurrentToken(_selectedSuggestion, _selectedPrefix);
            UndoSelectedInsertion();
            _pendingAcceptance = null;
        }
        else if (_window.SelectedSuggestion is { Length: > 0 } visibleSuggestion && _currentWord.Length > 0)
        {
            RejectSuggestionForCurrentToken(visibleSuggestion, _currentWord);
        }

        _window.HideSuggestions();
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _suppressSuggestionsForCurrentWord = false;
        _showingNextWordSuggestions = false;

        _needsEditorResync = true;
        ScheduleEditorResync();
        if (_currentWord.Length > 0)
        {
            QueueSuggestionRefresh();
        }
    }

    private void UndoSelectedInsertion()
    {
        if (!HasSelectedInsertion)
        {
            return;
        }

        if (_selectedWasReplacement)
        {
            InputInjector.SendBackspaces(_selectedSuffixLength);
            InputInjector.TypeText(_selectedPrefix);
        }
        else
        {
            InputInjector.SendBackspaces(_selectedSuffixLength);
        }

        _currentWord = _selectedPrefix;
        ClearSelectedInsertion(keepSuggestion: true);
    }

    private void RejectSuggestionForCurrentToken(string suggestion, string prefix)
    {
        var normalized = LocalLearningStore.NormalizeWord(suggestion);
        if (normalized.Length == 0 || !_rejectedSuggestionsForCurrentToken.Add(normalized))
        {
            return;
        }

        _store.RecordRejectedSuggestion(normalized, prefix, _contextWords.ToArray());
    }

    private bool IsRejectedForCurrentToken(string suggestion) =>
        _rejectedSuggestionsForCurrentToken.Contains(LocalLearningStore.NormalizeWord(suggestion));

    private void ClearRejectedSuggestions() => _rejectedSuggestionsForCurrentToken.Clear();

    private void ResetContextOnly()
    {
        _currentWord = string.Empty;
        _pendingAcceptance = null;
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _contextWords.Clear();
        _suppressSuggestionsForCurrentWord = false;
        _showingNextWordSuggestions = false;
        _autoCompletionSuppressedUntilBoundary = false;
        _needsEditorResync = false;
        ClearRejectedSuggestions();
        _editorResyncTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _window.HideSuggestions();
    }

    private void SoftResetForFocusChange()
    {
        _revision++;
        CancelIdleCompletion();
        _currentWord = string.Empty;
        _pendingAcceptance = null;
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _showingNextWordSuggestions = false;
        _suppressSuggestionsForCurrentWord = false;
        ClearSelectedInsertion(keepSuggestion: true);
        _window.HideSuggestions();
        _needsEditorResync = true;
        TryResyncCurrentWordFromEditor(updateSuggestions: true);
    }

    private void ScheduleEditorResync()
    {
        _resyncRevision = _revision;
        _resyncForeground = _lastForeground;
        _editorResyncTimer.Change(70, Timeout.Infinite);
    }

    private void OnEditorResyncTimer()
    {
        var revision = _resyncRevision;
        var foreground = _resyncForeground;
        _window.Dispatcher.InvokeAsync(() =>
        {
            if (foreground == IntPtr.Zero || NativeMethods.GetForegroundWindow() != foreground)
            {
                return;
            }

            // A newer key event may have queued another read. The last debounced read wins.
            if (revision != _resyncRevision)
            {
                return;
            }

            TryResyncCurrentWordFromEditor(updateSuggestions: true);
        });
    }

    private bool TryResyncCurrentWordFromEditor(bool updateSuggestions)
    {
        var focus = PrivacyGuard.GetFocusSnapshot();
        if (focus.Ignore || NativeMethods.GetForegroundWindow() != _lastForeground)
        {
            return false;
        }

        if (!FocusedTextReader.TryGetCurrentWordBeforeCaret(out var actualWord))
        {
            return false;
        }

        _needsEditorResync = false;
        if (string.Equals(actualWord, _currentWord, StringComparison.Ordinal))
        {
            return true;
        }

        _revision++;
        CancelIdleCompletion();
        _currentWord = actualWord;
        _pendingAcceptance = null;
        _currentSuggestions = Array.Empty<SuggestionItem>();
        _showingNextWordSuggestions = false;
        ClearSelectedInsertion(keepSuggestion: true);

        if (!updateSuggestions)
        {
            return true;
        }

        if (_currentWord.Length == 0 || IsTrainingMode || _suppressSuggestionsForCurrentWord)
        {
            _window.HideSuggestions();
        }
        else
        {
            UpdateSuggestions();
        }

        return true;
    }

    private void CancelIdleCompletion()
    {
        _scheduledWord = string.Empty;
        _scheduledFocusToken = string.Empty;
        _scheduledForeground = IntPtr.Zero;
        _autoCompleteTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void ClearSelectedInsertion(bool keepSuggestion)
    {
        if (!keepSuggestion && _selectedPrefix.Length > 0)
        {
            _currentWord = _selectedPrefix;
        }

        _selectedPrefix = string.Empty;
        _selectedSuggestion = string.Empty;
        _selectedSuffixLength = 0;
        _selectedWasReplacement = false;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetter(character) || character is '\'' or '’' or '-';

    private static bool IsNavigationKey(int virtualKey) => virtualKey is
        NativeMethods.VkLeft or NativeMethods.VkUp or NativeMethods.VkRight or NativeMethods.VkDown or
        NativeMethods.VkHome or NativeMethods.VkEnd or NativeMethods.VkPrior or NativeMethods.VkNext or
        NativeMethods.VkDelete or NativeMethods.VkEscape;

    public void Dispose()
    {
        _autoCompleteTimer.Dispose();
        _editorResyncTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
