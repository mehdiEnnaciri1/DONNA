using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Donna.Input;

/// <summary>
/// Fabrique partagée des structures Win32 SendInput, utilisée par
/// <see cref="TextInjector"/> pour l'injection de texte (Backspace + frappe
/// Unicode). Centralise les détails d'interop délicats pour ne pas dupliquer le
/// piège de taille de structure ci-dessous.
/// </summary>
internal static class NativeInput
{
    public const ushort VK_BACK = 0x08;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    public static void SendInputChecked(INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return;

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Frappe d'une touche virtuelle classique (ici, seulement Backspace).</summary>
    public static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    /// <summary>
    /// Frappe Unicode : wVk = 0, wScan = l'unité UTF-16 elle-même, KEYEVENTF_UNICODE.
    /// Windows synthétise le WM_CHAR correspondant sans passer par une disposition
    /// clavier — fonctionne pour n'importe quel caractère, même absent du clavier
    /// physique actif (émoji, accents...). LLKHF_INJECTED est posé par Windows sur
    /// ces évènements exactement comme pour une touche virtuelle classique (voir
    /// KeyEventTests), donc KeyboardHook les ignore bien et ne pollue pas le buffer
    /// de frappe de DONNA.
    /// </summary>
    public static INPUT UnicodeKeyInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Size = 32 impératif : l'union native Win32 contient aussi MOUSEINPUT
    // (32 octets sur x64), plus grand que KEYBDINPUT (24 octets) qu'on utilise
    // seul ici. Sans le forcer, le marshaling sous-dimensionne l'union → le
    // INPUT entier a la mauvaise taille → SendInput rejette l'appel avec
    // ERROR_INVALID_PARAMETER ("Paramètre incorrect").
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
