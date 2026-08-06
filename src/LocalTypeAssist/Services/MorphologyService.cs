using LocalTypeAssist.Models;
using Nestor;

namespace LocalTypeAssist.Services;

public sealed class MorphologyService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _morphCallGate = new();
    private readonly Dictionary<string, MorphCandidate> _forms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _prefixIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, HashSet<string>> _lengthIndex = new();
    private readonly HashSet<string> _indexedSourceWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MorphCandidate>> _analysisCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MorphCandidate>> _fuzzyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private NestorMorph? _morph;
    private bool _initializing;

    public bool IsReady { get; private set; }
    public bool Failed { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    public int IndexedFormCount
    {
        get
        {
            lock (_gate)
            {
                return _forms.Count;
            }
        }
    }

    public event EventHandler? StateChanged;

    public void Initialize(IEnumerable<string> words)
    {
        lock (_gate)
        {
            if (_initializing || IsReady)
            {
                return;
            }

            _initializing = true;
            Failed = false;
            LastError = string.Empty;
        }

        var snapshot = words
            .Select(LocalLearningStore.NormalizeWord)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ = Task.Run(() =>
        {
            try
            {
                // Use the package API directly. The previous reflection lookup expected a
                // one-parameter WordInfo method, while Nestor exposes an optional second
                // MorphOption parameter; that made the old loader report a false failure.
                var morph = new NestorMorph();
                lock (_gate)
                {
                    _morph = morph;
                    IsReady = true;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                IndexWordsInternal(snapshot);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    IsReady = false;
                    Failed = true;
                    LastError = GetReadableError(exception);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _initializing = false;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }, _cts.Token);
    }

    public void Retry(IEnumerable<string> words)
    {
        lock (_gate)
        {
            if (_initializing)
            {
                return;
            }

            IsReady = false;
            Failed = false;
            LastError = string.Empty;
            _morph = null;
            _forms.Clear();
            _prefixIndex.Clear();
            _lengthIndex.Clear();
            _indexedSourceWords.Clear();
            _analysisCache.Clear();
            _fuzzyCache.Clear();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        Initialize(words);
    }

    public void EnsureWordsIndexed(IEnumerable<string> words)
    {
        if (!IsReady || Failed)
        {
            return;
        }

        var pending = words
            .Select(LocalLearningStore.NormalizeWord)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x =>
            {
                lock (_gate)
                {
                    return !_indexedSourceWords.Contains(x);
                }
            })
            .Take(500)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            IndexWordsInternal(pending);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }, _cts.Token);
    }

    public IReadOnlyList<MorphCandidate> GetCandidates(string rawPrefix, int limit = 3500)
    {
        var prefix = LocalLearningStore.NormalizeWord(rawPrefix);
        if (prefix.Length == 0 || !IsReady)
        {
            return Array.Empty<MorphCandidate>();
        }

        lock (_gate)
        {
            if (!_prefixIndex.TryGetValue(prefix, out var words))
            {
                return Array.Empty<MorphCandidate>();
            }

            return words
                .Select(x => _forms.TryGetValue(x, out var candidate) ? candidate : null)
                .Where(x => x is not null && x.Text.Length > prefix.Length)
                .Take(limit)
                .Cast<MorphCandidate>()
                .ToArray();
        }
    }

    public IReadOnlyList<MorphCandidate> GetSimilarCandidates(string rawWord, int limit = 160)
    {
        var word = LocalLearningStore.NormalizeWord(rawWord);
        if (word.Length < 3 || !IsReady)
        {
            return Array.Empty<MorphCandidate>();
        }

        lock (_gate)
        {
            if (_fuzzyCache.TryGetValue(word, out var cached))
            {
                return cached.Take(limit).ToArray();
            }
        }

        var maxDistance = word.Length <= 5 ? 1 : 2;
        var candidates = new List<(MorphCandidate Candidate, int Distance)>();
        lock (_gate)
        {
            for (var length = Math.Max(1, word.Length - maxDistance);
                 length <= Math.Min(40, word.Length + 18);
                 length++)
            {
                if (!_lengthIndex.TryGetValue(length, out var bucket))
                {
                    continue;
                }

                foreach (var candidateWord in bucket)
                {
                    if (string.Equals(candidateWord, word, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var distance = LocalLearningStore.FuzzyPrefixDistance(word, candidateWord, maxDistance);
                    if (distance <= maxDistance && _forms.TryGetValue(candidateWord, out var candidate))
                    {
                        candidates.Add((candidate, distance));
                    }
                }
            }
        }

        var result = candidates
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Candidate.Text.Length)
            .Select(x => x.Candidate)
            .DistinctBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(limit, 300))
            .ToArray();

        lock (_gate)
        {
            _fuzzyCache[word] = result;
            if (_fuzzyCache.Count > 600)
            {
                _fuzzyCache.Clear();
                _fuzzyCache[word] = result;
            }
        }

        return result.Take(limit).ToArray();
    }

    public MorphCandidate? AnalyzeBest(string word)
    {
        var normalized = LocalLearningStore.NormalizeWord(word);
        if (normalized.Length == 0 || !IsReady)
        {
            return null;
        }

        lock (_gate)
        {
            if (_forms.TryGetValue(normalized, out var indexed))
            {
                return indexed;
            }

            if (_analysisCache.TryGetValue(normalized, out var cached))
            {
                return cached.FirstOrDefault();
            }
        }

        var analyzed = AnalyzeWordFamily(normalized);
        lock (_gate)
        {
            _analysisCache[normalized] = analyzed;
        }

        return analyzed.FirstOrDefault(x => string.Equals(x.Text, normalized, StringComparison.OrdinalIgnoreCase))
               ?? analyzed.FirstOrDefault();
    }

    private void IndexWordsInternal(IEnumerable<string> words)
    {
        foreach (var word in words)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            var normalized = LocalLearningStore.NormalizeWord(word);
            if (normalized.Length == 0)
            {
                continue;
            }

            lock (_gate)
            {
                if (!_indexedSourceWords.Add(normalized))
                {
                    continue;
                }
            }

            var family = AnalyzeWordFamily(normalized);
            if (family.Count == 0)
            {
                family = new[] { MorphCandidate.Plain(normalized) };
            }

            lock (_gate)
            {
                foreach (var candidate in family)
                {
                    AddCandidateLocked(candidate);
                }
            }
        }
    }

    private IReadOnlyList<MorphCandidate> AnalyzeWordFamily(string word)
    {
        NestorMorph? morph;
        lock (_gate)
        {
            morph = _morph;
        }

        if (morph is null)
        {
            return Array.Empty<MorphCandidate>();
        }

        try
        {
            lock (_morphCallGate)
            {
                var variants = morph.WordInfo(word);
                var collected = new Dictionary<string, MorphCandidate>(StringComparer.OrdinalIgnoreCase);

                foreach (var variant in variants)
                {
                    var lemma = LocalLearningStore.NormalizeWord(variant.Lemma.Word);
                    if (lemma.Length == 0)
                    {
                        lemma = word;
                    }

                    foreach (var form in variant.Forms)
                    {
                        var text = LocalLearningStore.NormalizeWord(form.Word);
                        if (text.Length == 0 || text.Length > 40 || !text.Any(char.IsLetter))
                        {
                            continue;
                        }

                        var tag = form.Tag;
                        collected[text] = new MorphCandidate(
                            text,
                            lemma,
                            tag.Pos.ToString(),
                            tag.Gender.ToString(),
                            tag.Number.ToString(),
                            tag.Case.ToString(),
                            tag.Tense.ToString(),
                            tag.Person.ToString());
                    }
                }

                return collected.Values.ToArray();
            }
        }
        catch
        {
            return Array.Empty<MorphCandidate>();
        }
    }

    private void AddCandidateLocked(MorphCandidate candidate)
    {
        var text = LocalLearningStore.NormalizeWord(candidate.Text);
        if (text.Length == 0)
        {
            return;
        }

        _forms[text] = candidate with { Text = text };
        if (!_lengthIndex.TryGetValue(text.Length, out var sameLength))
        {
            sameLength = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lengthIndex[text.Length] = sameLength;
        }
        sameLength.Add(text);
        _fuzzyCache.Clear();

        for (var length = 1; length < text.Length && length <= 40; length++)
        {
            var prefix = text[..length];
            if (!_prefixIndex.TryGetValue(prefix, out var bucket))
            {
                bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _prefixIndex[prefix] = bucket;
            }

            bucket.Add(text);
        }
    }

    private static string GetReadableError(Exception exception)
    {
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return $"{root.GetType().Name}: {root.Message}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
