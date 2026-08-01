namespace Donna.Core;

/// <summary>Mode de transformation choisi pour une formule détectée par <see cref="TypingBuffer"/>.</summary>
public enum TransformMode
{
    /// <summary>Source tapée au clavier avant le déclencheur — injection via TextInjector.</summary>
    TypedSource,

    /// <summary>Rien tapé avant le déclencheur, mais du texte réel lu dans le champ via UI Automation.</summary>
    UiaSource,

    /// <summary>
    /// Rien tapé avant le déclencheur, et rien d'exploitable lu dans le champ
    /// (application non supportée, ou curseur pas en fin de champ) — génération
    /// pure à partir du seul prompt.
    /// </summary>
    PureGeneration,
}

/// <summary>
/// Décide, à partir d'une formule détectée et (le cas échéant) du texte lu dans
/// le champ via UI Automation, quel des trois modes s'applique. Logique pure,
/// testable sans dépendance Win32/COM : <paramref name="uiaFieldText"/> est déjà
/// le résultat (ou l'absence de résultat, <see langword="null"/>) d'une lecture
/// faite ailleurs (<see cref="Input.UiaFieldAccessor"/>).
///
/// Point essentiel (régression corrigée) : une lecture UIA absente ou
/// inexploitable ne doit JAMAIS être traitée comme une erreur bloquante pour la
/// génération pure — une source vide recouvre deux situations différentes
/// (texte collé à lire, ou aucune source voulue du tout), et l'échec de la
/// première ne doit jamais empêcher la seconde.
/// </summary>
public static class TransformModeSelector
{
    public static (TransformMode Mode, string Source) SelectMode(TriggerMatch trigger, string? uiaFieldText)
    {
        if (trigger.Source.Length > 0)
            return (TransformMode.TypedSource, trigger.Source);

        if (uiaFieldText is not null
            && trigger.TryExtractSourceFromFieldText(uiaFieldText, out string extracted)
            && extracted.Length > 0)
        {
            return (TransformMode.UiaSource, extracted);
        }

        return (TransformMode.PureGeneration, "");
    }
}
