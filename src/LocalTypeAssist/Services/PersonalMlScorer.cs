using System.Text.Json;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class PersonalMlScorer
{
    private readonly object _gate = new();
    private MlModelFile? _model;
    private string _profileName = "default";
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public PersonalMlScorer(string profileName)
    {
        SwitchProfile(profileName);
    }

    public void SwitchProfile(string profileName)
    {
        lock (_gate)
        {
            _profileName = LocalLearningStore.SanitizeProfileName(profileName);
            _model = null;
            _lastWriteUtc = DateTime.MinValue;
        }
        Reload();
    }

    public void Reload()
    {
        string path;
        lock (_gate)
        {
            path = AppSettings.GetMlModelPath(_profileName);
        }

        try
        {
            if (!File.Exists(path))
            {
                lock (_gate)
                {
                    _model = null;
                    _lastWriteUtc = DateTime.MinValue;
                }
                return;
            }

            var model = JsonSerializer.Deserialize<MlModelFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (model is null || model.SchemaVersion != 1 || model.Weights.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                _model = model;
                _lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
        }
        catch
        {
            // A damaged optional model must never break typing.
        }
    }

    public double Score(string prefix, IReadOnlyList<string> contextWords, string candidate)
    {
        MlModelFile? model;
        string profile;
        DateTime knownWrite;
        lock (_gate)
        {
            model = _model;
            profile = _profileName;
            knownWrite = _lastWriteUtc;
        }

        var path = AppSettings.GetMlModelPath(profile);
        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > knownWrite)
            {
                Reload();
                lock (_gate)
                {
                    model = _model;
                }
            }
        }
        catch
        {
            // Ignore file-system races while a new model is being atomically replaced.
        }

        if (model is null)
        {
            return 0;
        }

        var score = model.Intercept;
        foreach (var feature in BuildFeatures(prefix, contextWords, candidate))
        {
            if (model.Weights.TryGetValue(feature, out var weight))
            {
                score += weight;
            }
        }

        return Math.Clamp(score, -4.0, 4.0);
    }

    public MlModelStatus GetStatus()
    {
        lock (_gate)
        {
            if (_model is null)
            {
                return new MlModelStatus(false, 0, 0, null, "ML-модель ещё не обучена.");
            }

            var trainedAt = DateTime.TryParse(_model.TrainedAtUtc, out var parsed)
                ? parsed
                : (DateTime?)null;
            return new MlModelStatus(
                true,
                _model.SampleCount,
                _model.PositiveSamples,
                trainedAt,
                $"ML готова: {_model.SampleCount:N0} примеров.");
        }
    }

    public static IReadOnlyList<string> BuildFeatures(
        string prefix,
        IReadOnlyList<string> contextWords,
        string candidate)
    {
        var normalizedPrefix = LocalLearningStore.NormalizeWord(prefix);
        var normalizedCandidate = LocalLearningStore.NormalizeWord(candidate);
        var context = contextWords
            .Select(LocalLearningStore.NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(5)
            .ToArray();
        var features = new List<string>(12)
        {
            "candidate=" + normalizedCandidate,
            "prefix=" + normalizedPrefix,
            "prefix_len=" + Math.Min(normalizedPrefix.Length, 8),
            "suffix_len=" + SuffixBucket(Math.Max(0, normalizedCandidate.Length - normalizedPrefix.Length))
        };

        if (normalizedPrefix.Length > 0)
        {
            features.Add("prefix_candidate=" + normalizedPrefix + "|" + normalizedCandidate);
        }

        if (context.Length >= 1)
        {
            features.Add("ctx1=" + context[^1]);
            features.Add("ctx1_candidate=" + context[^1] + "|" + normalizedCandidate);
        }
        if (context.Length >= 2)
        {
            features.Add("ctx2=" + context[^2] + "|" + context[^1]);
        }
        if (context.Length >= 3)
        {
            features.Add("ctx3=" + context[^3] + "|" + context[^2] + "|" + context[^1]);
        }
        if (context.Length >= 4)
        {
            features.Add("ctx4=" + context[^4] + "|" + context[^3] + "|" + context[^2] + "|" + context[^1]);
        }

        return features;
    }

    private static string SuffixBucket(int length) => length switch
    {
        <= 1 => "0-1",
        <= 3 => "2-3",
        <= 6 => "4-6",
        <= 10 => "7-10",
        _ => "11+"
    };

    private sealed class MlModelFile
    {
        public int SchemaVersion { get; set; }
        public string Profile { get; set; } = "default";
        public string TrainedAtUtc { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int PositiveSamples { get; set; }
        public double Intercept { get; set; }
        public Dictionary<string, double> Weights { get; set; } = new(StringComparer.Ordinal);
    }
}
