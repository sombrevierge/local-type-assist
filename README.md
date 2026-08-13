# Local Type Assist

Local Type Assist is a local-first autocomplete system for Windows. It works across browser chats, email, messengers, notes, and regular Windows text fields, while keeping typed text, learned vocabulary, context, and personal models on the user's computer.

Current public version: **7.0.4**.

## What it does

- provides global autocomplete in Windows text fields;
- supports suggestion-only, complete-on-Space, idle autocomplete, and learning-only modes;
- learns from words you type, suggestions you accept, words you reject, and corrections you make;
- ranks candidates using personal prefix mappings, contextual n-grams, Russian morphology, and optional local ML scoring;
- stores personal learning data in a local SQLite event store;
- includes a learning library where learned words can be reviewed, trusted, blocked, or deleted;
- keeps separate local profiles;
- supports local corpus import from TXT, MD, CSV, LOG, and JSON;
- avoids password fields and known password-manager contexts;
- does not send typed text to a cloud service and contains no telemetry pipeline.

## Learning system

Version 7 introduces a more explicit learning pipeline instead of treating every typed token as equally trustworthy.

The application records different kinds of signals, including:

- cleanly typed words;
- accepted suggestions;
- learning-only observations;
- rejected suggestions;
- deleted tokens;
- corrected words;
- correction pairs;
- contextual prefix-to-word observations;
- n-grams up to five words.

New observations can remain provisional until later typing confirms them. Corrections and deletions can reduce the weight of noisy entries instead of permanently teaching every typo.

### Learning Library

Open the **Learning Library** from the main window or tray menu to inspect accumulated personal data.

The library lets you:

- search learned words;
- review entries that are likely to need attention;
- mark words as trusted;
- block words from suggestions;
- remove individual entries;
- clean likely mistakes;
- inspect correction statistics;
- retrain the optional personal ML model.

Personal learning data is stored outside the repository under:

```text
%LOCALAPPDATA%\LocalTypeAssist
```

## Personal ML reranker

Local Type Assist can optionally train a lightweight personal reranking model using Python and scikit-learn.

The ML layer does **not** replace the core autocomplete engine. The C# application still generates candidates using vocabulary, personal statistics, morphology, and context. The trained model adds another score that helps reorder those candidates according to the user's own history.

Typical features include:

- current prefix;
- candidate word;
- prefix + candidate combinations;
- completion length;
- previous words;
- recent context combinations;
- positive and negative interaction labels.

Python is only needed for model training. Normal per-keystroke inference remains inside the desktop application using exported local model data.

Set up the ML environment once:

```powershell
.\scripts\setup-ml.ps1
```

Then use **Learning Library -> Retrain ML** when enough examples have accumulated.

## Completion modes

| Mode | Behaviour |
|---|---|
| **Suggestions** | Shows candidates but inserts only when explicitly selected |
| **Complete on Space** | `Space` accepts the selected completion; `Shift + Space` keeps the typed word unchanged |
| **Idle autocomplete** | Inserts the best candidate after the configured typing pause |
| **Learning only** | Shows no suggestions and performs no completion; only observes typing and corrections |

## Controls

| Action | Result |
|---|---|
| `Up` / `Down` | Select a suggestion |
| `Tab` / `Right Arrow` | Insert the selected suggestion |
| `Space` | Accept a suggestion in **Complete on Space** mode |
| `Shift + Space` | Keep the typed word unchanged in **Complete on Space** mode |
| `Esc` | Hide suggestions for the current word |
| popup `x` | Hide suggestions for the current word |
| `Ctrl + Alt + Space` | Enable or pause Local Type Assist |

Closing the suggestion popup is neutral feedback: it hides the UI for the current word without automatically treating the visible candidate as a bad suggestion.

## Russian morphology and context

The autocomplete engine combines several local signals:

1. confirmed personal usage;
2. exact personal context and prefix mappings;
3. correction history and rejection history;
4. optional personal ML score;
5. multi-word context;
6. Russian morphology;
7. built-in frequency vocabulary.

Russian inflection and morphology are handled locally through the Nestor package. The built-in seed vocabulary and phrase resources are bundled with the application; attribution is documented separately.

## Privacy

Local Type Assist is designed as a local desktop tool.

- Typed text is not uploaded by the application.
- Learned words and profiles stay under `%LOCALAPPDATA%\LocalTypeAssist`.
- Imported corpora remain local.
- Personal SQLite databases and generated ML models are not part of the Git repository.
- Password fields and selected sensitive contexts are excluded from learning and suggestions.

See the source before using the application with sensitive data; global keyboard hooks and accessibility APIs necessarily operate close to user input.

## Requirements

For running a published build:

- Windows 10 or Windows 11 x64.

For building from source:

- Windows 10 or Windows 11 x64;
- .NET 10 SDK x64;
- access to the `nuget.org` package source.

For optional personal ML training:

- Python supported by the setup script;
- Python packages installed into the dedicated local virtual environment by `setup-ml.ps1`.

## Build from source

```powershell
git clone https://github.com/sombrevierge/local-type-assist.git
cd local-type-assist
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\build-release.ps1
```

Run the application:

```powershell
.\dist\LocalTypeAssist.exe
```

Keep all files in the `dist` directory together. Runtime dependencies are published beside the executable.

## Development

```powershell
.\scripts\run-dev.ps1
```

The solution file is `LocalTypeAssist.sln`; the WPF application lives under `src/LocalTypeAssist`.

Useful scripts:

```text
scripts/build-release.ps1   release build
scripts/run-dev.ps1         development run
scripts/setup-ml.ps1        create/update the personal ML environment
scripts/open-log.ps1        open the local application log
scripts/reset-all-data.ps1  reset local application data
```

Runtime logs are stored under:

```text
%LOCALAPPDATA%\LocalTypeAssist\logs
```

## Architecture

The project is intentionally split into two layers:

- **C# / WPF**: Windows UI, global keyboard hook, caret detection, candidate generation, morphology, learning store, SQLite event history, and real-time scoring;
- **Python (optional)**: offline/local training of the personal reranker.

The typing path does not require a Python process for every keypress.

More details are available in [docs/architecture.md](docs/architecture.md).

## Repository structure

```text
src/LocalTypeAssist/             WPF application
src/LocalTypeAssist/Services/    autocomplete, learning, ML and Windows integration
src/LocalTypeAssist/Models/      settings and learning models
src/LocalTypeAssist/Resources/   seed vocabulary, phrases and ML training resources
scripts/                         build, development, diagnostics and ML setup
docs/                            architecture and behaviour documentation
```

## Documentation

- [Architecture](docs/architecture.md)
- [Behaviour test scenarios](docs/behavior-tests.md)
- [Library crash diagnostics](docs/library-crash-diagnostics.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Word-list attribution](WORDLIST_ATTRIBUTION.md)
- [Changelog](CHANGELOG.md)

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
