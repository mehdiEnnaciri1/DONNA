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
/// focus courant à chaque sondage (<see cref="UiaFieldAccessor.TryReadCurrentlyFocusedText"/>),
/// jamais la référence mémorisée, et ne pilote plus aucune action (seul le
/// message affiché en dépend) — elle peut donc se permettre d'être patiente
/// sans aucun risque : sur WhatsApp Web, React remplace le nœud DOM du champ
/// après l'écriture, et UI Automation peut prendre plus d'une seconde à
/// exposer le nouvel élément. Voir <see cref="VerifyAsync"/> pour la
/// distinction en trois issues (succès / succès non confirmé / échec réel).
/// Dans tous les cas, aucune touche n'est jamais envoyée pendant cette
/// vérification, et l'appelant mémorise la transformation pour "Annuler" quel
/// que soit le résultat — c'est le vrai filet de sécurité, pas cette
/// vérification.
///
/// Aucune sélection n'est jamais créée (seul Backspace est utilisé, jamais
/// Maj+Origine) — la garantie de sécurité de <see cref="UiaFieldAccessor"/> est
/// préservée intégralement par ce repli.
/// </summary>
public sealed class VerifiedFieldWriter(UiaFieldAccessor uia, TextInjector injector)
{
    private const int VerifyTimeoutMs = 1000;
    private const int PollIntervalMs = 50;

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
    /// AVANT toute tentative d'écriture — sert à calculer le nombre de
    /// Backspace du repli clavier ET de référence pour la vérification finale,
    /// jamais une relecture intermédiaire (voir le commentaire de classe).
    ///
    /// Renvoie <see langword="true"/> si l'écriture a pu être vérifiée, ou si
    /// le champ a manifestement changé sans qu'on puisse le confirmer au
    /// caractère près (voir <see cref="VerifyAsync"/>) ; <see langword="false"/>
    /// uniquement quand le champ est resté identique à son état d'origine
    /// pendant tout le délai de vérification — le seul cas où l'on peut
    /// affirmer que rien n'a bougé. Dans tous les cas, l'appelant doit
    /// considérer la transformation comme faite (Annuler doit rester
    /// disponible) : <see langword="false"/> signifie "vérifie manuellement",
    /// jamais "annule toi-même".
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

        return await VerifyAsync(originalFieldText, newText);
    }

    /// <summary>
    /// Sonde le focus courant (élément FRAIS réacquis à chaque tour, jamais
    /// une référence mémorisée) jusqu'à distinguer l'une de ces trois issues,
    /// ou jusqu'à expiration d'un délai borné (<see cref="VerifyTimeoutMs"/>) via
    /// <c>await Task.Delay</c> (jamais <c>Thread.Sleep</c>) :
    ///  - le texte lu correspond exactement à <paramref name="newText"/> → succès ;
    ///  - la lecture est impossible, ou diffère à la fois de
    ///    <paramref name="newText"/> ET de <paramref name="originalFieldText"/> →
    ///    succès également : le champ a manifestement changé (ou son élément
    ///    est devenu illisible, ce qui arrive précisément quand React
    ///    remplace le nœud DOM après une écriture réussie), on ne peut
    ///    simplement pas le confirmer au caractère près ;
    ///  - le texte lu reste identique à <paramref name="originalFieldText"/>
    ///    pendant tout le délai → seul cas où l'on sait que rien n'a bougé.
    /// Aucune touche n'est jamais envoyée ici, quelle que soit l'issue — cette
    /// méthode ne fait plus que décider quel message afficher.
    /// </summary>
    private async Task<bool> VerifyAsync(string originalFieldText, string newText)
    {
        var stopwatch = Stopwatch.StartNew();

        do
        {
            string? current = await Task.Run(uia.TryReadCurrentlyFocusedText);

            if (current == newText)
                return true;

            if (current != originalFieldText)
                return true;

            await Task.Delay(PollIntervalMs);
        }
        while (stopwatch.ElapsedMilliseconds < VerifyTimeoutMs);

        return false;
    }
}
