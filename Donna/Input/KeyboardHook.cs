using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Donna.Input;

/// <summary>Un appui ou relâchement de touche capté par le hook clavier bas niveau.</summary>
public readonly record struct KeyEvent(uint VkCode, uint ScanCode, uint Flags)
{
    private const uint LLKHF_EXTENDED = 0x00000001;
    private const uint LLKHF_INJECTED = 0x00000010;

    public bool IsExtended => (Flags & LLKHF_EXTENDED) != 0;

    /// <summary>
    /// Vrai si la touche a été injectée par un programme (via SendInput), pas
    /// tapée physiquement. À vérifier avant de traiter l'évènement : sinon les
    /// Backspace/Ctrl+V que DONNA injecte elle-même (TextInjector) repasseraient
    /// par ce hook et perturberaient le buffer.
    /// </summary>
    public bool IsInjected => (Flags & LLKHF_INJECTED) != 0;
}

/// <summary>
/// Hook clavier bas niveau (WH_KEYBOARD_LL) : reçoit chaque appui/relâchement
/// de touche dans tout Windows, sans jamais bloquer la frappe (on ne fait
/// qu'observer, CallNextHookEx laisse toujours passer l'évènement).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Référence gardée en champ : si le délégué est ramassé par le GC alors que
    // le hook natif le référence encore, Windows appelle un pointeur mort.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event Action<KeyEvent>? KeyDown;
    public event Action<KeyEvent>? KeyUp;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>Installe le hook. Doit être appelé depuis un thread avec une boucle de messages.</summary>
    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule!;
        IntPtr moduleHandle = GetModuleHandle(currentModule.ModuleName);

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        if (_hookId == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var evt = new KeyEvent(data.vkCode, data.scanCode, data.flags);

            int message = wParam.ToInt32();
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                KeyDown?.Invoke(evt);
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
                KeyUp?.Invoke(evt);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
