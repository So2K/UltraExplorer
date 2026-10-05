# Windows Explorer replacement coverage

The **Use UltraExplorer throughout Windows** switch controls filesystem folder launching, Shell selection,
Explorer frame handoff and supported Open/Save/Export/folder dialogs. The
feature is optional and uses per-user Shell routes, public COM APIs and
out-of-process observation. It does not replace the Windows desktop/taskbar
process or change system DLLs.

| Entry point | Route |
| --- | --- |
| Folder/Directory/Drive open or explore verb | Saved HKCU command redirects to `--shell-request --open-folder` |
| Win+E | Independent **Open UltraExplorer with Win+E** switch starts an own-process keyboard listener and opens `--home` |
| Reveal/select from `SHOpenFolderAndSelectItems` | Registered own Shell view receives relative PIDLs and preserves complete selection |
| New external filesystem Explorer frame | Verified new, unprotected single-view frame transfers only after exact UE folder/selection readiness |
| Browsing inside an original Windows Explorer window | Native session exception; navigation stays in that window |
| UltraExplorer directory Open / Show in Explorer / bulk reveal | Shared folder router, with original Windows call when switch is off |
| Common Open/Save/Export/folder dialogs | Existing prepared picker, native caller receives and validates chosen paths/options |

Filesystem Drive/Directory registry commands append `\.` to the `%1` path to
avoid CRT trailing-backslash quoting errors for drive roots. Virtual Folder
arguments retain their raw namespace token and fall back to Windows when the
filesystem parser cannot represent them. Original registry existence, value
type and raw bytes are journaled before writing; off restores only values still
owned by UE, preserving later changes made by the user or another manager.

Win+E has its own preference and sign-in entry (`UltraExplorer Win+E`). All four
combinations of the two switches are supported. General **Restore and pause**
preserves the Win+E choice; uninstall/recovery stops both. Earlier enabled
installations migrate their Win+E intent to the new preference. The former
Win+E/Home CLSID override is restored from its receipt while folder routes stay
enabled. Fresh installations leave both preferences off.

Windows reserves Win-key combinations for the operating system; RegisterHotKey
was rejected with error 1409 on the tested Windows build. The listener uses a
dedicated message thread and a minimal WH_KEYBOARD_LL callback. Only the exact
Win+E chord is claimed; other keys and modified chords pass on, and no keyboard
history is stored. A single non-text key-up prevents Start opening on Win
release, following PowerToys' pinned implementation. Failure releases the hook
and reports the error in settings. Disabling Win+E removes its own sign-in entry
and stops its listener.

Transfer validates frame, tab, active view, process creation time, folder PIDL,
all selected/focused items, destination process/window/request identity and
actual drawn readiness. A timeout or late completion cannot close the source.
Multiple source frames retain distinct destination windows. Disabled/modal,
menu, dragging, busy navigation and unfinished edit states defer transfer.

Existing Windows Explorer windows are no longer imported when integration starts.
Their native browsing session stays with that exact frame, including This PC or
another virtual location followed by a physical drive/folder. Native navigation
and user input protect the frame from later adoption; protection is rechecked
after waits and immediately before any closure. It is not an explorer.exe-wide
exclusion: other applications' folder/dialog requests and the separate Win+E
preference retain their routes.

Folder-verb requests launched from the active native Explorer use the same
browser through `IShellBrowser.BrowseObject(SBSP_SAMEBROWSER | SBSP_ABSOLUTE)`.
The route verifies the direct parent process, foreground frame, process lifetime,
active browser and destination relationship. It does not launch Explorer again
through the overridden verb. A native intent marker belongs to the HWND lifetime;
a recognized native request failure preserves the native window. This is a
conservative context check, since Windows can broker other callers through
Explorer without passing their original owner in a command-line folder verb.

The approach follows [Files' MIT launcher at a pinned revision](https://github.com/files-community/Files/blob/0e3c17ca44d143fb656be27eacf2b25c22a62043/src/Files.App.Launcher/FilesLauncher.cpp)
and Microsoft's [BrowseObject contract](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellbrowser-browseobject).

Native-session verification: 74/74 coordinator/owned-pipe assertions, 17/17
read-only observer assertions, 50/50 navigation policy/cancellation/owned-window
assertions, and 441/441 combined routing/settings/integration assertions.
Release builds have zero warnings/errors. The Windows 11 Shell lookup was also
read-only checked against two real browser rows (active tab host and current
view), with no navigation, input, focus or registry changes. Test/isolated copies
cannot invoke the production native navigation route. No user Explorer window
was manipulated by qualification tests.

Limits: unsupported Shell namespaces (Control Panel, MTP, archive/search/virtual
views), uncertain multiple native tabs and custom or inaccessible file dialogs
remain with their original provider. Out-of-context events can only handle a
window after creation; direct Explorer fallbacks can show a first frame. This
coverage is broader than dialog-only replacement, but is not a universal kernel
interceptor and does not promise that no Windows-native UI can ever be seen.

Verification artifacts are isolated: registry receipt tests33 checks, real
cross-process Shell selection21 checks, read-only observer identity17 checks,
production transfer/router endpoint58 checks, folder parser/navigation29 checks,
and combined picker/settings/integration groups464 checks. Actual WPF folder
view readiness and images are required before publishing this feature.

Sources: [Shell launching](https://learn.microsoft.com/en-us/windows/win32/shell/launch),
[IShellWindows](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nn-exdisp-ishellwindows),
[RegisterPending](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-registerpending),
[IShellView.SelectItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellview-selectitem),
[queued WinEvents](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook).
Existing implementation references/pins and licensing are in SHELL_REGISTRATION.md;
GPL Explorer++ source was inspected but not copied.

Win+E sources: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc),
[PowerToys keyboard hook, pinned source](https://github.com/microsoft/PowerToys/blob/1f9bae97f26788ccaa5eb6830655c67a399e073d/src/runner/centralized_kb_hook.cpp).

Final verification: Release0warnings/errors; combined headless466/466;
real isolatedWPF/IPC66/66, including normal readable folder view+onscreenselected
file tiles in7distinct windows and byte-for-byte preservation of global
workspace/tree/marks after adopted windows close. Since I009/I116 only a window's
own view (its camera, shell-windows/<DestinationId>.workspace.json.tree.json) is
kept per window, and deleted when the window closes; pins, marks, orders and
settings are the shared files, merged per changed field. Source windows start
one pane without changing the main split preference.
Registry33/33, production router/transfer58/58, native cross-process selection
21/21. Existing prepared picker/export/customcontrols/earlyoff/cancel regression
86/86. These tests mutate only test-owned windows or a disposable registry
subtree; no user Explorer frames were controlled during qualification.
