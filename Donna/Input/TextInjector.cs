namespace Donna.Input;

/// <summary>
/// Efface la formule tapée (N Backspace) puis injecte la réponse caractère par
/// caractère via SendInput en frappe Unicode — jamais via le presse-papiers, qui
/// n'est ni lu ni modifié ici (voir <see cref="UiaFieldAccessor"/> pour la
/// lecture/écriture d'un champ sans source tapée, via UI Automation).
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
        // Normalise tous les sauts de ligne (\r\n, \r seul) en \n : un seul marqueur
        // canonique à traiter plus bas, pour ne jamais injecter un \r isolé ni
        // doubler l'effet d'une paire \r\n.
        string normalized = replacementText.Replace("\r\n", "\n").Replace('\r', '\n');

        int backspaceCount = Math.Max(0, charsToDelete);
        var inputs = new List<NativeInput.INPUT>(backspaceCount * 2 + normalized.Length * 2);

        for (int b = 0; b < backspaceCount; b++)
        {
            inputs.Add(NativeInput.KeyInput(NativeInput.VK_BACK, keyUp: false));
            inputs.Add(NativeInput.KeyInput(NativeInput.VK_BACK, keyUp: true));
        }

        // On itère sur les `char` (unités UTF-16), pas sur les points de code : un
        // caractère hors du Plan de base (émoji...) occupe deux `char` consécutifs
        // (paire de substitution), chacun devient naturellement un évènement Unicode
        // distinct — Windows/l'application cible recombine la paire à la réception.
        foreach (char c in normalized)
        {
            if (c == '\n')
            {
                // Maj+Entrée, JAMAIS Entrée seule ni le caractère Unicode brut \n/\r :
                // Windows traduit un appui réel sur Entrée en WM_CHAR(0x0D) — injecter
                // ce caractère produit exactement le même évènement, ce qui envoie le
                // message dans les messageries (WhatsApp, Slack, Teams...) au lieu d'y
                // insérer un saut de ligne. Maj+Entrée est le raccourci universellement
                // reconnu par ces applications pour "nouvelle ligne, ne pas envoyer" —
                // et dans un simple champ multiligne (Bloc-notes...), Maj+Entrée insère
                // une ligne exactement comme Entrée seule : aucune régression là où
                // l'ancien comportement était déjà sûr.
                inputs.Add(NativeInput.KeyInput(NativeInput.VK_SHIFT, keyUp: false));
                inputs.Add(NativeInput.KeyInput(NativeInput.VK_RETURN, keyUp: false));
                inputs.Add(NativeInput.KeyInput(NativeInput.VK_RETURN, keyUp: true));
                inputs.Add(NativeInput.KeyInput(NativeInput.VK_SHIFT, keyUp: true));
            }
            else
            {
                inputs.Add(NativeInput.UnicodeKeyInput(c, keyUp: false));
                inputs.Add(NativeInput.UnicodeKeyInput(c, keyUp: true));
            }
        }

        NativeInput.SendInputChecked([.. inputs]);
    }
}
