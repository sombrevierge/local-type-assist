# Learning library crash diagnostics — v7.0.2

The learning library is isolated from the global typing loop. UI initialization and refresh errors are written to:

`%LOCALAPPDATA%\LocalTypeAssist\logs\localtypeassist.log`

Open the log with:

```powershell
.\scripts\open-log.ps1
```

v7.0.2 guards WPF filter events until all named controls exist, debounces live refreshes, catches library data/ML action failures, and gives SQLite reads/writes a busy timeout.


## v7.0.3 owner fix

The v7.0.2 log exposed a separate startup failure: `LearningLibraryWindow` used `Application.Current.MainWindow` as its owner. The app creates a suggestion popup before the real settings window, so that property can refer to a window that has never been shown. WPF rejects such a window as `Owner`. v7.0.3 no longer resolves ownership through `Application.Current.MainWindow`; `AppHost` explicitly passes the visible settings window, and owner assignment is non-fatal.


## v7.0.4 binding fix

The v7.0.3 log exposed a DataGrid binding failure: `DataGridCheckBoxColumn` defaults `IsChecked` bindings to TwoWay. `LearningWordView.NeedsReview` is a computed read-only property, so WPF threw while attaching the binding. v7.0.4 explicitly uses `Mode=OneWay` for every checkbox column and marks those columns read-only.
