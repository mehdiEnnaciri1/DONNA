using System.Text;

namespace Donna.Core;

/// <summary>
/// Cœur logique de DONNA : reconstruit le texte tapé depuis le dernier
/// « reset » et détecte la formule de déclenchement :
///
///     &lt;source&gt; donna &lt;prompt&gt;␣␣
///
/// Ne dépend d'AUCUNE API Win32 → entièrement testable en xUnit.
/// Séparation nette des responsabilités :
///   - KeyTranslator (Win32, non testable) fournit ici des caractères Unicode
///     déjà « propres » (AZERTY + touches mortes déjà résolues) ;
///   - TypingBuffer ne fait QUE de la logique de chaîne.
/// </summary>
public sealed class TypingBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly string _trigger;

    public TypingBuffer(string trigger = "donna")
    {
        if (string.IsNullOrWhiteSpace(trigger))
            throw new ArgumentException("Le déclencheur ne peut pas être vide.", nameof(trigger));
        _trigger = trigger.Trim().ToLowerInvariant();
    }

    /// <summary>Contenu courant du buffer (ce que DONNA « croit » avoir été tapé).</summary>
    public string Current => _buffer.ToString();

    /// <summary>Longueur courante — sert à savoir combien de Backspace envoyer.</summary>
    public int Length => _buffer.Length;

    /// <summary>
    /// Ajoute le texte imprimable produit par un appui touche (souvent 1 caractère,
    /// parfois 2 quand une touche morte se résout, ex. ^ + e → ê).
    /// Renvoie une correspondance si la formule vient d'être complétée par le
    /// double espace, sinon null.
    /// </summary>
    public TriggerMatch? Append(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        _buffer.Append(text);
        TriggerMatch? match = Detect();

        // Une formule traitée (avec succès ou pas) ne doit jamais rester dans le
        // buffer : sinon la frappe suivante s'accumule par-dessus, et la détection
        // du prochain déclencheur récupère un mélange de l'ancienne et de la
        // nouvelle formule comme source.
        if (match is not null)
            Reset();

        return match;
    }

    /// <summary>Retire le dernier caractère (touche Retour arrière).</summary>
    public void Backspace()
    {
        if (_buffer.Length > 0)
            _buffer.Remove(_buffer.Length - 1, 1);
    }

    /// <summary>
    /// Vide le buffer. À appeler DÈS QU'ON NE PEUT PLUS garantir la synchro avec
    /// le champ réel : clic souris, changement de fenêtre au premier plan, Entrée,
    /// Échap, Tab, flèches, Origine/Fin, coller manuel, etc.
    /// </summary>
    public void Reset() => _buffer.Clear();

    private TriggerMatch? Detect()
    {
        // 1) Validation = deux espaces consécutifs en toute fin de buffer.
        if (_buffer.Length < 2) return null;
        if (_buffer[^1] != ' ' || _buffer[^2] != ' ') return null;

        // 2) Corps utile, sans les 2 espaces de validation.
        string body = _buffer.ToString(0, _buffer.Length - 2);

        // 3) Localiser le token déclencheur, délimité par des espaces (dernière occurrence valide).
        int triggerStart = FindTriggerToken(body);
        if (triggerStart < 0) return null;

        int triggerEnd = triggerStart + _trigger.Length;
        string source = body[..triggerStart].Trim();
        string prompt = body[triggerEnd..].Trim();

        // 4) Il faut au moins une source OU un prompt, sinon « donna␣␣ » seul ne fait rien.
        if (source.Length == 0 && prompt.Length == 0) return null;

        // La formule complète à effacer = tout ce que DONNA a tapé (= tout le buffer).
        // On n'efface JAMAIS plus que notre propre buffer → le texte préexistant du
        // champ (chargé d'un brouillon, etc.) reste intact.
        // TriggerLength = uniquement la partie déclencheur + prompt + 2 espaces (sans
        // la source) — sert au repli UI Automation (DonnaContext) quand la source
        // tapée est vide : on n'efface alors QUE ce que l'utilisateur vient de taper,
        // jamais le texte réel (collé ou préexistant) qu'on va lire par ailleurs.
        // TypedSuffix = le texte exact de cette même portion (pas juste sa longueur) :
        // sert à VÉRIFIER, avant de découper le texte lu ailleurs, que ce texte se
        // termine bien par ce qu'on a réellement vu taper (voir TryExtractSourceFromFieldText).
        string typedSuffix = _buffer.ToString(triggerStart, _buffer.Length - triggerStart);
        return new TriggerMatch(source, prompt, _buffer.Length, typedSuffix.Length, typedSuffix);
    }

    /// <summary>
    /// Cherche le déclencheur comme MOT ENTIER (bord gauche = début ou espace,
    /// bord droit = fin ou espace). Insensible à la casse. Renvoie l'index de début
    /// de la dernière occurrence valide, ou -1.
    /// Ex. « madonna » ne déclenche PAS (bord gauche = 'a', pas un espace).
    /// </summary>
    private int FindTriggerToken(string body)
    {
        string hay = body.ToLowerInvariant();
        int search = hay.Length;

        while (search >= _trigger.Length)
        {
            int idx = hay.LastIndexOf(_trigger, search - 1, StringComparison.Ordinal);
            if (idx < 0) return -1;

            int end = idx + _trigger.Length;
            bool leftOk = idx == 0 || hay[idx - 1] == ' ';
            bool rightOk = end == hay.Length || hay[end] == ' ';

            if (leftOk && rightOk) return idx;

            // Occurrence collée à un mot (ex. « maDONNA ») → on continue plus à gauche.
            search = idx;
        }
        return -1;
    }
}

