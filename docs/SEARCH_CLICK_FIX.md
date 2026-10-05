# Search result text clicks

The installed 61fbc08 log recorded four UI-thread InvalidOperationException
entries on 2026-10-02: SearchResultUnder called VisualTreeHelper.GetParent on a
System.Windows.Documents.Run from highlighted search text. The exception was
survived by CrashReporter, which explains why the app and other click targets
kept working while highlighted text produced a warning.

SearchResultUnder now uses the existing ParentOf helper: visual nodes use their
visual parent, Run/Span content nodes use their logical parent. Single-click
reveal, double-click open and right-click lookup share this path. No search
ranking, file-open association or crash-report suppression was changed.

Regression verification: Release 0 warnings/errors; SearchChecks + SettingsChecks
370/370. The old expression reproduces the exact exception. Realized ListBox
and MainWindow XAML templates cover highlighted/plain/nested Runs, Span, icons,
row background, neighboring results, header/whitespace and reordered results.
Actual WPF preview-mouse forwarding invokes exactly one reveal for the clicked
full path. Tests use isolated state and record reveal actions; no user window,
file association, media application or Shell menu was operated.

Other WPF ancestor walks were audited. The only remaining direct visual-parent
call is the type-guarded ParentOf helper. Current Run/Span ownership matches
[LogicalTreeHelper.GetParent](https://learn.microsoft.com/en-us/dotnet/api/system.windows.logicaltreehelper.getparent?view=windowsdesktop-10.0).
Claude's broader read-only review is coordinated through coordination/messages.
