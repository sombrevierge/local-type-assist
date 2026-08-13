using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class SuggestionEngine
{
    private readonly LocalLearningStore _store;
    private readonly MorphologyService _morphology;
    private readonly AppSettings _settings;
    private readonly PersonalMlScorer _mlScorer;

    public SuggestionEngine(
        LocalLearningStore store,
        MorphologyService morphology,
        AppSettings settings,
        PersonalMlScorer mlScorer)
    {
        _store = store;
        _morphology = morphology;
        _settings = settings;
        _mlScorer = mlScorer;
    }

    public IReadOnlyList<SuggestionItem> Suggest(string rawPrefix, IReadOnlyList<string> contextWords, int limit)
    {
        var normalizedPrefix = LocalLearningStore.NormalizeWord(rawPrefix);
        var context = contextWords
            .Select(LocalLearningStore.NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(5)
            .ToArray();

        if (normalizedPrefix.Length == 0 && context.Length == 0)
        {
            return Array.Empty<SuggestionItem>();
        }

        _morphology.EnsureWordsIndexed(_store.GetKnownWordsSnapshot());

        var lemmaContext = _settings.MorphologyEnabled
            ? context.Select(word =>
                {
                    var analysis = _morphology.AnalyzeBest(word);
                    return LocalLearningStore.NormalizeWord(analysis?.Lemma ?? word);
                })
                .ToArray()
            : context;

        var candidateMap = new Dictionary<string, MorphCandidate>(StringComparer.OrdinalIgnoreCase);
        if (normalizedPrefix.Length > 0)
        {
            foreach (var word in _store.GetCandidateWords(normalizedPrefix))
            {
                candidateMap[word] = MorphCandidate.Plain(word);
            }

            if (_settings.MorphologyEnabled)
            {
                foreach (var candidate in _morphology.GetCandidates(normalizedPrefix))
                {
                    candidateMap[candidate.Text] = candidate;
                }
            }

        }

        if (_settings.SemanticSuggestionsEnabled && context.Length > 0)
        {
            foreach (var word in _store.GetContextCandidateWords(context, normalizedPrefix))
            {
                candidateMap[word] = _settings.MorphologyEnabled
                    ? _morphology.AnalyzeBest(word) ?? MorphCandidate.Plain(word)
                    : MorphCandidate.Plain(word);
            }

            if (!context.SequenceEqual(lemmaContext, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var word in _store.GetContextCandidateWords(lemmaContext, normalizedPrefix))
                {
                    candidateMap[word] = _settings.MorphologyEnabled
                        ? _morphology.AnalyzeBest(word) ?? MorphCandidate.Plain(word)
                        : MorphCandidate.Plain(word);
                }
            }
        }

        if (candidateMap.Count == 0)
        {
            return Array.Empty<SuggestionItem>();
        }

        var previous = context.LastOrDefault() ?? string.Empty;
        var beforePrevious = context.Length >= 2 ? context[^2] : string.Empty;
        var thirdPrevious = context.Length >= 3 ? context[^3] : string.Empty;
        var fourthPrevious = context.Length >= 4 ? context[^4] : string.Empty;
        var lemmaPrevious = lemmaContext.LastOrDefault() ?? string.Empty;
        var lemmaBeforePrevious = lemmaContext.Length >= 2 ? lemmaContext[^2] : string.Empty;
        var lemmaThirdPrevious = lemmaContext.Length >= 3 ? lemmaContext[^3] : string.Empty;
        var lemmaFourthPrevious = lemmaContext.Length >= 4 ? lemmaContext[^4] : string.Empty;
        var now = DateTime.UtcNow;
        var nextWordMode = normalizedPrefix.Length == 0;
        var mlStatus = _settings.PersonalMlEnabled ? _mlScorer.GetStatus() : null;
        var mlScale = mlStatus is { Available: true }
            ? 4.0 + Math.Min(1.0, mlStatus.SampleCount / 500.0) * 5.0
            : 0.0;

        var scored = new List<SuggestionItem>(candidateMap.Count);
        foreach (var candidate in candidateMap.Values)
        {
            var word = LocalLearningStore.NormalizeWord(candidate.Text);
            if (word.Length == 0 ||
                _store.IsBlocked(word) ||
                normalizedPrefix.Length > 0 &&
                (word.Length <= normalizedPrefix.Length ||
                 !word.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var stat = _store.GetWordStat(word);
            var signals = _store.GetWordSignals(word);
            var seedRank = Math.Min(_store.GetSeedRank(word), _store.GetSeedRank(candidate.Lemma));
            var seedScore = seedRank == int.MaxValue
                ? 0
                : Math.Max(0, 8.4 - Math.Log10(seedRank + 3) * 2.05);
            var typedScore = Math.Log(1 + stat.TypedCount) * 2.8;
            var trainingScore = Math.Log(1 + stat.TrainingCount) * 11.5;
            var corpusScore = Math.Log(1 + stat.CorpusCount) * 4.2;
            var acceptedScore = Math.Log(1 + stat.AcceptedCount) * 5.8;
            var correctedPenalty = Math.Log(1 + stat.CorrectedCount) * 4.5;

            var lemma = LocalLearningStore.NormalizeWord(candidate.Lemma);
            if (lemma.Length == 0)
            {
                lemma = word;
            }

            var personalBigram = previous.Length == 0 ? 0 : _store.GetBigramCount(previous, word);
            var personalTrigram = beforePrevious.Length == 0
                ? 0
                : _store.GetTrigramCount(beforePrevious, previous, word);
            var personalFourgram = thirdPrevious.Length == 0
                ? 0
                : _store.GetFourgramCount(thirdPrevious, beforePrevious, previous, word);
            var personalFivegram = fourthPrevious.Length == 0
                ? 0
                : _store.GetFivegramCount(fourthPrevious, thirdPrevious, beforePrevious, previous, word);

            var lemmaPersonalBigram = lemmaPrevious.Length == 0 ? 0 : _store.GetBigramCount(lemmaPrevious, lemma);
            var lemmaPersonalTrigram = lemmaBeforePrevious.Length == 0
                ? 0
                : _store.GetTrigramCount(lemmaBeforePrevious, lemmaPrevious, lemma);
            var lemmaPersonalFourgram = lemmaThirdPrevious.Length == 0
                ? 0
                : _store.GetFourgramCount(lemmaThirdPrevious, lemmaBeforePrevious, lemmaPrevious, lemma);
            var lemmaPersonalFivegram = lemmaFourthPrevious.Length == 0
                ? 0
                : _store.GetFivegramCount(
                    lemmaFourthPrevious,
                    lemmaThirdPrevious,
                    lemmaBeforePrevious,
                    lemmaPrevious,
                    lemma);

            var seedBigram = previous.Length == 0 ? 0 : _store.GetSeedBigramCount(previous, word);
            var seedTrigram = beforePrevious.Length == 0
                ? 0
                : _store.GetSeedTrigramCount(beforePrevious, previous, word);
            var seedFourgram = thirdPrevious.Length == 0
                ? 0
                : _store.GetSeedFourgramCount(thirdPrevious, beforePrevious, previous, word);
            var lemmaSeedBigram = lemmaPrevious.Length == 0 ? 0 : _store.GetSeedBigramCount(lemmaPrevious, lemma);
            var lemmaSeedTrigram = lemmaBeforePrevious.Length == 0
                ? 0
                : _store.GetSeedTrigramCount(lemmaBeforePrevious, lemmaPrevious, lemma);
            var lemmaSeedFourgram = lemmaThirdPrevious.Length == 0
                ? 0
                : _store.GetSeedFourgramCount(lemmaThirdPrevious, lemmaBeforePrevious, lemmaPrevious, lemma);

            // Adaptive interpolated n-gram scoring: longer personal contexts dominate,
            // while shorter contexts and the starter corpus remain useful as backoff.
            var contextScore = Math.Log(1 + personalBigram) * 6.8 +
                               Math.Log(1 + personalTrigram) * 10.8 +
                               Math.Log(1 + personalFourgram) * 15.8 +
                               Math.Log(1 + personalFivegram) * 22.0 +
                               Math.Log(1 + lemmaPersonalBigram) * 3.2 +
                               Math.Log(1 + lemmaPersonalTrigram) * 5.2 +
                               Math.Log(1 + lemmaPersonalFourgram) * 7.4 +
                               Math.Log(1 + lemmaPersonalFivegram) * 10.5 +
                               Math.Log(1 + Math.Max(seedBigram, lemmaSeedBigram)) * 1.35 +
                               Math.Log(1 + Math.Max(seedTrigram, lemmaSeedTrigram)) * 2.2 +
                               Math.Log(1 + Math.Max(seedFourgram, lemmaSeedFourgram)) * 3.3;
            var prefixChoiceCount = normalizedPrefix.Length == 0
                ? 0
                : _store.GetPrefixChoiceCount(normalizedPrefix, word);
            var contextPrefixChoiceCount = normalizedPrefix.Length == 0
                ? 0
                : _store.GetContextPrefixChoiceCount(normalizedPrefix, context, word);
            var prefixChoiceScore = Math.Log(1 + prefixChoiceCount) * 8.0 +
                                    Math.Log(1 + contextPrefixChoiceCount) * 18.0;
            var rejectedChoiceCount = normalizedPrefix.Length == 0
                ? 0
                : _store.GetRejectedChoiceCount(normalizedPrefix, context, word);
            var rejectedChoicePenalty = Math.Log(1 + rejectedChoiceCount) * 14.0;
            var ageDays = stat.LastUsedUtc == DateTime.MinValue
                ? 3650
                : Math.Max(0, (now - stat.LastUsedUtc).TotalDays);
            var recencyScore = stat.LastUsedUtc == DateTime.MinValue
                ? 0
                : 2.1 / (1 + ageDays / 12.0);
            var morphologyScore = _settings.MorphologyEnabled
                ? AgreementScore(
                    candidate,
                    context,
                    personalBigram + personalTrigram + personalFourgram + personalFivegram + contextPrefixChoiceCount)
                : 0;
            var completionLength = Math.Max(0, word.Length - normalizedPrefix.Length);
            var lengthPenalty = Math.Max(0, completionLength - 14) * 0.07;

            if (!nextWordMode && normalizedPrefix.Length == 1 &&
                seedRank > 650 &&
                stat.TypedCount == 0 &&
                stat.AcceptedCount == 0 &&
                personalBigram == 0 &&
                personalTrigram == 0 &&
                personalFourgram == 0 &&
                personalFivegram == 0 &&
                lemmaPersonalBigram == 0 &&
                lemmaPersonalTrigram == 0 &&
                lemmaPersonalFourgram == 0 &&
                lemmaPersonalFivegram == 0 &&
                seedBigram == 0 &&
                seedTrigram == 0 &&
                seedFourgram == 0 &&
                lemmaSeedBigram == 0 &&
                lemmaSeedTrigram == 0 &&
                lemmaSeedFourgram == 0 &&
                prefixChoiceCount == 0 &&
                contextPrefixChoiceCount == 0 &&
                stat.TrainingCount == 0 &&
                stat.CorpusCount == 0)
            {
                continue;
            }

            if (nextWordMode && contextScore <= 0 && stat.TypedCount == 0)
            {
                continue;
            }

            var shortWordBonus = word.Length <= 3 && seedRank <= 90 ? 1.5 : 0;
            var contextPresenceBonus = nextWordMode && contextScore > 0 ? 2.5 : 0;
            var confirmedBonus = Math.Log(1 + signals.ConfirmedCount) * 4.8;
            var correctionTargetBonus = Math.Log(1 + signals.CorrectionTargetCount) * 9.5;
            var trustedBonus = signals.Trusted ? 18.0 : 0.0;
            var correctedAwayPenalty = Math.Log(1 + signals.CorrectedAwayCount) * 12.5;
            var deletionPenalty = Math.Log(1 + signals.DeletedCount) * 4.0;
            var mlScore = _settings.PersonalMlEnabled
                ? _mlScorer.Score(normalizedPrefix, context, word)
                : 0.0;
            // ML starts as a supporting signal and becomes one of the strongest personal
            // ranking factors as the profile accumulates labelled examples.
            var mlContribution = mlScore * mlScale;
            var score = seedScore + typedScore + trainingScore + corpusScore + acceptedScore +
                        contextScore + prefixChoiceScore + recencyScore + morphologyScore +
                        shortWordBonus + contextPresenceBonus + confirmedBonus + correctionTargetBonus +
                        trustedBonus + mlContribution -
                        correctedPenalty - rejectedChoicePenalty - correctedAwayPenalty - deletionPenalty - lengthPenalty;

            scored.Add(new SuggestionItem(
                ApplyCase(word, rawPrefix),
                score,
                stat.AcceptedCount,
                Math.Max(
                    personalBigram + personalTrigram + personalFourgram + personalFivegram + contextPrefixChoiceCount,
                    Math.Max(
                        lemmaPersonalBigram + lemmaPersonalTrigram + lemmaPersonalFourgram + lemmaPersonalFivegram,
                        Math.Max(
                            seedBigram + seedTrigram + seedFourgram,
                            lemmaSeedBigram + lemmaSeedTrigram + lemmaSeedFourgram))),
                prefixChoiceCount,
                Math.Abs(morphologyScore) > 0.2,
                false,
                0,
                mlScore));
        }

        return scored
            .GroupBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Text.Length)
            .ThenBy(x => x.Text, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 50))
            .ToArray();
    }

    private double AgreementScore(
        MorphCandidate candidate,
        IReadOnlyList<string> context,
        int personalContextCount)
    {
        if (context.Count == 0)
        {
            return 0;
        }

        var score = 0.0;
        var previous = context[^1];
        var pronounSubject = FindRecentPronounSubject(context);
        var nominalSubject = string.IsNullOrEmpty(pronounSubject)
            ? FindRecentNominalSubject(context)
            : null;
        var desiredGender = ResolveDesiredGender(pronounSubject, nominalSubject);
        var desiredPerson = !string.IsNullOrEmpty(pronounSubject)
            ? ExpectedPerson(pronounSubject)
            : nominalSubject is null ? string.Empty : "Third";
        var desiredNumber = !string.IsNullOrEmpty(pronounSubject)
            ? ExpectedNumber(pronounSubject)
            : HasTagValue(nominalSubject?.Number) ? nominalSubject!.Number : string.Empty;
        var isVerb = IsVerbLike(candidate);

        if (isVerb && HasTagValue(candidate.Person))
        {
            if (!string.IsNullOrEmpty(desiredPerson))
            {
                score += EqualsTag(candidate.Person, desiredPerson) ? 8.5 : -18.0;
            }
            else if ((ContainsTag(candidate.Person, "First") || ContainsTag(candidate.Person, "Second")) &&
                     personalContextCount == 0)
            {
                // Without an explicit subject, first/second-person verb forms should not
                // win only because they are frequent in the starter dictionary.
                score -= 13.5;
            }
        }

        if (isVerb && HasTagValue(candidate.Number) && !string.IsNullOrEmpty(desiredNumber))
        {
            score += EqualsTag(candidate.Number, desiredNumber) ? 4.5 : -7.0;
        }

        if (!string.IsNullOrEmpty(desiredGender) &&
            (ContainsTag(candidate.Tense, "Past") ||
             ContainsTag(candidate.Pos, "Adjective") ||
             ContainsTag(candidate.Pos, "Participle")))
        {
            if (HasTagValue(candidate.Gender))
            {
                score += EqualsTag(candidate.Gender, desiredGender) ? 6.8 : -10.0;
            }
        }

        var expectedCase = ExpectedCaseAfterPreposition(previous);
        if (!string.IsNullOrEmpty(expectedCase) && HasTagValue(candidate.Case))
        {
            score += EqualsTag(candidate.Case, expectedCase) ? 4.8 : -3.5;
        }

        if (RequiresInfinitive(previous))
        {
            score += IsInfinitive(candidate) ? 7.5 : isVerb ? -5.0 : 0;
        }

        if (previous is "очень" or "слишком" or "довольно")
        {
            if (ContainsTag(candidate.Pos, "Adverb") || ContainsTag(candidate.Pos, "Adjective"))
            {
                score += 4.2;
            }
        }

        var previousMorph = _morphology.AnalyzeBest(previous);
        if (previousMorph is not null &&
            (ContainsTag(previousMorph.Pos, "Adjective") || ContainsTag(previousMorph.Pos, "Pronoun")))
        {
            if (HasTagValue(previousMorph.Gender) && HasTagValue(candidate.Gender))
            {
                score += EqualsTag(candidate.Gender, previousMorph.Gender) ? 2.8 : -1.8;
            }

            if (HasTagValue(previousMorph.Number) && HasTagValue(candidate.Number))
            {
                score += EqualsTag(candidate.Number, previousMorph.Number) ? 2.6 : -1.7;
            }

            if (HasTagValue(previousMorph.Case) && HasTagValue(candidate.Case))
            {
                score += EqualsTag(candidate.Case, previousMorph.Case) ? 2.4 : -1.5;
            }
        }

        return score;
    }

    private string ResolveDesiredGender(string pronounSubject, MorphCandidate? nominalSubject)
    {
        if (!string.IsNullOrEmpty(pronounSubject))
        {
            return pronounSubject switch
            {
                "она" => "Feminine",
                "он" => "Masculine",
                "оно" => "Neuter",
                "я" => _settings.GenderPreference == "Auto"
                    ? _store.GetLearnedGender()
                    : _settings.GenderPreference,
                _ => string.Empty
            };
        }

        return HasTagValue(nominalSubject?.Gender) ? nominalSubject!.Gender : string.Empty;
    }

    private static string ExpectedPerson(string subject) => subject switch
    {
        "я" or "мы" => "First",
        "ты" or "вы" => "Second",
        "он" or "она" or "оно" or "они" => "Third",
        _ => string.Empty
    };

    private static string ExpectedNumber(string subject) => subject switch
    {
        "я" or "ты" or "он" or "она" or "оно" => "Singular",
        "мы" or "вы" or "они" => "Plural",
        _ => string.Empty
    };

    private static string FindRecentPronounSubject(IReadOnlyList<string> context)
    {
        for (var i = context.Count - 1; i >= 0; i--)
        {
            if (context[i] is "я" or "ты" or "он" or "она" or "оно" or "мы" or "вы" or "они")
            {
                return context[i];
            }
        }

        return string.Empty;
    }

    private MorphCandidate? FindRecentNominalSubject(IReadOnlyList<string> context)
    {
        for (var i = context.Count - 1; i >= 0; i--)
        {
            var analyzed = _morphology.AnalyzeBest(context[i]);
            if (analyzed is not null &&
                (ContainsTag(analyzed.Pos, "Noun") || ContainsTag(analyzed.Pos, "Pronoun")) &&
                (!HasTagValue(analyzed.Case) || ContainsTag(analyzed.Case, "Nominative")))
            {
                return analyzed;
            }
        }

        return null;
    }

    private static string ExpectedCaseAfterPreposition(string previous) => previous switch
    {
        "без" or "для" or "до" or "из" or "около" or "от" or "после" or "у" => "Genitive",
        "к" or "по" => "Dative",
        "о" or "об" or "обо" or "при" => "Prepositional",
        "с" or "со" or "между" or "над" or "под" or "перед" => "Instrumental",
        _ => string.Empty
    };

    private static bool RequiresInfinitive(string previous) => previous is
        "хочу" or "хочешь" or "хотим" or "хотите" or
        "могу" or "можешь" or "может" or "можем" or "можете" or "могут" or
        "буду" or "будешь" or "будет" or "будем" or "будете" or "будут" or
        "нужно" or "надо" or "можно" or "нельзя" or "стоит" or
        "решил" or "решила" or "начала" or "начал" or "пытаюсь" or "попробую";

    private static bool IsVerbLike(MorphCandidate candidate) =>
        ContainsTag(candidate.Pos, "Verb") || ContainsTag(candidate.Pos, "Participle");

    private static bool IsInfinitive(MorphCandidate candidate) =>
        ContainsTag(candidate.Tense, "Infinitive");

    private static bool HasTagValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "None", StringComparison.OrdinalIgnoreCase);

    private static bool EqualsTag(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase) ||
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTag(string value, string expected) =>
        !string.IsNullOrEmpty(value) && value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string ApplyCase(string word, string prefix)
    {
        if (prefix.Length == 0)
        {
            return word;
        }

        if (prefix.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return word.ToUpperInvariant();
        }

        if (char.IsUpper(prefix[0]))
        {
            return char.ToUpperInvariant(word[0]) + word[1..];
        }

        return word;
    }
}
