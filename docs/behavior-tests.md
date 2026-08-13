# Behavior test scenarios — v7.0

## Suggestions

1. Type a prefix such as `прог` and verify every word suggestion begins with the complete current prefix.
2. Press `Esc`: the popup must disappear and remain hidden while the same token is edited further.
3. Start the next word: suggestions may appear again.
4. Click `×` in the popup and verify the same suppression behavior as `Esc`.
5. Dismissing a popup must not add a rejected-suggestion penalty to the selected candidate.

## Complete on Space

1. In **Complete on Space** mode type a prefix with a visible suggestion and press `Space`: selected completion plus a space is inserted.
2. Type a prefix and press `Shift + Space`: the exact typed text plus a space remains.
3. Separate `Shift` must not act as a cancellation gesture in this mode.

## Delayed learning

1. Type an incorrect word followed by Space.
2. Immediately press Backspace to remove the delimiter, correct the word, and press Space again.
3. Open **Learning Library → Corrections**. The original form should point to the corrected form.
4. The original typo should not receive a clean positive observation merely because Space was pressed once.
5. Type a clean word, continue to another word and finish it. The previous provisional word should then become a confirmed observation.

## Editing inside a token

1. Type an extra letter, Backspace it, finish the corrected token and press Space.
2. Verify the edit produces a correction signal rather than reinforcing the pre-Backspace form.
3. Delete a token completely and finish the edit with a delimiter; a deletion signal should appear in the learning library.

## Learning-only mode

1. Enable **Training only**.
2. Type normally: no popup or automatic insertion should appear.
3. Clean observations should receive the stronger training weight.
4. Corrections and deletions must still be recorded.

## Learning library

1. Search for a learned word.
2. Mark it **trusted** and verify it receives higher priority after returning to suggestions.
3. Mark it **blocked** and verify it is no longer suggested.
4. Delete it and verify its personal counts/context are removed.
5. Enable **Needs review** and verify it also surfaces weak one-off custom tokens carried over from older profiles.
6. Use **Clear likely errors** and verify automatic cleanup remains conservative: only non-seed words with correction evidence are removed.

## Personal ML

1. Accumulate both positive and negative events.
2. Run `scripts/setup-ml.ps1` once on a development machine.
3. Open the learning library and click **Retrain ML**.
4. After at least 20 usable examples with both classes, a `<profile>.ml.json` model should appear under `%LOCALAPPDATA%\LocalTypeAssist\profiles`.
5. Restarting the app should reload the model without starting Python.
