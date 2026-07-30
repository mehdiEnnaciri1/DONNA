using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Donna.Input;

/// <summary>
/// Portée de la sélection utilisée par <see cref="SelectionReader"/> quand aucune
/// source n'a été tapée au clavier avant le déclencheur.
/// </summary>
public enum SourceScope
{
    /// <summary>Maj+Origine : uniquement la ligne courante. Sûr, c'est le réglage par défaut.</summary>
    Line,

    /// <summary>
    /// Ctrl+Maj+Origine : tout le texte avant le curseur. Nécessaire pour du code
    /// collé multi-ligne, mais prend aussi tout contenu déjà présent avant dans le
    /// champ (ex. le début d'un mail existant).
    /// </summary>
    AllBeforeCursor,
}

/// <summary>
/// Lit le texte réellement présent dans un champ (collé ou déjà là), par
/// sélection + Ctrl+C, quand DONNA n'a rien vu taper avant le déclencheur —
/// <see cref="Donna.Core.TypingBuffer"/> ne voit ni le texte collé (Ctrl+V vide le
/// buffer par prudence) ni le texte déjà présent dans le champ.
///
/// Contrairement à l'ancien collage par presse-papiers (voir historique de
/// TextInjector), cette lecture n'a pas de course possible : on attend une preuve
/// observable que le Ctrl+C a eu lieu (le numéro de séquence du presse-papiers)
/// avant de lire, et aucun collage asynchrone n'est en attente pendant qu'on
/// restaure ensuite le presse-papiers précédent.
/// </summary>
public sealed class SelectionReader
{
    private const int ClipboardChangeTimeoutMs = 500;
    private const int PollIntervalMs = 10;

    /// <summary>
    /// Sélectionne selon <paramref name="scope"/>, copie, renvoie le texte copié
    /// ("" si rien n'était sélectionné), puis restaure le presse-papiers précédent.
    /// Lève <see cref="TimeoutException"/> si le presse-papiers n'a pas changé dans
    /// le délai imparti (champ qui ne répond pas à Ctrl+C).
    /// </summary>
    public string ReadSelection(SourceScope scope)
    {
        uint before = NativeInput.GetClipboardSequenceNumber();
        string? previousClipboard = TryGetClipboardText();

        SendSelectAndCopy(scope);

        if (!WaitForClipboardChange(before))
            throw new TimeoutException("Le presse-papiers n'a pas changé après Ctrl+C : rien à copier ?");

        string text = TryGetClipboardText() ?? "";

        TryRestoreClipboard(previousClipboard);

        return text;
    }

    /// <summary>
    /// Désélectionne (Fin) sans rien copier ni modifier — à appeler après un échec
    /// pour laisser le curseur en fin de champ, sélection relâchée, texte intact.
    /// </summary>
    public void Deselect() =>
        NativeInput.SendInputChecked(
        [
            NativeInput.KeyInput(NativeInput.VK_END, keyUp: false),
            NativeInput.KeyInput(NativeInput.VK_END, keyUp: true),
        ]);

    // Sélection + copie envoyées en UN SEUL appel SendInput (même principe que
    // TextInjector) : Windows garantit l'ordre de traitement, sans entrelacement
    // avec la frappe réelle de l'utilisateur.
    private static void SendSelectAndCopy(SourceScope scope)
    {
        var inputs = new List<NativeInput.INPUT>(8);

        inputs.Add(NativeInput.KeyInput(NativeInput.VK_SHIFT, keyUp: false));
        if (scope == SourceScope.AllBeforeCursor)
            inputs.Add(NativeInput.KeyInput(NativeInput.VK_CONTROL, keyUp: false));

        inputs.Add(NativeInput.KeyInput(NativeInput.VK_HOME, keyUp: false));
        inputs.Add(NativeInput.KeyInput(NativeInput.VK_HOME, keyUp: true));

        if (scope == SourceScope.AllBeforeCursor)
            inputs.Add(NativeInput.KeyInput(NativeInput.VK_CONTROL, keyUp: true));
        inputs.Add(NativeInput.KeyInput(NativeInput.VK_SHIFT, keyUp: true));

        inputs.Add(NativeInput.KeyInput(NativeInput.VK_CONTROL, keyUp: false));
        inputs.Add(NativeInput.KeyInput(NativeInput.VK_C, keyUp: false));
        inputs.Add(NativeInput.KeyInput(NativeInput.VK_C, keyUp: true));
        inputs.Add(NativeInput.KeyInput(NativeInput.VK_CONTROL, keyUp: true));

        NativeInput.SendInputChecked([.. inputs]);
    }

    // On sonde un signal observable (le numéro de séquence du presse-papiers,
    // incrémenté par Windows à CHAQUE écriture) plutôt que de deviner un délai fixe
    // — c'est exactement la course qui a causé le bug du Ctrl+V collant un contenu
    // périmé. Ici on attend la preuve que le Ctrl+C a réellement eu lieu.
    private static bool WaitForClipboardChange(uint before)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < ClipboardChangeTimeoutMs)
        {
            if (NativeInput.GetClipboardSequenceNumber() != before)
                return true;

            Thread.Sleep(PollIntervalMs);
        }

        return false;
    }

    // Le presse-papiers Windows peut être brièvement verrouillé par un autre
    // processus (visionneur, gestionnaire de presse-papiers...) : on retente
    // plutôt que d'échouer bruyamment pour une opération de confort.
    private static string? TryGetClipboardText(int maxAttempts = 10, int delayMs = 20)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        return null;
    }

    // Si le presse-papiers ne contenait pas de texte avant (image, vide...), on ne
    // touche pas à ce qu'on vient d'y mettre : il n'y a rien de textuel à restaurer.
    private static void TryRestoreClipboard(string? previousClipboard, int maxAttempts = 10, int delayMs = 20)
    {
        if (previousClipboard is null)
            return;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(previousClipboard);
                return;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
