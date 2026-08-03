using System.Diagnostics;
using Interop.UIAutomationClient;

namespace Donna.Input;

/// <summary>
/// Écrit le résultat d'une transformation dans un champ, avec repli à deux
/// niveaux :
///  1. <see cref="UiaFieldAccessor.TryWrite"/> (ValuePattern.SetValue) —
///     atomique, vérifié par relecture. Idéal pour les contrôles de bureau
///     (Bloc-notes) mais refusé par certaines applications (WhatsApp Web,
///     Word — champ <c>contenteditable</c> ou document riche, pas un simple
///     champ de saisie).
///  2. Repli clavier vérifié — Backspace exacts (comptés depuis le texte
///     RÉELLEMENT lu via UI Automation) puis injection Unicode via
///     <see cref="TextInjector"/>, qui fonctionne partout où l'injection de
///     texte fonctionne.
///
/// <c>SendInput</c> ne fait QUE mettre les frappes en file — il ne garantit
/// pas qu'elles ont été traitées par l'application ciblée au retour de
/// l'appel. Relire immédiatement après un envoi de touches revient à vérifier
/// une action asynchrone avant qu'elle ait eu lieu : le même piège qui a
/// rendu l'ancien collage par presse-papiers peu fiable, et qui a rendu la
/// lecture par sélection clavier destructrice (voir ARCHITECTURE.md §7.6).
/// Chaque étape ici attend donc un changement RÉELLEMENT observé (sondage
/// borné via <see cref="WaitForValueAsync"/>, <c>await Task.Delay</c>, jamais
/// <c>Thread.Sleep</c> ni une relecture immédiate) avant de décider quoi que
/// ce soit — et si rien ne change dans le délai imparti, on abandonne plutôt
/// que d'envoyer des touches supplémentaires sur la foi d'une lecture prise
/// trop tôt.
///
/// Aucune sélection n'est jamais créée (seul Backspace est utilisé, jamais
/// Maj+Origine) — la garantie de sécurité de <see cref="UiaFieldAccessor"/> est
/// préservée intégralement par ce repli.
/// </summary>
public sealed class VerifiedFieldWriter(UiaFieldAccessor uia, TextInjector injector)
{
    private const int MaxEraseAttempts = 3;
    private const int ChangeTimeoutMs = 500;
    private const int PollIntervalMs = 20;

    /// <summary>
    /// Un saut de ligne peut être lu <c>\r\n</c> (2 caractères) par UI Automation
    /// mais ne s'efface qu'avec UN SEUL Backspace dans la plupart des contrôles
    /// de texte — on normalise avant de compter, sinon chaque ligne laisserait un
    /// caractère non effacé (un <c>\r</c> ou <c>\n</c> résiduel).
    /// </summary>
    public static int CountBackspacesNeeded(string text) => text.Replace("\r\n", "\n").Length;

    /// <summary>
    /// Écrit <paramref name="newText"/> dans <paramref name="element"/> — un
    /// élément précis, ciblé explicitement du début à la fin (jamais "ce qui a
    /// le focus", qui peut changer en cours d'opération).
    /// <paramref name="originalFieldText"/> est le contenu du champ tel que
    /// connu AVANT toute tentative d'écriture — utilisé comme dernier recours
    /// pour restaurer si le repli clavier échoue. Ne lève aucune exception en
    /// cas de succès ; lève <see cref="InvalidOperationException"/> sinon, avec
    /// un message précisant si le texte d'origine a pu être restauré.
    ///
    /// Chaque appel UI Automation (lecture ou écriture) est explicitement
    /// dispatché via <c>Task.Run</c> pour rester sur un thread MTA (recommandation
    /// Microsoft), même si <see cref="Write"/> lui-même est appelé directement
    /// via <c>await</c> depuis un thread UI STA — voir DonnaContext.
    /// </summary>
    public async Task Write(IUIAutomationElement element, string originalFieldText, string newText)
    {
        bool wroteViaSetValue = await Task.Run(() => uia.TryWrite(element, newText));
        if (wroteViaSetValue)
            return;

        await WriteViaKeyboardFallbackAsync(element, originalFieldText, newText);
    }

