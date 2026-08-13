# Changelog

## 7.0.4

- Fixed the Learning Library crash caused by `DataGridCheckBoxColumn` creating a default TwoWay binding for the read-only computed `NeedsReview` property.
- All library checkbox columns are now explicitly `Mode=OneWay` and read-only; changes to Trusted/Blocked continue to happen only through the dedicated buttons.
- No learning-data migration is required from v7.0.3.

## 7.0.3

- Fixed Learning Library failing to open with `InvalidOperationException` when WPF tried to use an unshown window as `Owner`.
- Learning Library now receives the actual settings window from `AppHost` only while that window is visible.
- Owner assignment is defensive: a presentation-state race can no longer prevent the library from opening.

## 7.0.2

- Fixed a crash when opening the learning library caused by WPF filter events being able to fire before all named controls were initialized.
- Learning-library refresh is now guarded against re-entrancy and debounced while typing continues.
- Library loading, cleanup actions, flags, and ML status failures are contained inside the library window instead of terminating the whole app.
- Added local crash/error logging to `%LOCALAPPDATA%\LocalTypeAssist\logs\localtypeassist.log`.
- Added a SQLite busy timeout to make learning-library reads safer while typing events are being written.

## 7.0.1

- Fixed the v7.0 build failure in `MlTrainingService` by importing `LocalTypeAssist.Models`.
- Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12 so NuGet no longer resolves the vulnerable 2.1.11 native SQLite bundle.
- No learning data or settings migration is required from v7.0.


## 6.8.0 — 2026-08-06

- restored explicit completion on Space with `Shift + Space` bypass;
- added a learning-only mode without suggestions or automatic insertion;
- increased the priority of personal learning over the starter vocabulary;
- added context-plus-prefix learning and personal n-grams up to five words;
- improved local corpus import and morphology indexing for learned words.
