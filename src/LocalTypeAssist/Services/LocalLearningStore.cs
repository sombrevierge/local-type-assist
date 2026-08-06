using System.Text.Json;
using System.Text.RegularExpressions;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class LocalLearningStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _seedRanks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _seedBigrams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _seedTrigrams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _seedFourgrams = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _saveTimer;
    private ProfileData _data = new();
    private string _profileName = "default";
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public event EventHandler? Changed;

    public LocalLearningStore()
    {
        LoadSeedWords();
        LoadSeedPhrases();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string ProfileName
    {
        get
        {
            lock (_gate)
            {
                return _profileName;
            }
        }
    }

    public int LearnedWordCount
    {
        get
        {
            lock (_gate)
            {
                return _data.Words.Count;
            }
        }
    }

    public int SeedWordCount
    {
        get
        {
            lock (_gate)
            {
                return _seedRanks.Count;
            }
        }
    }

    public int LearnedContextCount
    {
        get
        {
            lock (_gate)
            {
                return _data.Bigrams.Count + _data.Trigrams.Count + _data.Fourgrams.Count +
                       _data.Fivegrams.Count + _data.ContextPrefixChoices.Values.Sum(x => x.Count);
            }
        }
    }

    public int TotalObservations
    {
        get
        {
            lock (_gate)
            {
                return _data.Words.Values.Sum(x => x.TypedCount + x.AcceptedCount);
            }
        }
    }

    public void SwitchProfile(string profileName)
    {
        SaveNow();
        var safeName = SanitizeProfileName(profileName);

        lock (_gate)
        {
            _profileName = safeName;
            _data = LoadProfileData(ProfilePath(safeName));
            _dirty = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> GetSeedWordsSnapshot()
    {
        lock (_gate)
        {
            return _seedRanks.OrderBy(x => x.Value).Select(x => x.Key).ToArray();
        }
    }

    public IReadOnlyList<string> GetKnownWordsSnapshot()
    {
        lock (_gate)
        {
            return _data.Words.Keys.ToArray();
        }
    }

    public IReadOnlyList<string> GetCandidateWords(string prefix, int limit = 5000)
    {
        var normalized = NormalizeWord(prefix);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        lock (_gate)
        {
            return _data.Words.Keys
                .Concat(_seedRanks.Keys)
                .Where(word => word.Length > normalized.Length && word.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(word =>
                {
                    var stat = _data.Words.TryGetValue(word, out var value) ? value : null;
                    return (stat?.TrainingCount ?? 0) * 30 +
                           (stat?.AcceptedCount ?? 0) * 12 +
                           (stat?.CorpusCount ?? 0) * 3 +
                           (stat?.TypedCount ?? 0);
                })
                .ThenBy(word => _seedRanks.TryGetValue(word, out var rank) ? rank : int.MaxValue)
                .ThenBy(word => word.Length)
                .Take(limit)
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetSimilarCandidateWords(string input, int limit = 240)
    {
        var normalized = NormalizeWord(input);
        if (normalized.Length < 3)
        {
            return Array.Empty<string>();
        }

        var maxDistance = normalized.Length <= 5 ? 1 : 2;
        lock (_gate)
        {
            return _data.Words.Keys
                .Concat(_seedRanks.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(word => !string.Equals(word, normalized, StringComparison.OrdinalIgnoreCase))
                .Where(word => word.Length >= Math.Max(1, normalized.Length - maxDistance) &&
                               word.Length <= normalized.Length + 20)
                .Select(word => new
                {
                    Word = word,
                    Distance = FuzzyPrefixDistance(normalized, word, maxDistance),
                    Personal = _data.Words.TryGetValue(word, out var stat)
                        ? stat.AcceptedCount * 20 + stat.TypedCount * 2
                        : 0,
                    SeedRank = _seedRanks.TryGetValue(word, out var rank) ? rank : int.MaxValue
                })
                .Where(x => x.Distance <= maxDistance)
                .OrderBy(x => x.Distance)
                .ThenByDescending(x => x.Personal)
                .ThenBy(x => x.SeedRank)
                .ThenBy(x => x.Word.Length)
                .Take(limit)
                .Select(x => x.Word)
                .ToArray();
        }
    }

    public WordStat GetWordStat(string word)
    {
        lock (_gate)
        {
            return _data.Words.TryGetValue(NormalizeWord(word), out var stat)
                ? new WordStat
                {
                    TypedCount = stat.TypedCount,
                    AcceptedCount = stat.AcceptedCount,
                    CorrectedCount = stat.CorrectedCount,
                    TrainingCount = stat.TrainingCount,
                    CorpusCount = stat.CorpusCount,
                    LastUsedUtc = stat.LastUsedUtc
                }
                : new WordStat { LastUsedUtc = DateTime.MinValue };
        }
    }

    public int GetSeedRank(string word)
    {
        lock (_gate)
        {
            return _seedRanks.TryGetValue(NormalizeWord(word), out var rank) ? rank : int.MaxValue;
        }
    }

    public int GetBigramCount(string previousWord, string word)
    {
        var key = BigramKey(previousWord, word);
        lock (_gate)
        {
            return _data.Bigrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetTrigramCount(string firstWord, string secondWord, string word)
    {
        var key = TrigramKey(firstWord, secondWord, word);
        lock (_gate)
        {
            return _data.Trigrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetFourgramCount(string firstWord, string secondWord, string thirdWord, string word)
    {
        var key = FourgramKey(firstWord, secondWord, thirdWord, word);
        lock (_gate)
        {
            return _data.Fourgrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetFivegramCount(
        string firstWord,
        string secondWord,
        string thirdWord,
        string fourthWord,
        string word)
    {
        var key = FivegramKey(firstWord, secondWord, thirdWord, fourthWord, word);
        lock (_gate)
        {
            return _data.Fivegrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetSeedBigramCount(string previousWord, string word)
    {
        var key = BigramKey(previousWord, word);
        lock (_gate)
        {
            return _seedBigrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetSeedTrigramCount(string firstWord, string secondWord, string word)
    {
        var key = TrigramKey(firstWord, secondWord, word);
        lock (_gate)
        {
            return _seedTrigrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetSeedFourgramCount(string firstWord, string secondWord, string thirdWord, string word)
    {
        var key = FourgramKey(firstWord, secondWord, thirdWord, word);
        lock (_gate)
        {
            return _seedFourgrams.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public IReadOnlyList<string> GetContextCandidateWords(
        IReadOnlyList<string> contextWords,
        string prefix,
        int limit = 160)
    {
        var context = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(5)
            .ToArray();
        if (context.Length == 0)
        {
            return Array.Empty<string>();
        }

        var normalizedPrefix = NormalizeWord(prefix);
        var previous = context[^1];
        var beforePrevious = context.Length >= 2 ? context[^2] : string.Empty;
        var thirdPrevious = context.Length >= 3 ? context[^3] : string.Empty;
        var fourthPrevious = context.Length >= 4 ? context[^4] : string.Empty;
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            // Explicitly learned context+prefix choices are the strongest source.
            AddFromContextPrefixChoicesLocked(_data.ContextPrefixChoices, context, normalizedPrefix, 420, scores);
            AddFromBigramsLocked(_data.Bigrams, previous, normalizedPrefix, 30, scores);
            AddFromBigramsLocked(_seedBigrams, previous, normalizedPrefix, 1, scores);

            if (beforePrevious.Length > 0)
            {
                AddFromTrigramsLocked(_data.Trigrams, beforePrevious, previous, normalizedPrefix, 80, scores);
                AddFromTrigramsLocked(_seedTrigrams, beforePrevious, previous, normalizedPrefix, 3, scores);
            }

            if (thirdPrevious.Length > 0)
            {
                AddFromFourgramsLocked(_data.Fourgrams, thirdPrevious, beforePrevious, previous, normalizedPrefix, 165, scores);
                AddFromFourgramsLocked(_seedFourgrams, thirdPrevious, beforePrevious, previous, normalizedPrefix, 5, scores);
            }

            if (fourthPrevious.Length > 0)
            {
                AddFromFivegramsLocked(
                    _data.Fivegrams,
                    fourthPrevious,
                    thirdPrevious,
                    beforePrevious,
                    previous,
                    normalizedPrefix,
                    300,
                    scores);
            }
        }

        return scores
            .OrderByDescending(x => x.Value)
            .ThenByDescending(x => GetWordStat(x.Key).TrainingCount)
            .ThenBy(x => GetSeedRank(x.Key))
            .ThenBy(x => x.Key.Length)
            .Take(limit)
            .Select(x => x.Key)
            .ToArray();
    }

    public int GetPrefixChoiceCount(string prefix, string word)
    {
        var key = PrefixChoiceKey(prefix, word);
        lock (_gate)
        {
            return _data.PrefixChoices.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public int GetContextPrefixChoiceCount(
        string prefix,
        IReadOnlyList<string> contextWords,
        string word)
    {
        var context = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(4)
            .ToArray();
        var normalizedPrefix = NormalizeWord(prefix);
        var normalizedWord = NormalizeWord(word);
        if (context.Length == 0 || normalizedPrefix.Length == 0 || normalizedWord.Length == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var total = 0;
            for (var length = 1; length <= context.Length; length++)
            {
                var key = ContextPrefixKey(context[^length..], normalizedPrefix);
                if (_data.ContextPrefixChoices.TryGetValue(key, out var candidates) &&
                    candidates.TryGetValue(normalizedWord, out var count))
                {
                    total += count * length;
                }
            }

            return total;
        }
    }

    public int GetRejectedChoiceCount(string prefix, IReadOnlyList<string> contextWords, string word)
    {
        var key = RejectedChoiceKey(prefix, contextWords, word);
        lock (_gate)
        {
            return _data.RejectedChoices.TryGetValue(key, out var count) ? count : 0;
        }
    }

    public string GetLearnedGender()
    {
        lock (_gate)
        {
            var best = _data.GenderSignals
                .Where(x => x.Key is "Feminine" or "Masculine" or "Neuter")
                .OrderByDescending(x => x.Value)
                .FirstOrDefault();
            return best.Value >= 2 ? best.Key : string.Empty;
        }
    }

    public void RecordRejectedSuggestion(
        string suggestion,
        string prefix,
        IReadOnlyList<string> contextWords)
    {
        var word = NormalizeWord(suggestion);
        if (!LooksLikeWord(word))
        {
            return;
        }

        var key = RejectedChoiceKey(prefix, contextWords, word);
        lock (_gate)
        {
            _data.RejectedChoices[key] = _data.RejectedChoices.TryGetValue(key, out var count)
                ? Math.Min(count + 1, 100)
                : 1;
            MarkDirtyLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordGenderSignal(string gender)
    {
        if (gender is not ("Feminine" or "Masculine" or "Neuter"))
        {
            return;
        }

        lock (_gate)
        {
            _data.GenderSignals[gender] = _data.GenderSignals.TryGetValue(gender, out var count) ? count + 1 : 1;
            MarkDirtyLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordCompletedWord(
        string actualWord,
        IReadOnlyList<string> contextWords,
        PendingAcceptance? pendingAcceptance,
        bool learnTypedWords,
        bool trainingMode)
    {
        var actual = NormalizeWord(actualWord);
        if (!LooksLikeWord(actual))
        {
            return;
        }

        var normalizedContext = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(5)
            .ToArray();
        var learningWeight = trainingMode ? 4 : 1;
        var shouldLearnTyped = learnTypedWords || trainingMode;
        var shouldLearnSequence = shouldLearnTyped || pendingAcceptance is not null;

        lock (_gate)
        {
            if (shouldLearnTyped)
            {
                var actualStat = GetOrCreateWordLocked(actual);
                actualStat.TypedCount++;
                if (trainingMode)
                {
                    actualStat.TrainingCount += learningWeight;
                }
                actualStat.LastUsedUtc = DateTime.UtcNow;
                LearnPrefixChoicesLocked(actual, trainingMode ? 5 : 1);
                LearnContextPrefixChoicesLocked(actual, normalizedContext, trainingMode ? 5 : 1);
            }

            if (pendingAcceptance is not null)
            {
                var suggested = NormalizeWord(pendingAcceptance.SuggestedWord);
                var suggestionStat = GetOrCreateWordLocked(suggested);
                if (string.Equals(actual, suggested, StringComparison.OrdinalIgnoreCase))
                {
                    suggestionStat.AcceptedCount++;
                    suggestionStat.LastUsedUtc = DateTime.UtcNow;
                    var prefixKey = PrefixChoiceKey(pendingAcceptance.Prefix, suggested);
                    _data.PrefixChoices[prefixKey] = _data.PrefixChoices.TryGetValue(prefixKey, out var count)
                        ? count + 3
                        : 3;
                    LearnContextPrefixChoicesLocked(suggested, pendingAcceptance.ContextWords, 3);

                    var rejectedKey = RejectedChoiceKey(
                        pendingAcceptance.Prefix,
                        pendingAcceptance.ContextWords,
                        suggested);
                    if (_data.RejectedChoices.TryGetValue(rejectedKey, out var rejectedCount))
                    {
                        if (rejectedCount <= 1)
                        {
                            _data.RejectedChoices.Remove(rejectedKey);
                        }
                        else
                        {
                            _data.RejectedChoices[rejectedKey] = rejectedCount - 1;
                        }
                    }
                }
                else
                {
                    suggestionStat.CorrectedCount++;
                }
            }

            if (shouldLearnSequence)
            {
                if (normalizedContext.Length >= 1)
                {
                    IncrementLocked(_data.Bigrams, BigramKey(normalizedContext[^1], actual), learningWeight);
                }

                if (normalizedContext.Length >= 2)
                {
                    IncrementLocked(_data.Trigrams, TrigramKey(normalizedContext[^2], normalizedContext[^1], actual), learningWeight);
                }

                if (normalizedContext.Length >= 3)
                {
                    IncrementLocked(
                        _data.Fourgrams,
                        FourgramKey(normalizedContext[^3], normalizedContext[^2], normalizedContext[^1], actual),
                        learningWeight);
                }

                if (normalizedContext.Length >= 4)
                {
                    IncrementLocked(
                        _data.Fivegrams,
                        FivegramKey(
                            normalizedContext[^4],
                            normalizedContext[^3],
                            normalizedContext[^2],
                            normalizedContext[^1],
                            actual),
                        learningWeight);
                }
            }

            MarkDirtyLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public (int Words, int Tokens) ImportCorpus(IEnumerable<string> filePaths)
    {
        var wordRegex = new Regex(@"[A-Za-zА-Яа-яЁё][A-Za-zА-Яа-яЁё'’-]{0,39}", RegexOptions.Compiled);
        var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bigramCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var trigramCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fourgramCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fivegramCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var contextPrefixCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var importedTokens = 0;

        foreach (var path in filePaths)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 40 * 1024 * 1024)
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
                continue;
            }

            foreach (var segment in Regex.Split(text, @"[.!?\r\n]+"))
            {
                var context = new Queue<string>();
                foreach (Match match in wordRegex.Matches(segment))
                {
                    var word = NormalizeWord(match.Value);
                    if (!LooksLikeWord(word))
                    {
                        continue;
                    }

                    wordCounts[word] = wordCounts.TryGetValue(word, out var wordCount) ? wordCount + 1 : 1;
                    importedTokens++;

                    if (context.Count >= 1)
                    {
                        var previous = context.Last();
                        var key = BigramKey(previous, word);
                        bigramCounts[key] = bigramCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    }

                    if (context.Count >= 2)
                    {
                        var values = context.ToArray();
                        var key = TrigramKey(values[^2], values[^1], word);
                        trigramCounts[key] = trigramCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    }

                    if (context.Count >= 3)
                    {
                        var values = context.ToArray();
                        var key = FourgramKey(values[^3], values[^2], values[^1], word);
                        fourgramCounts[key] = fourgramCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    }

                    if (context.Count >= 4)
                    {
                        var values = context.ToArray();
                        var key = FivegramKey(values[^4], values[^3], values[^2], values[^1], word);
                        fivegramCounts[key] = fivegramCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    }

                    if (context.Count > 0 && contextPrefixCounts.Count < 250000 && word.Length > 1)
                    {
                        var values = context.ToArray();
                        var maxContext = Math.Min(4, values.Length);
                        var maxPrefix = Math.Min(8, word.Length - 1);
                        for (var contextLength = 1; contextLength <= maxContext; contextLength++)
                        {
                            var contextSlice = values[^contextLength..];
                            for (var prefixLength = 1; prefixLength <= maxPrefix; prefixLength++)
                            {
                                var key = ContextPrefixKey(contextSlice, word[..prefixLength]);
                                if (!contextPrefixCounts.TryGetValue(key, out var candidates))
                                {
                                    candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    contextPrefixCounts[key] = candidates;
                                }

                                candidates[word] = candidates.TryGetValue(word, out var count)
                                    ? count + 1
                                    : 1;
                            }
                        }
                    }

                    context.Enqueue(word);
                    while (context.Count > 4)
                    {
                        context.Dequeue();
                    }
                }
            }
        }

        if (importedTokens == 0)
        {
            return (0, 0);
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var pair in wordCounts)
            {
                var stat = GetOrCreateWordLocked(pair.Key);
                stat.TypedCount += pair.Value;
                stat.CorpusCount += pair.Value;
                stat.LastUsedUtc = now;
            }

            foreach (var pair in bigramCounts)
            {
                _data.Bigrams[pair.Key] = _data.Bigrams.TryGetValue(pair.Key, out var count)
                    ? count + pair.Value
                    : pair.Value;
            }

            foreach (var pair in trigramCounts)
            {
                _data.Trigrams[pair.Key] = _data.Trigrams.TryGetValue(pair.Key, out var count)
                    ? count + pair.Value
                    : pair.Value;
            }

            foreach (var pair in fourgramCounts)
            {
                _data.Fourgrams[pair.Key] = _data.Fourgrams.TryGetValue(pair.Key, out var count)
                    ? count + pair.Value
                    : pair.Value;
            }

            foreach (var pair in fivegramCounts)
            {
                _data.Fivegrams[pair.Key] = _data.Fivegrams.TryGetValue(pair.Key, out var count)
                    ? count + pair.Value
                    : pair.Value;
            }

            foreach (var pair in contextPrefixCounts)
            {
                if (!_data.ContextPrefixChoices.TryGetValue(pair.Key, out var candidates))
                {
                    candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _data.ContextPrefixChoices[pair.Key] = candidates;
                }

                foreach (var candidate in pair.Value)
                {
                    candidates[candidate.Key] = candidates.TryGetValue(candidate.Key, out var count)
                        ? count + candidate.Value
                        : candidate.Value;
                }
            }

            foreach (var pair in wordCounts)
            {
                LearnPrefixChoicesLocked(pair.Key, Math.Max(1, Math.Min(5, pair.Value)));
            }

            MarkDirtyLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return (wordCounts.Count, importedTokens);
    }

    public void ResetActiveProfile()
    {
        lock (_gate)
        {
            _data = new ProfileData();
            _dirty = true;
        }

        SaveNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> ListProfiles()
    {
        Directory.CreateDirectory(AppSettings.ProfilesRoot);
        return Directory.EnumerateFiles(AppSettings.ProfilesRoot, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public void SaveNow()
    {
        ProfileData snapshot;
        string path;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _data.UpdatedUtc = DateTime.UtcNow;
            snapshot = new ProfileData
            {
                UpdatedUtc = _data.UpdatedUtc,
                Words = _data.Words.ToDictionary(
                    pair => pair.Key,
                    pair => new WordStat
                    {
                        TypedCount = pair.Value.TypedCount,
                        AcceptedCount = pair.Value.AcceptedCount,
                        CorrectedCount = pair.Value.CorrectedCount,
                        TrainingCount = pair.Value.TrainingCount,
                        CorpusCount = pair.Value.CorpusCount,
                        LastUsedUtc = pair.Value.LastUsedUtc
                    },
                    StringComparer.OrdinalIgnoreCase),
                Bigrams = new Dictionary<string, int>(_data.Bigrams, StringComparer.OrdinalIgnoreCase),
                Trigrams = new Dictionary<string, int>(_data.Trigrams, StringComparer.OrdinalIgnoreCase),
                Fourgrams = new Dictionary<string, int>(_data.Fourgrams, StringComparer.OrdinalIgnoreCase),
                Fivegrams = new Dictionary<string, int>(_data.Fivegrams, StringComparer.OrdinalIgnoreCase),
                PrefixChoices = new Dictionary<string, int>(_data.PrefixChoices, StringComparer.OrdinalIgnoreCase),
                ContextPrefixChoices = _data.ContextPrefixChoices.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, int>(pair.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                RejectedChoices = new Dictionary<string, int>(_data.RejectedChoices, StringComparer.OrdinalIgnoreCase),
                GenderSignals = new Dictionary<string, int>(_data.GenderSignals, StringComparer.OrdinalIgnoreCase)
            };
            path = ProfilePath(_profileName);
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(AppSettings.ProfilesRoot);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, path, true);
        }
        catch
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    private void LoadSeedWords()
    {
        var assembly = typeof(LocalLearningStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.seed_words.txt", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return;
        }

        using var reader = new StreamReader(stream);
        var rank = 1;
        while (reader.ReadLine() is { } line)
        {
            var word = NormalizeWord(line);
            if (LooksLikeWord(word) && !_seedRanks.ContainsKey(word))
            {
                _seedRanks[word] = rank++;
            }
        }
    }

    private void LoadSeedPhrases()
    {
        var assembly = typeof(LocalLearningStore).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Resources.seed_phrases.tsv", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !int.TryParse(parts[^1], out var weight) || weight <= 0)
            {
                continue;
            }

            if (parts.Length == 3)
            {
                var first = NormalizeWord(parts[0]);
                var second = NormalizeWord(parts[1]);
                if (LooksLikeWord(first) && LooksLikeWord(second))
                {
                    _seedBigrams[BigramKey(first, second)] = weight;
                }
            }
            else if (parts.Length == 4)
            {
                var first = NormalizeWord(parts[0]);
                var second = NormalizeWord(parts[1]);
                var third = NormalizeWord(parts[2]);
                if (LooksLikeWord(first) && LooksLikeWord(second) && LooksLikeWord(third))
                {
                    _seedTrigrams[TrigramKey(first, second, third)] = weight;
                }
            }
            else if (parts.Length == 5)
            {
                var first = NormalizeWord(parts[0]);
                var second = NormalizeWord(parts[1]);
                var third = NormalizeWord(parts[2]);
                var fourth = NormalizeWord(parts[3]);
                if (LooksLikeWord(first) && LooksLikeWord(second) && LooksLikeWord(third) && LooksLikeWord(fourth))
                {
                    _seedFourgrams[FourgramKey(first, second, third, fourth)] = weight;
                }
            }
        }
    }

    private static void AddFromBigramsLocked(
        IReadOnlyDictionary<string, int> source,
        string previous,
        string prefix,
        int multiplier,
        IDictionary<string, int> scores)
    {
        var keyPrefix = NormalizeWord(previous) + "\u001F";
        foreach (var pair in source)
        {
            if (!pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = pair.Key[keyPrefix.Length..];
            if (candidate.Length == 0 ||
                prefix.Length > 0 && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scores[candidate] = scores.TryGetValue(candidate, out var current)
                ? current + pair.Value * multiplier
                : pair.Value * multiplier;
        }
    }

    private static void AddFromTrigramsLocked(
        IReadOnlyDictionary<string, int> source,
        string first,
        string second,
        string prefix,
        int multiplier,
        IDictionary<string, int> scores)
    {
        var keyPrefix = NormalizeWord(first) + "\u001F" + NormalizeWord(second) + "\u001F";
        foreach (var pair in source)
        {
            if (!pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = pair.Key[keyPrefix.Length..];
            if (candidate.Length == 0 ||
                prefix.Length > 0 && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scores[candidate] = scores.TryGetValue(candidate, out var current)
                ? current + pair.Value * multiplier
                : pair.Value * multiplier;
        }
    }

    private static void AddFromFourgramsLocked(
        IReadOnlyDictionary<string, int> source,
        string first,
        string second,
        string third,
        string prefix,
        int multiplier,
        IDictionary<string, int> scores)
    {
        var keyPrefix = NormalizeWord(first) + "\u001F" + NormalizeWord(second) + "\u001F" + NormalizeWord(third) + "\u001F";
        foreach (var pair in source)
        {
            if (!pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = pair.Key[keyPrefix.Length..];
            if (candidate.Length == 0 ||
                prefix.Length > 0 && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scores[candidate] = scores.TryGetValue(candidate, out var current)
                ? current + pair.Value * multiplier
                : pair.Value * multiplier;
        }
    }

    private static void AddFromFivegramsLocked(
        IReadOnlyDictionary<string, int> source,
        string first,
        string second,
        string third,
        string fourth,
        string prefix,
        int multiplier,
        IDictionary<string, int> scores)
    {
        var keyPrefix = NormalizeWord(first) + "\u001F" + NormalizeWord(second) + "\u001F" +
                        NormalizeWord(third) + "\u001F" + NormalizeWord(fourth) + "\u001F";
        foreach (var pair in source)
        {
            if (!pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = pair.Key[keyPrefix.Length..];
            if (candidate.Length == 0 ||
                prefix.Length > 0 && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scores[candidate] = scores.TryGetValue(candidate, out var current)
                ? current + pair.Value * multiplier
                : pair.Value * multiplier;
        }
    }

    private static void AddFromContextPrefixChoicesLocked(
        IReadOnlyDictionary<string, Dictionary<string, int>> source,
        IReadOnlyList<string> contextWords,
        string prefix,
        int multiplier,
        IDictionary<string, int> scores)
    {
        if (contextWords.Count == 0 || prefix.Length == 0)
        {
            return;
        }

        var context = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(4)
            .ToArray();
        for (var contextLength = 1; contextLength <= context.Length; contextLength++)
        {
            var key = ContextPrefixKey(context[^contextLength..], prefix);
            if (!source.TryGetValue(key, out var candidates))
            {
                continue;
            }

            foreach (var pair in candidates)
            {
                var weighted = pair.Value * multiplier * contextLength;
                scores[pair.Key] = scores.TryGetValue(pair.Key, out var current)
                    ? current + weighted
                    : weighted;
            }
        }
    }

    private static ProfileData LoadProfileData(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ProfileData();
            }

            var data = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(path), JsonOptions) ?? new ProfileData();
            data.Words = new Dictionary<string, WordStat>(data.Words ?? new(), StringComparer.OrdinalIgnoreCase);
            data.Bigrams = new Dictionary<string, int>(data.Bigrams ?? new(), StringComparer.OrdinalIgnoreCase);
            data.Trigrams = new Dictionary<string, int>(data.Trigrams ?? new(), StringComparer.OrdinalIgnoreCase);
            data.Fourgrams = new Dictionary<string, int>(data.Fourgrams ?? new(), StringComparer.OrdinalIgnoreCase);
            data.Fivegrams = new Dictionary<string, int>(data.Fivegrams ?? new(), StringComparer.OrdinalIgnoreCase);
            data.PrefixChoices = new Dictionary<string, int>(data.PrefixChoices ?? new(), StringComparer.OrdinalIgnoreCase);
            data.ContextPrefixChoices = (data.ContextPrefixChoices ?? new())
                .ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, int>(pair.Value ?? new(), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            data.RejectedChoices = new Dictionary<string, int>(data.RejectedChoices ?? new(), StringComparer.OrdinalIgnoreCase);
            data.GenderSignals = new Dictionary<string, int>(data.GenderSignals ?? new(), StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch
        {
            var brokenPath = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, brokenPath, true);
            }
            catch
            {
                // ignored
            }

            return new ProfileData();
        }
    }

    private WordStat GetOrCreateWordLocked(string word)
    {
        if (!_data.Words.TryGetValue(word, out var stat))
        {
            stat = new WordStat();
            _data.Words[word] = stat;
        }

        return stat;
    }

    private void LearnPrefixChoicesLocked(string word, int weight)
    {
        if (weight <= 0 || word.Length < 2)
        {
            return;
        }

        var maxPrefixLength = Math.Min(32, word.Length - 1);
        for (var length = 1; length <= maxPrefixLength; length++)
        {
            var prefix = word[..length];
            var key = PrefixChoiceKey(prefix, word);
            _data.PrefixChoices[key] = _data.PrefixChoices.TryGetValue(key, out var count)
                ? count + weight
                : weight;
        }
    }

    private void LearnContextPrefixChoicesLocked(
        string word,
        IReadOnlyList<string> contextWords,
        int weight)
    {
        if (weight <= 0 || word.Length < 2)
        {
            return;
        }

        var context = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(4)
            .ToArray();
        if (context.Length == 0)
        {
            return;
        }

        var maxPrefixLength = Math.Min(24, word.Length - 1);
        for (var contextLength = 1; contextLength <= context.Length; contextLength++)
        {
            var contextSlice = context[^contextLength..];
            for (var prefixLength = 1; prefixLength <= maxPrefixLength; prefixLength++)
            {
                var key = ContextPrefixKey(contextSlice, word[..prefixLength]);
                if (!_data.ContextPrefixChoices.TryGetValue(key, out var candidates))
                {
                    candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _data.ContextPrefixChoices[key] = candidates;
                }

                IncrementLocked(candidates, word, weight);
            }
        }
    }

    private static void IncrementLocked(IDictionary<string, int> target, string key, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        target[key] = target.TryGetValue(key, out var count)
            ? Math.Min(int.MaxValue - amount, count) + amount
            : amount;
    }

    private void MarkDirtyLocked()
    {
        _dirty = true;
        _saveTimer.Change(900, Timeout.Infinite);
    }

    private static string ProfilePath(string profileName) =>
        Path.Combine(AppSettings.ProfilesRoot, SanitizeProfileName(profileName) + ".json");

    public static string SanitizeProfileName(string profileName)
    {
        var cleaned = Regex.Replace(profileName.Trim(), @"[^A-Za-zА-Яа-яЁё0-9._-]+", "-").Trim('-', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "default" : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    public static string NormalizeWord(string word) =>
        (word ?? string.Empty).Trim().Trim('’', '\'', '-', '—').ToLowerInvariant().Replace('ё', 'е');

    private static bool LooksLikeWord(string word) =>
        word.Length is >= 1 and <= 40 && word.Any(char.IsLetter);

    private static string BigramKey(string previousWord, string word) =>
        NormalizeWord(previousWord) + "\u001F" + NormalizeWord(word);

    private static string TrigramKey(string firstWord, string secondWord, string word) =>
        NormalizeWord(firstWord) + "\u001F" + NormalizeWord(secondWord) + "\u001F" + NormalizeWord(word);

    private static string FourgramKey(string firstWord, string secondWord, string thirdWord, string word) =>
        NormalizeWord(firstWord) + "\u001F" + NormalizeWord(secondWord) + "\u001F" +
        NormalizeWord(thirdWord) + "\u001F" + NormalizeWord(word);

    private static string FivegramKey(
        string firstWord,
        string secondWord,
        string thirdWord,
        string fourthWord,
        string word) =>
        NormalizeWord(firstWord) + "\u001F" + NormalizeWord(secondWord) + "\u001F" +
        NormalizeWord(thirdWord) + "\u001F" + NormalizeWord(fourthWord) + "\u001F" +
        NormalizeWord(word);

    private static string ContextPrefixKey(
        IReadOnlyList<string> contextWords,
        string prefix) =>
        string.Join("\u001F", contextWords.Select(NormalizeWord).Where(x => x.Length > 0).TakeLast(4)) +
        "\u001E" + NormalizeWord(prefix);

    private static string RejectedChoiceKey(
        string prefix,
        IReadOnlyList<string> contextWords,
        string word)
    {
        var context = contextWords
            .Select(NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(2);
        return string.Join("\u001F", context) + "\u001E" +
               NormalizeWord(prefix) + "\u001F" + NormalizeWord(word);
    }

    private static string PrefixChoiceKey(string prefix, string word) =>
        NormalizeWord(prefix) + "\u001F" + NormalizeWord(word);

    public static int FuzzyPrefixDistance(string input, string candidate, int maxDistance)
    {
        input = NormalizeWord(input);
        candidate = NormalizeWord(candidate);
        if (input.Length == 0 || candidate.Length == 0)
        {
            return maxDistance + 1;
        }

        var minLength = Math.Max(1, input.Length - maxDistance);
        var maxLength = Math.Min(candidate.Length, input.Length + maxDistance);
        var best = maxDistance + 1;
        for (var length = minLength; length <= maxLength; length++)
        {
            var distance = BoundedDamerauLevenshtein(input, candidate[..length], maxDistance);
            best = Math.Min(best, distance);
            if (best == 0)
            {
                break;
            }
        }

        return best;
    }

    public static int BoundedDamerauLevenshtein(string left, string right, int maxDistance)
    {
        left = NormalizeWord(left);
        right = NormalizeWord(right);

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (Math.Abs(left.Length - right.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        var previousPrevious = new int[right.Length + 1];
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
            previousPrevious[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];

            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1 &&
                    left[i - 1] == right[j - 2] &&
                    left[i - 2] == right[j - 1])
                {
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                }

                current[j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            if (rowMinimum > maxDistance)
            {
                return maxDistance + 1;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[right.Length];
    }

    public void Dispose()
    {
        SaveNow();
        _saveTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
