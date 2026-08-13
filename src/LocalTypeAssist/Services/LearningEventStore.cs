using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalTypeAssist.Models;

namespace LocalTypeAssist.Services;

public sealed class LearningEventStore : IDisposable
{
    private readonly object _gate = new();
    private string _profileName = "default";

    public static string DatabasePath => Path.Combine(AppSettings.DataRoot, "learning-v7.sqlite3");

    public LearningEventStore()
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        EnsureSchema();
    }

    public void SwitchProfile(string profileName)
    {
        lock (_gate)
        {
            _profileName = LocalLearningStore.SanitizeProfileName(profileName);
        }
    }

    public void RecordEvent(
        LearningEventType type,
        string word,
        IReadOnlyList<string> contextWords,
        string prefix = "",
        string suggestion = "",
        string originalWord = "",
        int weight = 1)
    {
        word = LocalLearningStore.NormalizeWord(word);
        prefix = LocalLearningStore.NormalizeWord(prefix);
        suggestion = LocalLearningStore.NormalizeWord(suggestion);
        originalWord = LocalLearningStore.NormalizeWord(originalWord);
        var context = contextWords
            .Select(LocalLearningStore.NormalizeWord)
            .Where(x => x.Length > 0)
            .TakeLast(5)
            .ToArray();

        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO learning_events
                (profile, utc, event_type, word, original_word, prefix, context_json, suggestion, weight)
            VALUES
                ($profile, $utc, $eventType, $word, $originalWord, $prefix, $context, $suggestion, $weight);
            """;
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$eventType", EventName(type));
        command.Parameters.AddWithValue("$word", word);
        command.Parameters.AddWithValue("$originalWord", originalWord);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$context", JsonSerializer.Serialize(context));
        command.Parameters.AddWithValue("$suggestion", suggestion);
        command.Parameters.AddWithValue("$weight", Math.Clamp(weight, 1, 20));
        command.ExecuteNonQuery();
    }

    public void RecordCorrection(
        string originalWord,
        string correctedWord,
        IReadOnlyList<string> contextWords,
        int weight = 1)
    {
        var original = LocalLearningStore.NormalizeWord(originalWord);
        var corrected = LocalLearningStore.NormalizeWord(correctedWord);
        if (original.Length == 0 || corrected.Length == 0 ||
            string.Equals(original, corrected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RecordEvent(LearningEventType.CorrectedAway, original, contextWords, originalWord: original, weight: weight);
        RecordEvent(LearningEventType.CorrectionTarget, corrected, contextWords, originalWord: original, weight: weight);

        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO corrections (profile, original_word, corrected_word, count, last_seen_utc)
            VALUES ($profile, $original, $corrected, $weight, $utc)
            ON CONFLICT(profile, original_word, corrected_word)
            DO UPDATE SET
                count = corrections.count + excluded.count,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$original", original);
        command.Parameters.AddWithValue("$corrected", corrected);
        command.Parameters.AddWithValue("$weight", Math.Clamp(weight, 1, 20));
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, LearningWordSignals> GetWordSignals()
    {
        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        var result = new Dictionary<string, LearningWordSignals>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT word,
                       SUM(CASE WHEN event_type = 'typed_clean' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'training_observation' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'accepted_suggestion' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'rejected_suggestion' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'corrected_away' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'correction_target' THEN weight ELSE 0 END),
                       SUM(CASE WHEN event_type = 'deleted_token' THEN weight ELSE 0 END)
                FROM learning_events
                WHERE profile = $profile
                  AND word <> ''
                  AND event_type IN (
                      'typed_clean', 'training_observation', 'accepted_suggestion',
                      'rejected_suggestion', 'corrected_away', 'correction_target', 'deleted_token'
                  )
                GROUP BY word;
                """;
            command.Parameters.AddWithValue("$profile", profile);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var word = reader.GetString(0);
                result[word] = new LearningWordSignals
                {
                    ConfirmedCount = Convert.ToInt32(reader.GetInt64(1)),
                    TrainingObservationCount = Convert.ToInt32(reader.GetInt64(2)),
                    AcceptedSuggestionCount = Convert.ToInt32(reader.GetInt64(3)),
                    RejectedSuggestionCount = Convert.ToInt32(reader.GetInt64(4)),
                    CorrectedAwayCount = Convert.ToInt32(reader.GetInt64(5)),
                    CorrectionTargetCount = Convert.ToInt32(reader.GetInt64(6)),
                    DeletedCount = Convert.ToInt32(reader.GetInt64(7))
                };
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT word, trusted, blocked FROM word_flags WHERE profile = $profile;";
            command.Parameters.AddWithValue("$profile", profile);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var word = reader.GetString(0);
                if (!result.TryGetValue(word, out var signals))
                {
                    signals = new LearningWordSignals();
                    result[word] = signals;
                }

                signals.Trusted = Convert.ToInt32(reader.GetInt64(1)) != 0;
                signals.Blocked = Convert.ToInt32(reader.GetInt64(2)) != 0;
            }
        }

        return result;
    }

    public IReadOnlyList<LearningCorrectionView> GetCorrections()
    {
        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        var result = new List<LearningCorrectionView>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT original_word, corrected_word, count, last_seen_utc
            FROM corrections
            WHERE profile = $profile
            ORDER BY count DESC, last_seen_utc DESC
            LIMIT 5000;
            """;
        command.Parameters.AddWithValue("$profile", profile);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lastSeen = DateTime.TryParse(reader.GetString(3), out var parsed)
                ? parsed
                : DateTime.MinValue;
            result.Add(new LearningCorrectionView(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToInt32(reader.GetInt64(2)),
                lastSeen));
        }

        return result;
    }

    public int GetEventCount()
    {
        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM learning_events WHERE profile = $profile;";
        command.Parameters.AddWithValue("$profile", profile);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public void SetWordFlags(string word, bool? trusted = null, bool? blocked = null)
    {
        word = LocalLearningStore.NormalizeWord(word);
        if (word.Length == 0)
        {
            return;
        }

        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO word_flags (profile, word, trusted, blocked)
            VALUES ($profile, $word, $trusted, $blocked)
            ON CONFLICT(profile, word)
            DO UPDATE SET
                trusted = CASE WHEN $setTrusted = 1 THEN $trusted ELSE word_flags.trusted END,
                blocked = CASE WHEN $setBlocked = 1 THEN $blocked ELSE word_flags.blocked END;
            """;
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$word", word);
        command.Parameters.AddWithValue("$trusted", trusted == true ? 1 : 0);
        command.Parameters.AddWithValue("$blocked", blocked == true ? 1 : 0);
        command.Parameters.AddWithValue("$setTrusted", trusted.HasValue ? 1 : 0);
        command.Parameters.AddWithValue("$setBlocked", blocked.HasValue ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void DeleteWord(string word) => DeleteWords(new[] { word });

    public void DeleteWords(IEnumerable<string> words)
    {
        var normalizedWords = words
            .Select(LocalLearningStore.NormalizeWord)
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedWords.Length == 0)
        {
            return;
        }

        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var word in normalizedWords)
        {
            foreach (var sql in new[]
                     {
                         // Removing a learned token also removes events where it was part of
                         // the recorded context, otherwise stale typo-contexts could survive
                         // a manual cleanup and influence the next ML retrain.
                         "DELETE FROM learning_events WHERE profile = $profile AND (word = $word OR original_word = $word OR suggestion = $word OR instr(context_json, $contextNeedle) > 0);",
                         "DELETE FROM corrections WHERE profile = $profile AND (original_word = $word OR corrected_word = $word);",
                         "DELETE FROM word_flags WHERE profile = $profile AND word = $word;"
                     })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$profile", profile);
                command.Parameters.AddWithValue("$word", word);
                if (sql.Contains("$contextNeedle", StringComparison.Ordinal))
                {
                    command.Parameters.AddWithValue("$contextNeedle", JsonSerializer.Serialize(word));
                }
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public void ClearProfile()
    {
        string profile;
        lock (_gate)
        {
            profile = _profileName;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "learning_events", "corrections", "word_flags" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE profile = $profile;";
            command.Parameters.AddWithValue("$profile", profile);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS learning_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile TEXT NOT NULL,
                utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                word TEXT NOT NULL DEFAULT '',
                original_word TEXT NOT NULL DEFAULT '',
                prefix TEXT NOT NULL DEFAULT '',
                context_json TEXT NOT NULL DEFAULT '[]',
                suggestion TEXT NOT NULL DEFAULT '',
                weight INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS ix_learning_events_profile_word
                ON learning_events(profile, word);
            CREATE INDEX IF NOT EXISTS ix_learning_events_profile_type
                ON learning_events(profile, event_type);

            CREATE TABLE IF NOT EXISTS corrections (
                profile TEXT NOT NULL,
                original_word TEXT NOT NULL,
                corrected_word TEXT NOT NULL,
                count INTEGER NOT NULL DEFAULT 1,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY(profile, original_word, corrected_word)
            );

            CREATE TABLE IF NOT EXISTS word_flags (
                profile TEXT NOT NULL,
                word TEXT NOT NULL,
                trusted INTEGER NOT NULL DEFAULT 0,
                blocked INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(profile, word)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static string EventName(LearningEventType type) => type switch
    {
        LearningEventType.TypedClean => "typed_clean",
        LearningEventType.TrainingObservation => "training_observation",
        LearningEventType.AcceptedSuggestion => "accepted_suggestion",
        LearningEventType.RejectedSuggestion => "rejected_suggestion",
        LearningEventType.CorrectedAway => "corrected_away",
        LearningEventType.CorrectionTarget => "correction_target",
        LearningEventType.DeletedToken => "deleted_token",
        LearningEventType.DismissedPopup => "dismissed_popup",
        _ => "typed_clean"
    };

    private static SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(AppSettings.DataRoot);
        var connection = new SqliteConnection($"Data Source={DatabasePath};Cache=Shared;Default Timeout=5");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
