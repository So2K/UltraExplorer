# Win+E shortcut checks

Run `dotnet run --project tests/WinExplorerShortcutSmoke -c Release`.
The harness links the production service source, exercises synthetic key policy,
and runs only owned Win32 thread message queues. It never installs a keyboard
hook, sends keyboard input, starts Explorer/UltraExplorer windows, changes focus,
or edits registry/settings. Its callbacks are fixture work on a worker thread.

The checks cover every unrelated virtual key, exact modifier combinations, E
auto-repeat, E held before enabling, injected events, matching release behavior,
queue dispatch, OFF during blocked work, startup/OFF races and observable startup
or launch failure. INPUT layout and the Start-menu workaround are inspected as
data, without sending the workaround to Windows.

Optional `--registration-probe` briefly calls RegisterHotKey on an owned thread,
then immediately unregisters if successful. It sends no key presses. On the
installed Windows build 26100 (Explorer running), Win+E returned error 1409,
ERROR_HOTKEY_ALREADY_REGISTERED, and cleanup succeeded. Accordingly, production
uses WH_KEYBOARD_LL on a dedicated own-process message thread.

## Primary references and implementation choice

- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey): Windows-key shortcuts are reserved by Windows.
- [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc): the callback executes in the installing process on its message-pumping thread. Untouched events must go through CallNextHookEx. The dedicated thread must hand application work to a worker and return promptly; on Windows 7+ timeout may silently remove the hook without a queryable notification, and on Windows 10 1709+ the timeout maximum is 1000 ms.
- [Microsoft PowerToys, pinned source](https://github.com/microsoft/PowerToys/blob/1f9bae97f26788ccaa5eb6830655c67a399e073d/src/runner/centralized_kb_hook.cpp), KeyboardHookProc (MIT): an intercepted Win shortcut emits one VK 0xFF key-up with an origin marker to prevent the subsequent Win release from opening Start. This narrow mechanism is reused; Win key events and unrelated key events pass through normally.
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput): the dummy event can be rejected by UIPI when the foreground app has higher integrity. On rejection the service passes the original E through to Windows, switches readiness OFF and exposes the error. It does not change integrity or patch any other process.

`IsReady` reports installed-hook/message-thread readiness. Windows does not expose
whether an installed low-level hook was silently removed after a timeout; this
is why the keyboard callback contains no UI, filesystem work or application
launches. It only checks E and current modifier bits, applies the one-key latch,
emits the tagged dummy release for an exact new Win+E, and posts an owned message.
There is no keyboard history or text capture.

`Dispose` gates suppression and removes the hook immediately, then ends its
message thread. It cancels queued launches; a caller-supplied launch already
running may finish. The resident launch action must therefore re-read its Win+E
preference before creating a window, including inside any deferred UI callback.
