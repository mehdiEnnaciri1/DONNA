using Interop.UIAutomationClient;

namespace Donna.Input;

/// <summary>
/// Lit et écrit le contenu d'un champ de saisie via UI Automation (COM,
/// <c>Interop.UIAutomationClient</c>) — remplace l'ancienne lecture par
/// sélection clavier (Maj/Ctrl+Origine puis Ctrl+C), qui a détruit des
/// documents entiers en conditions réelles : une désélection de secours pouvait
/// être traitée par l'application AVANT la sélection elle-même (SendInput étant
/// asynchrone), laissant tout sélectionné sans rien pour le relâcher.
///
/// Ici, aucune touche n'est injectée, aucun presse-papiers n'est touché, aucune
/// sélection n'est créée : la lecture et l'écriture passent uniquement par les
/// patterns UI Automation (ValuePattern et TextPattern, le résultat exploitable
/// étant choisi dynamiquement — voir <see cref="TryReadFocusedField"/>), qui
/// n'ont aucun effet de bord sur le champ tant qu'on n'appelle pas explicitement
/// <see cref="TryWrite"/>. La destruction devient impossible par construction.
///
/// Limitation connue et acceptée : les applications qui ne rendent pas leur
/// contenu accessible via ces patterns (notamment l'éditeur Monaco de VS Code,
/// dont le contenu de code n'est exposé qu'en mode lecteur d'écran) ne sont pas
/// supportées — <see cref="TryReadFocusedField"/> renvoie alors <c>null</c>, et
/// l'appelant doit abandonner proprement avec un message clair plutôt que
/// deviner un contenu ou se rabattre sur une sélection clavier.
///
/// DOIT être utilisée depuis un thread MTA (recommandation Microsoft pour les
/// clients UI Automation — à l'inverse du presse-papiers, qui exigeait STA).
/// Voir DonnaContext, qui encapsule chaque appel dans un <c>Task.Run</c> à cet effet.
/// </summary>
public sealed class UiaFieldAccessor
{
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    /// <summary>Élément UI Automation ciblé et texte lu, pour une lecture réussie.</summary>
    public sealed record ReadResult(IUIAutomationElement Element, string Text);

    /// <summary>
    /// Lit le contenu du champ actuellement focalisé (tout Windows confondu), en
    /// essayant ValuePattern ET TextPattern, et en retenant celui des deux dont le
    /// texte est réellement exploitable : non vide, et se terminant par
    /// <paramref name="expectedSuffix"/> (la formule que DONNA a vue taper — voir
    /// <see cref="Core.TriggerMatch.TypedSuffix"/>).
    ///
    /// Ce double critère est nécessaire : sur un <c>contenteditable</c> (WhatsApp
    /// Web, Slack, Gmail...), ValuePattern est souvent *supporté* mais renvoie une
    /// chaîne vide (le vrai contenu n'est exposé que via TextPattern) — un simple
    /// repli <c>??</c> sur "supporté ou pas" ne suffit pas, puisqu'une chaîne vide
    /// n'est pas <see langword="null"/> : il faut choisir activement le résultat
    /// qui contient réellement ce qu'on cherche, pas le premier qui existe.
    ///
    /// Renvoie <see langword="null"/> si aucun des deux ne convient — application
    /// non supportée, ou curseur qui n'était pas en fin de champ au moment du
    /// déclenchement. DONNA doit alors abandonner proprement (message clair),
    /// jamais deviner un contenu ni se rabattre sur une sélection clavier.
    /// </summary>
    public ReadResult? TryReadFocusedField(string expectedSuffix)
    {
        var automation = new CUIAutomation();
        IUIAutomationElement? element = automation.GetFocusedElement();
        if (element is null)
            return null;

        string? valueText = TryReadValuePattern(element);
        if (IsUsable(valueText, expectedSuffix))
            return new ReadResult(element, valueText!);

        string? textPatternText = TryReadTextPattern(element);
        if (IsUsable(textPatternText, expectedSuffix))
            return new ReadResult(element, textPatternText!);

        return null;
    }

    private static bool IsUsable(string? text, string expectedSuffix) =>
        !string.IsNullOrEmpty(text) && text.EndsWith(expectedSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Lit le contenu du champ actuellement focalisé sans exigence de
    /// correspondance particulière — retient le résultat le plus informatif
    /// entre ValuePattern et TextPattern (le plus long des deux non
    /// <see langword="null"/>, ValuePattern à égalité). Sert de sonde générale
    /// pendant le repli clavier (<see cref="VerifiedFieldWriter"/>) pour vérifier
    /// l'état courant du champ après un Backspace ou une injection — pas pour
    /// détecter la bonne source initiale (voir <see cref="TryReadFocusedField"/>
    /// pour ça, qui a besoin de la formule attendue).
    /// </summary>
    public ReadResult? TryReadFocusedFieldRaw()
    {
        var automation = new CUIAutomation();
        IUIAutomationElement? element = automation.GetFocusedElement();
        if (element is null)
            return null;

        string? valueText = TryReadValuePattern(element);
        string? textPatternText = TryReadTextPattern(element);

        string? best = (valueText, textPatternText) switch
        {
            (null, null) => null,
            (not null, null) => valueText,
            (null, not null) => textPatternText,
            _ => textPatternText!.Length > valueText!.Length ? textPatternText : valueText,
        };

        return best is null ? null : new ReadResult(element, best);
    }

    /// <summary>
    /// Écrit <paramref name="newText"/> dans <paramref name="element"/> via
    /// <c>ValuePattern.SetValue</c> — une seule opération atomique : soit elle
    /// aboutit entièrement, soit rien ne change côté DONNA (on ne fait qu'un seul
    /// appel, pas de séquence effacer-puis-coller qui pourrait s'interrompre à
    /// mi-chemin). Relit ensuite la valeur pour VÉRIFIER qu'elle correspond :
    /// certaines applications web (React et consorts, qui contrôlent la valeur
    /// de leurs champs par leur propre état interne) peuvent accepter
    /// l'écriture visuellement sans que leur état interne soit mis à jour — la
    /// relecture est le seul moyen fiable de détecter ce cas côté DONNA.
    /// Renvoie <see langword="false"/> si le pattern n'est pas disponible en
    /// écriture, si l'élément n'est plus valide (application fermée, contrôle
    /// détruit...), ou si la relecture ne correspond pas à ce qu'on vient d'écrire.
    /// </summary>
    public bool TryWrite(IUIAutomationElement element, string newText)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_ValuePatternId) is not IUIAutomationValuePattern vp)
                return false;
            if (vp.CurrentIsReadOnly != 0)
                return false;

            vp.SetValue(newText);

            string after = vp.CurrentValue ?? "";
            return after == newText;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadValuePattern(IUIAutomationElement element)
    {
        try
        {
            return element.GetCurrentPattern(UIA_ValuePatternId) is IUIAutomationValuePattern vp
                ? vp.CurrentValue ?? ""
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadTextPattern(IUIAutomationElement element)
    {
        try
        {
            return element.GetCurrentPattern(UIA_TextPatternId) is IUIAutomationTextPattern tp
                ? tp.DocumentRange.GetText(-1) ?? ""
                : null;
        }
        catch
        {
            return null;
        }
    }
}