/// <summary>Résultat d'une formule complétée.</summary>
/// <param name="Source">Texte source à transformer (peut être vide → repli UI Automation, voir DonnaContext).</param>
/// <param name="Prompt">Instruction pour Gemini (peut être vide → action par défaut).</param>
/// <param name="CharsToDelete">Backspace pour effacer TOUTE la formule (source + déclencheur + prompt + 2 espaces) — chemin normal, quand la source a été tapée.</param>
/// <param name="TriggerLength">Longueur de <see cref="TypedSuffix"/> — Backspace pour effacer SEULEMENT déclencheur + prompt + 2 espaces (sans la source), chemin de repli.</param>
/// <param name="TypedSuffix">Texte exact (pas juste sa longueur) de déclencheur + prompt + 2 espaces, tel que réellement tapé — sert à vérifier qu'un texte lu ailleurs (UI Automation) se termine bien par ceci avant d'en déduire la source.</param>
public readonly record struct TriggerMatch(string Source, string Prompt, int CharsToDelete, int TriggerLength, string TypedSuffix)
{
    /// <summary>
    /// Retrouve la source dans <paramref name="fieldText"/> (le contenu complet d'un
    /// champ lu via UI Automation, quand rien n'a été tapé avant le déclencheur), en
    /// vérifiant que ce texte se termine bien par <see cref="TypedSuffix"/> — la
    /// formule exacte que DONNA a vue taper. Renvoie <see langword="false"/> si ce
    /// n'est pas le cas (le curseur n'était pas en fin de champ au moment du
    /// déclenchement, par exemple) : ne JAMAIS déduire une source par simple
    /// troncature de longueur dans ce cas, au risque de couper du vrai contenu et de
    /// laisser la formule elle-même dans la source envoyée à l'IA.
    /// </summary>
    public bool TryExtractSourceFromFieldText(string fieldText, out string source)
    {
        if (!fieldText.EndsWith(TypedSuffix, StringComparison.Ordinal))
        {
            source = "";
            return false;
        }

        // Trim, comme la source du chemin normal (Detect ci-dessus) : cohérence entre
        // les deux chemins, quelle que soit l'origine de la source.
        source = fieldText[..^TypedSuffix.Length].Trim();
        return true;
    }
}
