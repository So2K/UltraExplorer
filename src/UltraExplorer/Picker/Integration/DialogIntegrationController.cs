using System.Windows.Threading;

namespace UltraExplorer.Picker.Integration;

/// <summary>The Settings window controls a resident agent, not the lifetime of
/// this file-manager window. Closing the canvas leaves the chosen mode running.</summary>
internal sealed class DialogIntegrationController
{
    public static DialogIntegrationController Shared { get; } = new();
    private DialogIntegrationSettings _settings = DialogIntegrationStore.Read();
    private readonly DispatcherTimer _refresh;
    /// <summary>What listeners were last told (see <see cref="Reported"/>).</summary>
    private (bool, bool, int, string, string) _reported;
    public event Action? Changed;
    public bool Enabled => _settings.Enabled;
    public bool WinEEnabled => _settings.WinEEnabled == true;
    public string WinEStatus => _settings.WinELastError.Length > 0 ? _settings.WinELastError
        : !WinEEnabled ? "Win+E uses Windows Explorer."
        : WinEShortcutAgent.IsReady ? "Win+E opens UltraExplorer, independently of folder and dialog replacement."
        : "The Win+E shortcut is starting.";
    public int ExclusionCount => _settings.ExcludedApplications.Length;
    public string Status => _settings.LastRecovery.Length > 0 ? _settings.LastRecovery
        : !Enabled ? "Windows Explorer and file dialogs are in use."
        : DialogAgent.IsRunning ? (DialogStartup.IsAllowed ? "On, and starts when you sign in. " : "On. ")
            + "Folders and file dialogs open through UltraExplorer. Ctrl+Alt+Shift+Esc restores Windows."
        : "The background integration is starting.";

    private DialogIntegrationController()
    {
        _reported = Reported();
        _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) =>
        {
            _settings = DialogIntegrationStore.Read();
            // OFF may finish after a rapid ON saw the old agent's mutex.
            // Reconcile the still-enabled preference once that agent exits.
            if (_settings.WinEEnabled == true && WinEShortcutStartup.IsAllowed && !WinEShortcutAgent.IsRunning)
                _ = Task.Run(EnsureWinEShortcutStarted);
            // Raised only when something is different: every folder window
            // answers it by telling the Shell its folder again, which asks the
            // disk, and on a share that has gone did so every second.
            var reported = Reported();
            if (reported == _reported) return;
            _reported = reported;
            Changed?.Invoke();
        };
        _refresh.Start();
    }

    /// <summary>Everything a listener shows or acts on: the switches, the number of exceptions,
    /// and the two status lines, which change as the agents start and stop.</summary>
    private (bool, bool, int, string, string) Reported() => (Enabled, WinEEnabled, ExclusionCount, Status, WinEStatus);

    /// <summary>
    /// Every start of UltraExplorer's own window: a dialog some earlier
    /// replacement left hidden is put back, whether or not the mode is on, and
    /// the listener is started when it is on and not yet running. Off the UI
    /// thread; nothing here is needed for the window to open.
    /// </summary>
    public static void OnApplicationStarted() => _ = Task.Run(() =>
    {
        try { DialogGuardian.Heal(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DialogIntegrationStore.Log("Checking dialog leases at start", ex); }
        try { DialogIntegrationStore.ReconcileRegistrations(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { DialogIntegrationStore.Log("The folder-opening registration could not be reconciled", ex); }
        EnsureStarted();
    });

    public static void EnsureStarted()
    {
        if (!DialogStartup.IsAllowed) return;
        try
        {
            if (DialogIntegrationStore.Read().Enabled && !DialogAgent.IsRunning)
                using (DialogSelfProcess.Start("--dialog-agent")) { }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            DialogIntegrationStore.Log("The dialog listener could not start", ex);
            try { DialogGuardian.RestoreAll(true, "Dialog replacement could not start. Windows dialogs are in use."); }
            catch (IOException) { }
        }
        EnsureWinEShortcutStarted();
    }

    internal static void EnsureWinEShortcutStarted()
    {
        if (!WinEShortcutStartup.IsAllowed) return;
        try
        {
            if (DialogIntegrationStore.Read().WinEEnabled == true && !WinEShortcutAgent.IsRunning)
                using (DialogSelfProcess.Start("--shortcut-agent")) { }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            DialogIntegrationStore.Log("The Win+E listener could not start", ex);
            try { WinEShortcutAgent.RecordFailure(ex.Message); }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
            { DialogIntegrationStore.Log("The Win+E failure could not be saved", recovery); }
        }
    }

    /// <summary>
    /// One change at a time, in the order asked, from every Settings window:
    /// an On still being applied must not finish after a Restore and pause
    /// asked later, nor opposite switches from two windows interleave.
    /// </summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    public async Task SetEnabledAsync(bool enabled)
    {
        await _turn.WaitAsync();
        try
        {
            // Update adds or removes the sign-in start with the setting.
            _settings = await Task.Run(() => DialogIntegrationStore.Update(settings => settings with
            { Enabled = enabled, LastRecovery = string.Empty }, requireFolderRegistration: enabled));
            UltraExplorer.Services.FolderShellWindowRegistration.Refresh(enabled);
            if (enabled) await Task.Run(EnsureStarted);
            else await Task.Run(() => DialogGuardian.RestoreAll(false, "Windows dialogs restored."));
        }
        finally { _turn.Release(); }
        Changed?.Invoke();
    }

    public async Task SetWinEEnabledAsync(bool enabled)
    {
        await _turn.WaitAsync();
        try
        {
            _settings = await Task.Run(() => DialogIntegrationStore.Update(settings => settings with
            { WinEEnabled = enabled, WinELastError = string.Empty }));
            if (enabled) await Task.Run(EnsureWinEShortcutStarted);
        }
        finally { _turn.Release(); }
        Changed?.Invoke();
    }

    public async Task RecoverAsync()
    {
        await _turn.WaitAsync();
        try
        {
            await Task.Run(() => DialogGuardian.RestoreAll(true, "Windows dialogs restored. Turn replacement on again when you are ready."));
            _settings = DialogIntegrationStore.Read();
            UltraExplorer.Services.FolderShellWindowRegistration.Refresh(false);
        }
        finally { _turn.Release(); }
        Changed?.Invoke();
    }

    public async Task ResetExclusionsAsync()
    {
        await _turn.WaitAsync();
        try
        {
            _settings = await Task.Run(() => DialogIntegrationStore.Update(settings => settings with { ExcludedApplications = [] }));
        }
        finally { _turn.Release(); }
        Changed?.Invoke();
    }
}
