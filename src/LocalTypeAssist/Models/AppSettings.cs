using System.Text.Json;

namespace LocalTypeAssist.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 11;
    public bool Enabled { get; set; } = true;
    public int MinPrefixLength { get; set; } = 1;
    public int MaxSuggestions { get; set; } = 5;
    public bool LearnTypedWords { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool MorphologyEnabled { get; set; } = true;
    public bool SemanticSuggestionsEnabled { get; set; } = true;
    public bool PersonalMlEnabled { get; set; } = true;
    public bool AutoCompleteShortWords { get; set; }
    public bool ShiftCancelsCompletion { get; set; } = true;
    public string CompletionMode { get; set; } = "Immediate";
    public string GenderPreference { get; set; } = "Auto";
    public int AutoCompleteDelayMs { get; set; } = 600;
    public int AutoCompleteMinPrefix { get; set; } = 3;
    public string ActiveProfile { get; set; } = "default";

    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalTypeAssist");

    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static string ProfilesRoot => Path.Combine(DataRoot, "profiles");
    public static string GetMlModelPath(string profileName) =>
        Path.Combine(ProfilesRoot, LocalTypeAssist.Services.LocalLearningStore.SanitizeProfileName(profileName) + ".ml.json");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ProfilesRoot);

        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                               ?? new AppSettings();
                if (settings.SchemaVersion < 8)
                {
                    settings.SchemaVersion = 8;
                    settings.MinPrefixLength = 1;
                    settings.MaxSuggestions = 5;
                    settings.MorphologyEnabled = true;
                    settings.SemanticSuggestionsEnabled = true;
                    settings.ShiftCancelsCompletion = true;
                    settings.CompletionMode = "Immediate";
                    settings.AutoCompleteMinPrefix = 3;
                    settings.AutoCompleteDelayMs = 600;
                    settings.AutoCompleteShortWords = false;
                }

                if (settings.SchemaVersion < 9)
                {
                    settings.SchemaVersion = 9;
                    if (settings.CompletionMode is "Inline" or "Smart")
                    {
                        settings.CompletionMode = "Soft";
                    }
                }

                if (settings.SchemaVersion < 10)
                {
                    settings.SchemaVersion = 10;
                }

                if (settings.SchemaVersion < 11)
                {
                    settings.SchemaVersion = 11;
                    settings.PersonalMlEnabled = true;
                }

                settings.Normalize();
                return settings;
            }
        }
        catch
        {
            // A damaged settings file should not prevent the app from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DataRoot);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, SettingsPath, true);
    }

    private void Normalize()
    {
        MinPrefixLength = Math.Clamp(MinPrefixLength, 1, 6);
        MaxSuggestions = Math.Clamp(MaxSuggestions, 1, 5);
        AutoCompleteDelayMs = Math.Clamp(AutoCompleteDelayMs, 250, 2000);
        AutoCompleteMinPrefix = Math.Clamp(AutoCompleteMinPrefix, 1, 12);
        CompletionMode = CompletionMode switch
        {
            "Soft" => "Soft",
            "Space" => "Space",
            "Training" => "Training",
            "Inline" or "Smart" => "Soft",
            "Immediate" or "Aggressive" => "Immediate",
            _ => "Immediate"
        };
        if (CompletionMode == "Training")
        {
            LearnTypedWords = true;
        }
        GenderPreference = GenderPreference is "Auto" or "Feminine" or "Masculine" or "Neuter"
            ? GenderPreference
            : "Auto";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
