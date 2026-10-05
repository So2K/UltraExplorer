# Favorite navigation

**Settings → Canvas → Favorite shortcuts** enables round links above This PC
on the nested canvas. It is off by default, preserving the existing view.
Standard quick-access folders and custom UltraExplorer favorites appear as
links, not duplicate folders. Double-click a circle or its label to navigate
and frame the actual folder. A single click leaves a pending file choice intact.
The strip follows pan/zoom, wraps long lists, and is included by Fit all.
It does not read folder contents merely to display a link. Turning it off
restores the original overview without changing the current focused folder.

The setting is saved, including explicit changes from a native replacement
picker. A prepared picker refreshes the preference and custom favorites for
each new request; a late full-contract bind of the same request preserves its
current state. Unedited older windows and pickers preserve newer saved favorite
choices, and native pickers write only explicitly edited navigation preferences.

Known-folder navigation keeps the folder inside its real disk/share hierarchy.
Only missing ancestor names are described to reach the target; the target's
contents load when it is opened. Initial Open/Save folder framing and ordinary
folder launches focus that node instead of adding it alongside the drives.

Normal windows release the initial drive-read restriction once the requested
folder has its complete first frame. At rest, the ordinary This PC overview
automatically lists visible drives and partial folders; it does not wait for a
click. A deep focus and an animated flight still avoid enumerating offscreen
ancestors, and pickers keep their separate lazy overview policy.

A crisp presentation snapshot during camera motion schedules one final scene
retry for deferred visible reads. It then becomes idle without repeated reads.
Partial metadata never claims a full folder count. Gone notifications below an
unread parent verify only known paths, remove genuinely missing nodes and retain
unaffected siblings; a delayed notification for a recreated path refreshes only
that folder.

Idle-loading regression qualification: 1063/1063 combined canvas, frame, live,
picker, settings, routing and integration checks; 53/53 native dialog checks.
The old policy was reproduced in a real owned WPF overview, and the crisp-frame
retry gap failed 5/6 checks against the prior app assembly, then passed 6/6 with
the fix. Natural idle loading was verified for ordinary, Home and folder-launch
windows, without a follow-up click, manual LoadAsync or redraw loop. Before/after
images are in artifacts/normal-idle. Builds have zero warnings/errors.

Verification: 355/355 combined picker, settings, preference and normal folder
IPC assertions; 230/230 final visual picker/settings assertions after removing
the false busy note on sparse ancestors; 306/306 canvas/favorite regressions;
16/16 preference persistence checks; 5/5 current Windows known-folder checks;
53/53 real native Open/custom Save/folder/early Cancel assertions. Release
builds have zero warnings/errors. Images are in artifacts/picker-hierarchy and
artifacts/favorite-link-checks/shots. The native prepared first-frame observations
were 75 ms median/81 ms worst for already-open dialogs, not click-to-frame promises.

Windows known folders such as Downloads are physical folders even when Explorer displays only their friendly names. Downloads navigation uses the current `FOLDERID_Downloads` location returned by Windows, including a folder moved to another drive or share. An unavailable known folder is omitted rather than replaced with a guessed profile path. Desktop, Documents and Pictures keep their current Windows special-folder locations, including configured OneDrive redirection.

The [Known Folder flags documentation](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ne-shlobj_core-known_folder_flag) specifies that retrieval without `KF_FLAG_DEFAULT_PATH` uses the current, possibly redirected location. [SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath) resolves the full path from the known-folder identity; [SIGDN](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-sigdn) distinguishes friendly names from filesystem paths. This PC is a virtual namespace; Downloads is a per-user known folder with its own physical location, as listed in [KNOWNFOLDERID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid).
