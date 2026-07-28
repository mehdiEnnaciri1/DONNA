namespace Donna.Config;

/// <summary>
/// Trousseau de clés API Gemini : expose la clé courante et bascule sur la
/// clé suivante disponible quand la courante atteint son quota. Ne dépend
/// d'aucune API Win32 ni réseau → entièrement testable.
/// </summary>
public sealed class KeyRing
{
    private readonly IReadOnlyList<string> _keys;
    private readonly bool[] _exhausted;
    private int _currentIndex;

    public KeyRing(IEnumerable<string> keys)
    {
        _keys = keys?.ToList() ?? throw new ArgumentNullException(nameof(keys));
        if (_keys.Count == 0)
            throw new ArgumentException("Le trousseau doit contenir au moins une clé.", nameof(keys));

        _exhausted = new bool[_keys.Count];
        _currentIndex = 0;
    }

    /// <summary>Clé à utiliser pour le prochain appel, ou null si tout le trousseau est épuisé.</summary>
    public string? CurrentKey => _exhausted[_currentIndex] ? null : _keys[_currentIndex];

    /// <summary>Vrai si toutes les clés du trousseau ont atteint leur quota.</summary>
    public bool IsExhausted => Array.TrueForAll(_exhausted, e => e);

    /// <summary>
    /// Marque la clé courante comme ayant atteint son quota (ou renvoyé une
    /// erreur de limite) et bascule sur la clé suivante disponible, dans
    /// l'ordre du trousseau, en bouclant depuis le début.
    /// </summary>
    /// <returns>La nouvelle clé courante, ou null si le trousseau est épuisé.</returns>
    public string? MarkCurrentAsQuotaExceeded()
    {
        _exhausted[_currentIndex] = true;

        for (int offset = 1; offset <= _keys.Count; offset++)
        {
            int candidate = (_currentIndex + offset) % _keys.Count;
            if (!_exhausted[candidate])
            {
                _currentIndex = candidate;
                return _keys[candidate];
            }
        }

        return null;
    }
}
