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
/// Entièrement asynchrone, et c'est ESSENTIEL : WH_KEYBOARD_LL délivre les
/// évènements clavier au thread qui a installé le hook via SA file de messages —
/// ce thread doit donc continuer à pomper les messages pendant qu'on attend.
/// Nos propres évènements injectés (Maj/Ctrl+Origine, Ctrl+C) traversent CE MÊME
/// hook pour atteindre l'application cible : un <c>Thread.Sleep</c> bloquant ce
/// thread les empêcherait d'être dispatchés → interblocage (le sondage attend un
/// Ctrl+C qui ne peut jamais aboutir). <c>await Task.Delay</c> rend la main à la
/// boucle de messages entre deux sondages, sans jamais quitter le thread UI STA
/// (pas de <c>ConfigureAwait(false)</c> : on reste sur le contexte de
/// synchronisation WinForms, obligatoire pour les appels Clipboard/OLE).
///
/// Deux garanties essentielles, vu qu'une sélection (potentiellement tout un
/// document, en portée <see cref="SourceScope.AllBeforeCursor"/>) est une arme
/// chargée si elle reste active :
///  1. Aucune sélection n'est créée tant qu'on n'a pas vérifié que le
///     presse-papiers est réellement accessible (voir <see cref="ReadSelectionAsync"/>,
///     l'appel à <see cref="TryGetClipboardTextAsync"/> AVANT <see cref="SendSelectAndCopy"/>).
///  2. Toute sortie en échec (exception, timeout) désélectionne dans un `finally`
///     — jamais uniquement dans l'appelant, dont le chemin d'erreur peut varier.
/// </summary>
public sealed class SelectionReader
{
    private const int ClipboardChangeTimeoutMs = 500;
    private const int PollIntervalMs = 10;

    /// <summary>
    /// Sélectionne selon <paramref name="scope"/>, copie, renvoie le texte copié
    /// ("" si rien n'était sélectionné), puis restaure le presse-papiers précédent.
    /// Lève si le presse-papiers est inaccessible, si le thread n'est pas STA, ou si
    /// le presse-papiers n'a pas changé après Ctrl+C (délai dépassé). Dans tous les
    /// cas d'échec, garantit qu'aucune sélection ne reste active dans le champ.
    ///
    /// À appeler avec <c>await</c> directement (jamais via <c>Task.Run</c>, qui
    /// ferait échouer les appels Clipboard hors STA) depuis le thread UI.
    /// </summary>
    public async Task<string> ReadSelectionAsync(SourceScope scope)
    {
        EnsureStaThread();

        // 1) On vérifie D'ABORD que le presse-papiers est réellement accessible —
        //    AVANT de créer la moindre sélection. Si cette lecture échoue (verrouillé
        //    par une autre application, etc.), on abandonne ici : aucune sélection
        //    n'a encore été créée, il n'y a donc rien à désélectionner.
        (bool accessible, string? previousClipboard) = await TryGetClipboardTextAsync();
        if (!accessible)
            throw new InvalidOperationException("Le presse-papiers est inaccessible (verrouillé par une autre application ?).");

        uint before = NativeInput.GetClipboardSequenceNumber();
        bool succeeded = false;
        try
        {
            SendSelectAndCopy(scope);

            if (!await WaitForClipboardChangeAsync(before))
                throw new TimeoutException("Le presse-papiers n'a pas changé après Ctrl+C : rien à copier ?");

            (bool readOk, string? copied) = await TryGetClipboardTextAsync();
            if (!readOk)
                throw new InvalidOperationException("Impossible de relire le presse-papiers après la copie.");

            succeeded = true;
            return copied ?? "";
        }
        finally
        {
            // 2) Garantie inconditionnelle : si on n'a PAS réussi à lire un texte
            // exploitable, on désélectionne AVANT de rendre la main — quelle que
            // soit l'erreur (timeout, presse-papiers illisible, etc.). Sinon, la
            // prochaine frappe de l'utilisateur remplacerait toute la sélection
            // (potentiellement un document entier en portée AllBeforeCursor).
            if (!succeeded)
                TryDeselect();

            await TryRestoreClipboardAsync(previousClipboard);
        }
    }

