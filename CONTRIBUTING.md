# Contributing to UltraExplorer

UltraExplorer is a native Windows file manager built around a zoomable map of
the filesystem. Contributions should improve a real navigation or file-management
workflow while keeping the familiar Windows shell and predictable file operations.

## Start with a small, observable problem

For a bug, include the app version, Windows version, exact steps, expected result
and actual result. Say whether the path is local, removable or on a network.
A tiny folder fixture is much easier to investigate than a screenshot alone.
Do not attach private files, full personal paths or credentials.

For a feature, describe what you are trying to do and where the current workflow
gets in the way. Discuss large interaction changes in an issue before implementing
them. View select is still being defined.

## Build

Install the .NET 10 SDK on Windows x64, then:

```powershell
dotnet build UltraExplorer.sln -c Release
dotnet run --project src/UltraExplorer/UltraExplorer.csproj -c Release
```

See [BUILD.md](BUILD.md) for publishing and targeted smoke checks. Use an isolated
`ULTRAEXPLORER_STATE_DIR` when testing so experiments do not change your ordinary
workspace. Run the checks relevant to your change and explain what was tested
in the pull request.

## Useful first contributions

- Reproduce one keyboard, selection, DPI or accessibility edge case.
- Improve a confusing label or a missing piece of documentation.
- Add a focused regression check for a confirmed bug.
- Test the portable build on another Windows machine and report the result.
- Improve a translation while keeping controls and shortcuts consistent.

Keep pull requests focused. Preserve existing user state formats, keep disk and
Shell work off the UI thread, and avoid changing file-operation behavior as a
side effect of a visual improvement.

Code contributions are accepted under the repository's MIT license. Preserve
third-party notices when adapting existing code.
