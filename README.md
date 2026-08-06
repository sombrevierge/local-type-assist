# Local Type Assist

Local-first text autocomplete for Windows with personal learning, Russian morphology, contextual ranking, and per-user profiles.

The application works in browser chats and email, notes, messengers, and regular Windows text fields. Typed text, learned words, context, and profiles stay on the user's computer.

![Local Type Assist interface](docs/ui-reference.png)

## Features

- global autocomplete in Windows text fields;
- four modes: suggestion list, complete on Space, idle completion, and learning-only;
- local learning from typed words and accepted suggestions;
- contextual ranking using personal prefix mappings and n-grams up to five words;
- Russian morphology through the local Nestor package;
- separate local profiles for different users;
- optional local corpus import from TXT, MD, CSV, LOG, and JSON;
- password-field and password-manager exclusions;
- no telemetry or cloud text processing.

## Controls

| Action | Result |
|---|---|
| `↑` / `↓` | Select a suggestion |
| `Tab` / `→` | Insert the selected suggestion |
| `Space` | Accept a suggestion only in **Complete on Space** mode |
| `Shift + Space` | Keep the typed word unchanged in **Complete on Space** mode |
| `Esc` | Hide suggestions for the current word |
| `Ctrl + Alt + Space` | Enable or pause the application |

## Requirements

- Windows 10 or Windows 11 x64;
- .NET 10 SDK x64 for building from source;
- the `nuget.org` package source.

## Build

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

Keep the complete `dist` directory together because the morphology dependencies are published beside the executable.

## Development

```powershell
.\scripts\run-dev.ps1
```

The solution file is `LocalTypeAssist.sln`; the WPF project is under `src/LocalTypeAssist`.

## Local data and privacy

Personal profiles are stored outside the repository:

```text
%LOCALAPPDATA%\LocalTypeAssist
```

The application does not upload typed text. Its runtime model and imported corpora remain local. The repository does not contain a user's learned profile.

## Documentation

- [Architecture](docs/architecture.md)
- [Behavior test scenarios](docs/behavior-tests.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Word-list attribution](WORDLIST_ATTRIBUTION.md)

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