    private async Task WriteViaKeyboardFallbackAsync(IUIAutomationElement element, string originalFieldText, string newText)
    {
        string current = await ReadAsync(element) ?? originalFieldText;

        for (int attempt = 1; attempt <= MaxEraseAttempts && CountBackspacesNeeded(current) > 0; attempt++)
        {
            string before = current;
            int remaining = CountBackspacesNeeded(before);

            injector.Replace(remaining, "");
            string? afterErase = await WaitForValueAsync(element, "", before);

            if (afterErase is null)
            {
                // Impossible de relire l'élément ciblé du tout (invalide,
                // application fermée...) : on ne peut plus rien vérifier, donc
                // on abandonne sans envoyer une seule touche de plus.
                throw new InvalidOperationException(
                    "Impossible de relire le champ ciblé après l'effacement (élément invalide ou application fermée) — abandon sans autre action.");
            }

            if (afterErase == before)
            {
                // Aucun changement observable dans le délai imparti : le champ
                // ne réagit plus à nos frappes. On abandonne SANS envoyer une
                // seule touche de plus — y compris pour "restaurer", ce qui
                // nécessiterait aussi d'envoyer des touches à un champ qui
                // vient de prouver son absence de réaction. Le message reflète
                // l'état RÉEL du champ (before == originalFieldText seulement
                // au premier tour ; un tour ultérieur peut avoir déjà effacé
                // une partie du texte).
                string state = afterErase == originalFieldText
                    ? "le texte d'origine est intact (rien n'a été effacé)"
                    : "le champ est resté dans un état partiellement effacé";
                throw new InvalidOperationException(
                    $"Le champ n'a pas réagi à l'effacement (aucun changement observé) — abandon sans autre action ; {state}.");
            }

            // Effacement partiel réellement observé : on continue avec ce qu'il
            // reste VRAIMENT (jamais une simple soustraction supposée).
            current = afterErase;
        }

        if (CountBackspacesNeeded(current) > 0)
        {
            // Toutes les tentatives épuisées sans atteindre un champ vide, alors
            // que du progrès avait bien lieu à chaque tour : on restaure plutôt
            // que de laisser un état intermédiaire.
            await RestoreOrThrowAsync(element, originalFieldText, "L'effacement du champ n'a pas abouti après plusieurs tentatives.");
            return;
        }

        injector.Replace(0, newText);
        string? afterWrite = await WaitForValueAsync(element, newText, current);

        if (afterWrite == newText)
            return;

        await RestoreOrThrowAsync(element, originalFieldText, "L'injection de la réponse n'a pas abouti comme prévu.");
    }

    private async Task RestoreOrThrowAsync(IUIAutomationElement element, string originalFieldText, string reason)
    {
        string current = await ReadAsync(element) ?? "";

        if (CountBackspacesNeeded(current) > 0)
        {
            injector.Replace(CountBackspacesNeeded(current), "");
            current = await WaitForValueAsync(element, "", current) ?? current;
        }

        if (current == "")
        {
            injector.Replace(0, originalFieldText);
            string? after = await WaitForValueAsync(element, originalFieldText, current);
            if (after == originalFieldText)
                throw new InvalidOperationException($"{reason} Texte d'origine restauré.");
        }

        throw new InvalidOperationException(
            $"{reason} Impossible de restaurer le texte d'origine — vérifie le champ manuellement.");
    }

    /// <summary>
    /// Sonde <paramref name="element"/> jusqu'à ce que son contenu atteigne
    /// <paramref name="expected"/>, ou jusqu'à ce qu'il reste STABLE (aucun
    /// changement observé) pendant tout un délai borné (<see cref="ChangeTimeoutMs"/>),
    /// via <c>await Task.Delay</c> entre deux lectures — jamais <c>Thread.Sleep</c>,
    /// et jamais de relecture immédiate.
    ///
    /// Le délai se RÉARME à chaque changement réellement observé, même partiel :
    /// sur un champ volumineux, effacer tout le contenu peut prendre plus que
    /// <see cref="ChangeTimeoutMs"/>, et un délai fixe expirerait alors que les
    /// Backspace sont encore en cours de traitement côté application — la
    /// tentative suivante en enverrait par-dessus, sur-effaçant le champ. Ne
    /// déclarer un blocage que quand la valeur n'a PAS bougé pendant tout le
    /// délai est le seul moyen de distinguer "lent mais qui avance" de
    /// "vraiment bloqué".
    ///
    /// Renvoie la dernière valeur observée, qu'elle corresponde ou non à
    /// <paramref name="expected"/> — l'appelant compare pour savoir si l'attente
    /// a abouti, et peut distinguer "rien n'a changé du tout" (== <paramref
    /// name="before"/>) de "changement partiel observé, puis bloqué ailleurs".
    /// </summary>
    private async Task<string?> WaitForValueAsync(IUIAutomationElement element, string expected, string before)
    {
        string? last = before;
        var stableSince = Stopwatch.StartNew();

        while (stableSince.ElapsedMilliseconds < ChangeTimeoutMs)
        {
            await Task.Delay(PollIntervalMs);

            string? current = await ReadAsync(element);
            if (current == expected)
                return current;

            if (current != last)
            {
                // Progrès réellement observé (même partiel) : le traitement
                // est encore en cours côté application, pas bloqué — on
                // réarme le délai de stabilité plutôt que de le laisser
                // expirer pendant que des frappes sont encore en file.
                last = current;
                stableSince.Restart();
            }
        }

        return last;
    }

    private Task<string?> ReadAsync(IUIAutomationElement element) =>
        Task.Run(() => uia.TryReadElementText(element));
}
