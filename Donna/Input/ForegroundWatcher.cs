using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Donna.Input;

/// <summary>
/// Surveille les changements de fenêtre au premier plan (SetWinEventHook /
/// EVENT_SYSTEM_FOREGROUND) pour réinitialiser le buffer : si l'utilisateur
/// change de fenêtre, le champ de saisie a changé et le buffer ne correspond
/// plus à rien de fiable.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    // Référence gardée en champ pour la même raison que dans KeyboardHook :
    // éviter que le GC ramasse le délégué tant que le hook natif l'utilise.
    private readonly WinEventProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event Action? ForegroundChanged;

    public ForegroundWatcher()
    {
        _proc = WinEventCallback;
    }

    /// <summary>Installe le hook. Nécessite une boucle de messages sur le thread appelant.</summary>
    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookId = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, idProcess: 0, idThread: 0, WINEVENT_OUTOFCONTEXT);

        if (_hookId == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void WinEventCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Ne réagir qu'aux changements de fenêtre elle-même, pas à ceux de ses
        // sous-éléments (barres d'outils, contrôles internes, etc.).
        if (idObject == OBJID_WINDOW)
            ForegroundChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWinEvent(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
