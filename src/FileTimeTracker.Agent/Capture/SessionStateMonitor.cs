using System.Runtime.Versioning;
using FileTimeTracker.Agent.Interop;
using FileTimeTracker.Core.Diagnostics;
using FileTimeTracker.Core.Tracking;
using Microsoft.Win32;

namespace FileTimeTracker.Agent.Capture;

/// <summary>
/// Tracks lock, sign-out, sleep and resume so accrual can be paused (design.md §4.1, §3).
/// </summary>
/// <remarks>
/// Two independent signals are combined on purpose. <see cref="SystemEvents"/> gives immediate
/// notification but can be missed — the agent may start while the machine is already locked, and
/// fast user switching can reorder events. The input-desktop check in
/// <see cref="NativeMethods.IsWorkstationLocked"/> is polled each sample and is authoritative for
/// the lock state, so a missed event self-heals within one sample interval instead of silently
/// billing locked hours as work.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionStateMonitor : IDisposable
{
    private readonly TrackerLog _log;
    private readonly bool _verifyDesktop;

    private volatile bool _suspended;
    private volatile bool _lockedByEvent;
    private volatile bool _sessionEnded;
    private bool _disposed;

    public SessionStateMonitor(TrackerLog log, bool verifyDesktop = true)
    {
        _log = log;
        _verifyDesktop = verifyDesktop;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    /// <summary>
    /// Raised just before the machine suspends or the session ends, so the host can close the open
    /// interval and drain the buffer while it still can.
    /// </summary>
    public event Action<string>? Interrupting;

    public SessionState Current
    {
        get
        {
            if (_sessionEnded)
            {
                return SessionState.LoggedOff;
            }

            if (_suspended)
            {
                return SessionState.Suspended;
            }

            if (_lockedByEvent)
            {
                return SessionState.Locked;
            }

            if (_verifyDesktop && IsLockedByDesktop())
            {
                return SessionState.Locked;
            }

            return SessionState.Unlocked;
        }
    }

    private bool IsLockedByDesktop()
    {
        try
        {
            return NativeMethods.IsWorkstationLocked();
        }
        catch (Exception ex)
        {
            // Treat an unreadable desktop as unlocked: over-counting a rare failure is better than
            // silently dropping a whole day because one API misbehaved.
            _log.Warn("Input-desktop lock check failed; assuming unlocked.", ex);
            return false;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _lockedByEvent = true;
                _log.Info($"Session paused: {e.Reason}.");
                Interrupting?.Invoke(e.Reason.ToString());
                break;

            case SessionSwitchReason.SessionLogoff:
                _sessionEnded = true;
                _log.Info("Session ending: logoff.");
                Interrupting?.Invoke("SessionLogoff");
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionLogon:
                _lockedByEvent = false;
                _sessionEnded = false;
                _log.Info($"Session resumed: {e.Reason}.");
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _suspended = true;
                _log.Info("Power: suspending.");
                Interrupting?.Invoke("Suspend");
                break;

            case PowerModes.Resume:
                _suspended = false;
                _log.Info("Power: resumed.");
                break;
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _sessionEnded = true;
        _log.Info($"Session ending: {e.Reason}.");
        Interrupting?.Invoke(e.Reason.ToString());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}
