# Architecture — Local Type Assist v6

## Input path

`KeyboardHook` installs `WH_KEYBOARD_LL`. Non-injected key-down events are translated with the active keyboard layout and passed to `TypingController`. Injected events are ignored, preventing a feedback loop.

`TypingController` keeps the current word, up to three completed context words, current suggestions and temporary selected completion. A timer schedules idle completion. Every schedule stores the current revision, word, foreground window and focus token so a stale timer cannot complete a newer word.

## Cross-application completion

`InputInjector` uses Unicode `SendInput`. Immediate completion types only the suffix and selects it with `Shift+Left`. This makes ordinary typing replace the temporary suffix naturally in browsers and desktop editors. Space/punctuation moves the caret to the end and commits the word. Shift deletes the selected suffix and suppresses suggestions for the rest of that word.

## Focus and privacy

`PrivacyGuard` ignores the app itself, password fields and known credential/password-manager processes. The focus token avoids volatile UIA `RuntimeId` and text-derived `Name`, using the foreground window plus stable control metadata and approximate field location. This prevents Chromium contenteditable controls from resetting the prefix after each character while still distinguishing different fields in the browser.

## Ranking

`SuggestionEngine` combines:

- seed word rank;
- typed, accepted and corrected counts;
- learned prefix → word choices;
- personal bigram/trigram counts;
- embedded starter phrase counts;
- recency;
- completion length;
- morphology and grammatical agreement.

The context model is local n-gram ranking, not a generative LLM. Personal counts have larger multipliers than the embedded starter phrases.

## Morphology

`MorphologyService` initializes `NestorMorph` directly and asynchronously. It builds a prefix index for generated word forms and caches analyses. Candidate agreement checks person, number, gender, case, tense and infinitive expectations. Enum value `None` is treated as absent rather than as a conflicting grammatical value.

## Storage

Each profile is a JSON file under `%LOCALAPPDATA%\LocalTypeAssist\profiles`. It stores words, bigrams, trigrams, prefix choices and learned gender signals. Writes are debounced and performed atomically through a temporary file.

## UI

WPF is used only for the settings window and non-activating suggestion overlay. The release is self-contained `win-x64`, multi-file, and untrimmed for compatibility with the morphology package.
