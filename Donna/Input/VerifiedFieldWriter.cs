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
///     RÉELLEMENT lu via UI Automation à cet instant, jamais une valeur
///     supposée) puis injection Unicode via <see cref="TextInjector"/>, qui
///     fonctionne partout où l'injection de texte fonctionne. Chaque étape est
///     vérifiée par relecture ; en cas d'échec, tente de restaurer le texte
///     d'origine plutôt que de laisser un état intermédiaire.
///
/// Aucune sélection n'est jamais créée (seul Backspace est utilisé, jamais
/// Maj+Origine) — la garantie de sécurité de <see cref="UiaFieldAccessor"/> est
/// préservée intégralement par ce repli.
/// </summary>
public sealed class VerifiedFieldWriter(UiaFieldAccessor uia, TextInjector injector)
{
    private const int MaxEraseAttempts = 3;

    /// <summary>
    /// Un saut de ligne peut être lu <c>\r\n</c> (2 caractères) par UI Automation
    /// mais ne s'efface qu'avec UN SEUL Backspace dans la plupart des contrôles
    /// de texte — on normalise avant de compter, sinon chaque ligne laisserait un
    /// caractère non effacé (un <c>\r</c> ou <c>\n</c> résiduel).
    /// </summary>
    public static int CountBackspacesNeeded(string text) => text.Replace("\r\n", "\n").Length;

    /// <summary>
    /// Écrit <paramref name="newText"/> dans <paramref name="element"/>.
    /// <paramref name="originalFieldText"/> est le contenu du champ tel que
    /// connu AVANT toute tentative d'écriture — utilisé comme dernier recours
    /// pour restaurer si le repli clavier échoue. Ne lève aucune exception en
    /// cas de succès ; lève <see cref="InvalidOperationException"/> sinon, avec
    /// un message précisant si le texte d'origine a pu être restauré.
    /// </summary>
    public void Write(IUIAutomationElement element, string originalFieldText, string newText)
    {
        if (uia.TryWrite(element, newText))
            return;

        WriteViaKeyboardFallback(originalFieldText, newText);
    }

    private void WriteViaKeyboardFallback(string originalFieldText, string newText)
    {
        // On relit l'état RÉEL du champ maintenant, pas celui d'avant la
        // tentative SetValue ci-dessus : SetValue a pu modifier le champ
        // partiellement même en cas d'échec de la vérification — se fier à une
        // longueur supposée reviendrait à effacer à l'aveugle.
        string current = uia.TryReadFocusedFieldRaw()?.Text ?? originalFieldText;
        int remaining = CountBackspacesNeeded(current);

        for (int attempt = 1; attempt <= MaxEraseAttempts; attempt++)
        {
            injector.Replace(remaining, "");

            string afterErase = uia.TryReadFocusedFieldRaw()?.Text ?? "";
            if (afterErase.Length == 0)
            {
                injector.Replace(0, newText);

                string afterWrite = uia.TryReadFocusedFieldRaw()?.Text ?? "";
                if (afterWrite == newText)
                    return;

                RestoreOrThrow(originalFieldText, "L'injection de la réponse n'a pas abouti comme prévu.");
                return;
            }

            // Effacement incomplet : on relit combien il en reste RÉELLEMENT
            // (jamais une simple soustraction) avant de retenter.
            remaining = CountBackspacesNeeded(afterErase);
        }

        RestoreOrThrow(originalFieldText, "L'effacement du champ n'a pas abouti après plusieurs tentatives.");
    }

    private void RestoreOrThrow(string originalFieldText, string reason)
    {
        string current = uia.TryReadFocusedFieldRaw()?.Text ?? "";
        injector.Replace(CountBackspacesNeeded(current), originalFieldText);

        string after = uia.TryReadFocusedFieldRaw()?.Text ?? "";
        if (after == originalFieldText)
            throw new InvalidOperationException($"{reason} Texte d'origine restauré.");

        throw new InvalidOperationException(
            $"{reason} Impossible de restaurer le texte d'origine — vérifie le champ manuellement.");
    }
}