    /// <summary>
    /// Désélectionne (Fin) sans rien copier ni modifier — à appeler après un échec
    /// pour laisser le curseur en fin de champ, sélection relâchée, texte intact.
    /// Synchrone : un seul SendInput, ne bloque jamais rien (pas d'attente).
    /// </summary>
    public void Deselect() =>
        NativeInput.SendInputChecked(
        [
            NativeInput.KeyInput(NativeInput.VK_END, keyUp: false),
            NativeInput.KeyInput(NativeInput.VK_END, keyUp: true),
        ]);

    private void TryDeselect()
    {
        try
        {
            Deselect();
        }
        catch
        {
            // Best effort : une désélection qui échoue ne doit jamais masquer
            // l'erreur d'origine qu'on est en train de propager.
        }
    }

    // Toutes les opérations presse-papiers (Clipboard.*) sont des appels OLE qui
    // exigent le thread UI STA (voir [STAThread] sur Program.Main). Si ce n'est
    // pas le cas — régression future, appel depuis un Task.Run, etc. — on lève une
    // erreur explicite en français plutôt que l'exception OLE cryptique.
    private static void EnsureStaThread()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "Lecture du presse-papiers demandée hors du thread STA de l'interface — " +
                "bug interne de DONNA (ReadSelectionAsync doit être appelée avec await, " +
                "jamais via Task.Run, depuis le thread UI).");
        }
    }

    // Sélection + copie envoyées en UN SEUL appel SendInput (même principe que
    // TextInjector) : Windows garantit l'ordre de traitement, sans entrelacement
    // avec la frappe réelle de l'utilisateur. Ces évènements ne sont que DÉPOSÉS
    // dans la file d'entrée ici — leur dispatch réel (donc le Ctrl+C qui remplit
    // effectivement le presse-papiers) n'a lieu que lorsque le thread UI repompe
    // ses messages, ce que permettent les `await Task.Delay` de ReadSelectionAsync.
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
    // incrémenté par Windows à CHAQUE écriture) plutôt que de deviner un délai fixe.
    // `await Task.Delay` (pas Thread.Sleep) entre deux sondages : rend la main à la
    // boucle de messages du thread UI, indispensable pour que le hook clavier
    // puisse dispatcher les évènements Ctrl+C qu'on vient d'injecter (sinon
    // interblocage : on attendrait un Ctrl+C qui ne peut jamais être traité).
    private static async Task<bool> WaitForClipboardChangeAsync(uint before)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < ClipboardChangeTimeoutMs)
        {
            if (NativeInput.GetClipboardSequenceNumber() != before)
                return true;

            await Task.Delay(PollIntervalMs);
        }

        return false;
    }

    // Le presse-papiers Windows peut être brièvement verrouillé par un autre
    // processus (visionneur, gestionnaire de presse-papiers...) : on retente
    // plutôt que d'échouer bruyamment pour une opération de confort. On élargit
    // volontairement le filtre à InvalidOperationException (violation STA) : si
    // EnsureStaThread n'a pas déjà intercepté le problème (défense en profondeur),
    // on ne veut pas qu'elle traverse cette méthode sans être gérée.
    // Renvoie (false, null) si la lecture échoue définitivement (presse-papiers
    // inaccessible) — à distinguer de (true, null) : rien à copier, un résultat
    // normal, pas un échec.
    private static async Task<(bool ok, string? text)> TryGetClipboardTextAsync(int maxAttempts = 10, int delayMs = 20)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                string? text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                return (true, text);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is ExternalException or InvalidOperationException)
            {
                await Task.Delay(delayMs);
            }
        }

        return (false, null);
    }

    // Si le presse-papiers ne contenait pas de texte avant (image, vide...), on ne
    // touche pas à ce qu'on vient d'y mettre : il n'y a rien de textuel à restaurer.
    // Best effort : ne propage jamais d'exception (voir commentaire sur
    // TryGetClipboardTextAsync pour l'élargissement du filtre d'exceptions).
    private static async Task TryRestoreClipboardAsync(string? previousClipboard, int maxAttempts = 10, int delayMs = 20)
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
            catch (Exception ex) when (attempt < maxAttempts && ex is ExternalException or InvalidOperationException)
            {
                await Task.Delay(delayMs);
            }
        }
    }
}
