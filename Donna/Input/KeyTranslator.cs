using System.Runtime.InteropServices;
using System.Text;

namespace Donna.Input;

/// <summary>
/// Traduit un code de touche (VK) en caractère(s) Unicode, en tenant compte de
/// la disposition clavier active (AZERTY, AltGr…) et des touches mortes
/// (`^` + `e` → `ê`). Point le plus délicat du projet — voir ARCHITECTURE.md §7.1.
///
/// Fonctionnement :
///  - Cette classe suit elle-même l'état Shift/Ctrl/Alt via <see cref="OnKeyDown"/>
///    et <see cref="OnKeyUp"/>. On ne peut PAS utiliser GetKeyboardState() : dans
///    un hook global (WH_KEYBOARD_LL), il reflète la file d'entrée du thread
///    appelant, pas forcément celle de l'application au premier plan.
///  - AltGr n'a pas besoin de traitement spécial : Windows génère un Ctrl
///    synthétique en plus de l'Alt droit physique, donc suivre Ctrl/Alt suffit
///    à ce que ToUnicodeEx résolve correctement `@ # € [ ]` sur AZERTY.
///  - Les touches mortes sont gérées automatiquement par l'état interne (par
///    thread) de ToUnicodeEx : un appel par touche réellement enfoncée, dans
///    l'ordre, suffit. Un retour négatif signale une touche morte en attente
///    (rien à ajouter au buffer pour l'instant, le caractère combiné sortira
///    au prochain appel).
/// </summary>
public sealed class KeyTranslator
{
    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12; // Alt
    private const uint VK_CAPITAL = 0x14;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;

    private const uint VK_BACK = 0x08;
    private const uint VK_TAB = 0x09;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;

    // Touches dont ToUnicodeEx peut historiquement renvoyer un caractère de
    // contrôle brut (BS/TAB/CR/ESC) au lieu de rien : à ne jamais injecter
    // comme texte, ces touches sont gérées à part par TypingBuffer
    // (Backspace / Reset), pas par une traduction en caractère.
    private static readonly HashSet<uint> ControlCharacterKeys = [VK_BACK, VK_TAB, VK_RETURN, VK_ESCAPE];

    private bool _shiftDown;
    private bool _controlDown;
    private bool _altDown;

    /// <summary>À appeler pour CHAQUE touche enfoncée, avant tout traitement du buffer.</summary>
    public void OnKeyDown(uint vkCode)
    {
        UpdateModifierState(vkCode, isDown: true);
    }

    /// <summary>À appeler pour CHAQUE touche relâchée.</summary>
    public void OnKeyUp(uint vkCode)
    {
        UpdateModifierState(vkCode, isDown: false);
    }

    /// <summary>
    /// Vrai si Ctrl est actuellement enfoncé — utilisé par le câblage pour
    /// détecter un collage manuel (Ctrl+V), qui doit réinitialiser le buffer
    /// au même titre qu'un clic ou un changement de fenêtre (ARCHITECTURE.md §6).
    /// </summary>
    public bool IsControlDown => _controlDown;

    private void UpdateModifierState(uint vkCode, bool isDown)
    {
        if (vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
            _shiftDown = isDown;
        else if (vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL)
            _controlDown = isDown;
        else if (vkCode is VK_MENU or VK_LMENU or VK_RMENU)
            _altDown = isDown;
    }

    /// <summary>
    /// Traduit une touche enfoncée en caractère(s) imprimables selon la
    /// disposition active, ou "" si la touche ne produit rien tout de suite
    /// (touche de contrôle, ou touche morte en attente de combinaison).
    /// </summary>
    public string Translate(uint vkCode, uint scanCode)
    {
        if (ControlCharacterKeys.Contains(vkCode))
            return "";

        byte[] keyState = new byte[256];
        if (_shiftDown) keyState[VK_SHIFT] = 0x80;
        if (_controlDown) keyState[VK_CONTROL] = 0x80;
        if (_altDown) keyState[VK_MENU] = 0x80;

        // Verrouillage majuscules : état "toggle" global, fiable via GetKeyState
        // même hors focus (contrairement à l'état des touches simples).
        if ((GetKeyState((int)VK_CAPITAL) & 1) != 0)
            keyState[VK_CAPITAL] = 0x01;

        IntPtr layout = GetForegroundKeyboardLayout();
        var buffer = new StringBuilder(8);
        int result = ToUnicodeEx(vkCode, scanCode, keyState, buffer, buffer.Capacity, 0, layout);

        // result > 0  : "result" caractères imprimables (parfois 2, ex. touche
        //               morte combinée à une touche non-combinable).
        // result == 0 : touche sans caractère associé (F1, flèches, etc.).
        // result <  0 : touche morte amorcée, en attente du caractère suivant.
        return result > 0 ? buffer.ToString(0, result) : "";
    }

    /// <summary>
    /// Disposition clavier du thread de la fenêtre au premier plan — PAS celle
    /// du thread de DONNA, qui peut différer si l'utilisateur a des dispositions
    /// différentes selon l'application active.
    /// </summary>
    private static IntPtr GetForegroundKeyboardLayout()
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        uint threadId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        return GetKeyboardLayout(threadId);
    }

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
}
