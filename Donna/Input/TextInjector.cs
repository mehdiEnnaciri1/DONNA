namespace Donna.Input;

/// <summary>
/// Efface la formule tapée (N Backspace) puis injecte la réponse caractère par
/// caractère via SendInput en frappe Unicode — jamais via le presse-papiers, qui
/// n'est ni lu ni modifié ici (voir <see cref="SelectionReader"/> pour la lecture
/// du champ, qui elle utilise le presse-papiers de façon contrôlée).
///
/// Backspace et caractères sont envoyés en UN SEUL appel SendInput : Windows
/// garantit alors que toute la séquence est traitée dans l'ordre, sans
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
    /// <summary>
    /// Efface les <paramref name="charsToDelete"/> derniers caractères puis injecte
    /// <paramref name="replacementText"/>. Avec <paramref name="charsToDelete"/> = 0
    /// et une sélection active dans le champ (repli par sélection, voir
    /// DonnaContext), la première frappe remplace la sélection — pas besoin de
    /// Backspace.
    /// </summary>
    public void Replace(int charsToDelete, string replacementText)
    {
        int backspaceCount = Math.Max(0, charsToDelete);
        var inputs = new NativeInput.INPUT[backspaceCount * 2 + replacementText.Length * 2];
        int i = 0;

        for (int b = 0; b < backspaceCount; b++)
        {
            inputs[i++] = NativeInput.KeyInput(NativeInput.VK_BACK, keyUp: false);
            inputs[i++] = NativeInput.KeyInput(NativeInput.VK_BACK, keyUp: true);
        }

        // On itère sur les `char` (unités UTF-16), pas sur les points de code : un
        // caractère hors du Plan de base (émoji...) occupe deux `char` consécutifs
        // (paire de substitution), chacun devient naturellement un évènement Unicode
        // distinct — Windows/l'application cible recombine la paire à la réception.
        foreach (char c in replacementText)
        {
            inputs[i++] = NativeInput.UnicodeKeyInput(c, keyUp: false);
            inputs[i++] = NativeInput.UnicodeKeyInput(c, keyUp: true);
        }

        NativeInput.SendInputChecked(inputs);
    }
}
