using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Donna.Input;

/// <summary>
/// Efface la formule tapée (N Backspace) puis colle la réponse via Ctrl+V,
/// sans jamais voler le focus : <see cref="SendInput"/> injecte dans la
/// fenêtre qui a déjà le focus, on ne touche à aucune API d'activation de
/// fenêtre. Voir ARCHITECTURE.md §7.3 : « gérer aussi le presse-papiers et le
/// timing des SendInput ».
/// </summary>
public sealed class TextInjector
{
    private const ushort VK_BACK = 0x08;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Délai avant de restaurer le presse-papiers : Ctrl+V est asynchrone
    /// (SendInput ne fait que déposer l'évènement dans la file d'entrée), donc
    /// si on restaure trop vite, l'application cible peut lire l'ANCIEN
    /// contenu au lieu de notre réponse. Deviendra réglable via
    /// SettingsForm/AppConfig (onglet Avancé, cf. ARCHITECTURE.md §5 Ui/).
    /// </summary>
    public int PasteRestoreDelayMs { get; set; } = 150;

    /// <summary>Efface les <paramref name="charsToDelete"/> derniers caractères puis colle <paramref name="replacementText"/>.</summary>
    public void Replace(int charsToDelete, string replacementText)
    {
        SendBackspaces(charsToDelete);

        if (!string.IsNullOrEmpty(replacementText))
            PasteViaClipboard(replacementText);
    }

    private static void SendBackspaces(int count)
    {
        if (count <= 0)
            return;

        var inputs = new INPUT[count * 2];
        for (int i = 0; i < count; i++)
        {
            inputs[i * 2] = KeyInput(VK_BACK, keyUp: false);
            inputs[i * 2 + 1] = KeyInput(VK_BACK, keyUp: true);
        }

        SendInputChecked(inputs);
    }

    private void PasteViaClipboard(string text)
    {
        string? previousClipboard = TrySaveClipboard();

        try
        {
            TrySetClipboardText(text);
            SendCtrlV();

            // Laisse le temps à l'application cible de lire le presse-papiers
            // avant qu'on le restaure (cf. commentaire sur PasteRestoreDelayMs).
            Thread.Sleep(PasteRestoreDelayMs);
        }
        finally
        {
            TryRestoreClipboard(previousClipboard);
        }
    }

    private static void SendCtrlV()
    {
        SendInputChecked(
        [
            KeyInput(VK_CONTROL, keyUp: false),
            KeyInput(VK_V, keyUp: false),
            KeyInput(VK_V, keyUp: true),
            KeyInput(VK_CONTROL, keyUp: true),
        ]);
    }

    // Le presse-papiers Windows est un ressource partagée notoirement capricieuse
    // (peut être brièvement verrouillé par un autre processus — visionneur du
    // presse-papiers, gestionnaire de copier-coller, etc.) : on retente plutôt
    // que d'échouer bruyamment pour une opération qui n'est pas critique.
    // On ne sauvegarde/restaure QUE le texte (pas l'IDataObject complet) :
    // Clipboard.SetDataObject() avec un IDataObject OLE complexe provenant
    // d'une autre appli peut lever des exceptions de mismatch de type au
    // moment du rendu des formats, hors du contrôle des retries ci-dessus.
    private static string? TrySaveClipboard()
    {
        return TryClipboardOperation(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
    }

    private static void TrySetClipboardText(string text)
    {
        TryClipboardOperation<object?>(() => { Clipboard.SetText(text); return null; });
    }

    private static void TryRestoreClipboard(string? previousClipboard)
    {
        if (!string.IsNullOrEmpty(previousClipboard))
            TryClipboardOperation<object?>(() => { Clipboard.SetText(previousClipboard); return null; });
    }

    private static T? TryClipboardOperation<T>(Func<T?> operation, int maxAttempts = 20, int delayMs = 50)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        return default;
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
