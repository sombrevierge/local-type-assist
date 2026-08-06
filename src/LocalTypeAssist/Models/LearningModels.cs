namespace LocalTypeAssist.Models;

public sealed class WordStat
{
    public int TypedCount { get; set; }
    public int AcceptedCount { get; set; }
    public int CorrectedCount { get; set; }
    public int TrainingCount { get; set; }
    public int CorpusCount { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProfileData
{
    public Dictionary<string, WordStat> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Bigrams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Trigrams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Fourgrams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Fivegrams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> PrefixChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, int>> ContextPrefixChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> RejectedChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> GenderSignals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed record MorphCandidate(
    string Text,
    string Lemma,
    string Pos,
    string Gender,
    string Number,
    string Case,
    string Tense,
    string Person)
{
    public static MorphCandidate Plain(string text) =>
        new(text, text, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

public sealed record SuggestionItem(
    string Text,
    double Score,
    int AcceptedCount,
    int ContextCount,
    int PrefixChoiceCount,
    bool MorphologyMatched,
    bool IsCorrection,
    int EditDistance);

public sealed record PendingAcceptance(
    string SuggestedWord,
    string Prefix,
    IReadOnlyList<string> ContextWords);
