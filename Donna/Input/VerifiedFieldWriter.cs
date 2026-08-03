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
///  2. Repli clavier — un SEUL appel <see cref="TextInjector.Replace"/>
///     (Backspace exacts puis caractères de la réponse, dans le même
///     <c>SendInput</c>, ordre garanti par Windows), suivi d'une vérification
///     finale FACULTATIVE.
///
/// Le niveau 2 n'effectue AUCUNE boucle effacer-vérifier-réessayer. Une
/// version antérieure relisait le champ entre chaque étape pour décider s'il
/// fallait renvoyer des touches — mais un <see cref="IUIAutomationElement"/>
/// mémorisé peut devenir périmé dès que le contenu change sur une application
/// pilotée par JavaScript (React, dont WhatsApp Web) : la relecture continuait
/// de renvoyer l'ANCIEN texte, concluait à tort "aucun changement", et
/// renvoyait un nouveau lot de Backspace — détruisant le contenu réel avant
/// d'abandonner. Il n'existe aucune fenêtre de relecture fiable entre
/// l'effacement et l'écriture sur ces applications : la seule stratégie sûre
/// est de ne JAMAIS décider quoi que ce soit sur la foi d'une relecture
/// intermédiaire, exactement comme le mode 1 (source tapée), qui fonctionne
/// de façon fiable partout en injectant tout en un seul geste sans jamais se
/// vérifier lui-même à mi-chemin.
///
/// La vérification finale (facultative) réacquiert un élément FRAIS via le
/// focus courant (<see cref="UiaFieldAccessor.TryReadCurrentlyFocusedText"/>),
/// jamais la référence mémorisée : si elle échoue ou ne correspond pas,
/// DONNA affiche un simple avertissement, n'envoie plus aucune touche, et ne
/// tente jamais de "corriger" après coup. Le vrai filet de sécurité est
/// "Annuler" (<see cref="DonnaContext"/>), pas cette vérification — la
/// transformation est mémorisée pour Annuler même quand la vérification est
/// incertaine.
///
/// Aucune sélection n'est jamais créée (seul Backspace est utilisé, jamais
/// Maj+Origine) — la garantie de sécurité de <see cref="UiaFieldAccessor"/> est
/// préservée intégralement par ce repli.
/// </summary>
public sealed class VerifiedFieldWriter(UiaFieldAccessor uia, TextInjector injector)
{
    // Laisse à l'application ciblée le temps de traiter les touches injectées
    // (SendInput ne fait que les mettre en file) avant la vérification finale
    // facultative — sans jamais bloquer une décision derrière ce délai, elle
    // reste purement informative (avertissement, jamais un nouvel envoi).
    private const int SettleDelayMs = 150;

    /// <summary>
    /// Un saut de ligne peut être lu <c>\r\n</c> (2 caractères) par UI Automation
    /// mais ne s'efface qu'avec UN SEUL Backspace dans la plupart des contrôles
    /// de texte — on normalise avant de compter, sinon chaque ligne laisserait un
    /// caractère non effacé (un <c>\r</c> ou <c>\n</c> résiduel).
    /// </summary>
    public static int CountBackspacesNeeded(string text) => text.Replace("\r\n", "\n").Length;

    /// <summary>
    /// Écrit <paramref name="newText"/> dans <paramref name="element"/>.
    /// <paramref name="originalFieldText"/> est le contenu du champ tel que lu
    /// AVANT toute tentative d'écriture — sert directement à calculer le
    /// nombre de Backspace du repli clavier, jamais une relecture
    /// intermédiaire (voir le commentaire de classe).
    ///
    /// Renvoie <see langword="true"/> si l'écriture a pu être vérifiée
    /// (SetValue relu avec succès, ou repli clavier confirmé par relecture
    /// fraîche du focus), <see langword="false"/> si l'écriture a été tentée
    /// mais n'a pas pu être confirmée — dans les deux cas, l'appelant doit
    /// considérer la transformation comme faite (Annuler doit rester
    /// disponible) ; <see langword="false"/> signifie seulement "vérifie
    /// manuellement", jamais "rien n'a changé".
    ///
    /// Chaque appel UI Automation (lecture ou écriture) est explicitement
    /// dispatché via <c>Task.Run</c> pour rester sur un thread MTA (recommandation
    /// Microsoft), même si <see cref="Write"/> lui-même est appelé directement
    /// via <c>await</c> depuis un thread UI STA — voir DonnaContext.
    /// </summary>
    public async Task<bool> Write(IUIAutomationElement element, string originalFieldText, string newText)
    {
        bool wroteViaSetValue = await Task.Run(() => uia.TryWrite(element, newText));
        if (wroteViaSetValue)
            return true;

        int backspaceCount = CountBackspacesNeeded(originalFieldText);
        injector.Replace(backspaceCount, newText);

        await Task.Delay(SettleDelayMs);
        string? current = await Task.Run(uia.TryReadCurrentlyFocusedText);
        return current == newText;
    }
}
