using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Donna.Input;

/// <summary>
/// Efface la formule tapée (N Backspace) puis injecte la réponse caractère par
/// caractère via <see cref="SendInput"/> en frappe Unicode (KEYEVENTF_UNICODE) —
/// jamais via le presse-papiers, qui n'est ni lu ni modifié.
///
/// Backspace et caractères sont envoyés en UN SEUL appel <see cref="SendInput"/> :
/// Windows garantit alors que toute la séquence est traitée dans l'ordre, sans
/// entrelacement avec la frappe réelle de l'utilisateur. C'est ce qui remplace
/// l'ancienne approche Ctrl+V + presse-papiers, dont le collage (asynchrone)
/// pouvait arriver après la restauration du presse-papiers — collant alors un
/// contenu périmé ou celui de l'utilisateur, sans qu'aucun délai ne puisse
/// corriger la course de façon fiable.
///
/// Ne vole jamais le focus : SendInput injecte dans la fenêtre qui a déjà le
/// focus, on ne touche à aucune API d'activation de fenêtre.
/// </summary>
public sealed class TextInjector
{
    private const ushort VK_BACK = 0x08;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>Efface les <paramref name="charsToDelete"/> derniers caractères puis injecte <paramref name="replacementText"/>.</summary>
    public void Replace(int charsToDelete, string replacementText)
    {
        int backspaceCount = Math.Max(0, charsToDelete);
        var inputs = new INPUT[backspaceCount * 2 + replacementText.Length * 2];
        int i = 0;

        for (int b = 0; b < backspaceCount; b++)
        {
            inputs[i++] = KeyInput(VK_BACK, keyUp: false);
            inputs[i++] = KeyInput(VK_BACK, keyUp: true);
        }

        // On itère sur les `char` (unités UTF-16), pas sur les points de code : un
        // caractère hors du Plan de base (émoji...) occupe deux `char` consécutifs
        // (paire de substitution), chacun devient naturellement un évènement Unicode
        // distinct — SendInput/Windows recombine la paire côté application cible.
        foreach (char c in replacementText)
        {
            inputs[i++] = UnicodeKeyInput(c, keyUp: false);
            inputs[i++] = UnicodeKeyInput(c, keyUp: true);
        }

        if (inputs.Length > 0)
            SendInputChecked(inputs);
    }

    private static void SendInputChecked(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
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

    // Frappe Unicode : wVk = 0, wScan = l'unité UTF-16 elle-même, KEYEVENTF_UNICODE.
    // Windows synthétise le WM_CHAR correspondant sans passer par une disposition
    // clavier — fonctionne pour n'importe quel caractère (accents, émoji...), même
    // absent du clavier physique actif. LLKHF_INJECTED est posé par Windows sur ces
    // évènements exactement comme pour un SendInput classique (vérifié via
    // KeyEvent.IsInjected, voir KeyboardHook), donc KeyboardHook les ignore bien
    // et ne pollue pas le buffer de frappe de DONNA.
    private static INPUT UnicodeKeyInput(char c, bool keyUp) => new()
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
    private struct INPUT
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
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
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
