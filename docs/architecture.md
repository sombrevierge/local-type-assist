# Architecture — Local Type Assist v7

## Input path

`KeyboardHook` installs `WH_KEYBOARD_LL`. Non-injected key events are translated with the active keyboard layout and passed to `TypingController`. Injected events are ignored so text inserted by the application does not re-enter the learning loop.

`TypingController` tracks the current token, up to five context words, suggestions, temporary completions and edit history. Idle completion is revision-gated by the current word, foreground window and focus token.

## Delayed confirmation and corrections

A completed word is kept as a provisional token for one word instead of being positively learned immediately. If the user presses Backspace across the delimiter, the provisional token returns to editing state. The original and final forms can then be recorded as a correction pair.

Backspace inside a word also starts an edit trace. If the final token differs from the form seen before editing, the old form gets a negative correction signal and the new form becomes the correction target. If the token is erased entirely, a deletion event is recorded. This prevents immediate typos from becoming strong positive examples.

## Suggestion dismissal

`Esc` or the `×` control in `SuggestionWindow` hides the popup for the rest of the current token. Dismissal is stored as a neutral UI event; it is deliberately separate from rejecting a particular suggestion. A new token clears the suppression state.

## Ranking

`SuggestionEngine` combines:

- seed word rank;
- typed, accepted, training and corpus counts;
- personal prefix choices and context-prefix choices;
- personal n-grams up to five words and lemma backoff;
- recency;
- morphology and grammatical agreement;
- confirmed clean observations and correction targets;
- correction/deletion/rejection penalties;
- explicit trusted/blocked word flags;
- optional personal ML reranker score.

Blocked words are excluded. Trusted words receive an explicit positive prior. Personal ML has a strong but bounded contribution so it cannot bypass prefix safety or rewrite typed text.

## Personal ML

The optional trainer is `Resources/ml/train_personal_model.py`. It reads only the local SQLite event database and uses scikit-learn `DictVectorizer` plus `SGDClassifier(loss="log_loss")`. Positive examples come from clean typing, training mode, accepted suggestions and correction targets. Negative examples come from rejected suggestions and corrected-away forms.

The trainer exports a compact JSON file containing an intercept and feature weights. C# loads that file through `PersonalMlScorer`; Python is not used during ordinary typing. Features include candidate identity, prefix, prefix/candidate pair, suffix-length bucket and up to four context n-grams.

## Storage

Two local layers are used under `%LOCALAPPDATA%\LocalTypeAssist`:

1. existing profile JSON files keep aggregate counts and n-grams for backward compatibility with v6 data;
2. `learning-v7.sqlite3` stores event-level learning signals, correction pairs and trust/block flags.

The ML model is stored beside the profile as `<profile>.ml.json`. All of these files are outside the repository and are ignored by Git.

## Learning library

`LearningLibraryWindow` exposes accumulated personal words and v7 corrections. Users can search, delete individual learned words, mark words trusted, block words, and remove cautiously detected likely errors. Deleting a learned word removes its aggregate counts, matching personal n-grams/context choices and associated v7 events.

## Privacy

`PrivacyGuard` excludes password fields and known credential/password-manager contexts. No learning event, imported corpus or ML model is uploaded by the application.
