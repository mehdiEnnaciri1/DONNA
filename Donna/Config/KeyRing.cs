namespace Donna.Config;

/// <summary>
/// Trousseau de clés API (Gemini et/ou Groq, mélangées) : expose la clé
/// courante et bascule sur la clé suivante disponible dès que la courante
/// échoue, quelle que soit la raison (quota, clé invalide, mauvais
/// fournisseur...) — DONNA n'a aucune garantie a priori qu'une clé donnée
/// fonctionne, chaque échec fait donc avancer le trousseau. Ne dépend
/// d'aucune API Win32 ni réseau → entièrement testable.
/// </summary>
public sealed class KeyRing
{
    private readonly IReadOnlyList<string> _keys;
    private readonly bool[] _failed;
    private int _currentIndex;

    public KeyRing(IEnumerable<string> keys)
    {
        _keys = keys?.ToList() ?? throw new ArgumentNullException(nameof(keys));
        if (_keys.Count == 0)
            throw new ArgumentException("Le trousseau doit contenir au moins une clé.", nameof(keys));

        _failed = new bool[_keys.Count];
        _currentIndex = 0;
    }

    /// <summary>Clé à utiliser pour le prochain appel, ou null si tout le trousseau a échoué.</summary>
    public string? CurrentKey => _failed[_currentIndex] ? null : _keys[_currentIndex];

    /// <summary>Vrai si toutes les clés du trousseau ont échoué.</summary>
    public bool IsExhausted => Array.TrueForAll(_failed, e => e);

    /// <summary>
    /// Marque la clé courante comme en échec (quota dépassé, invalide, mauvais
    /// fournisseur...) et bascule sur la clé suivante disponible, dans l'ordre
    /// du trousseau, en bouclant depuis le début.
    /// </summary>
    /// <returns>La nouvelle clé courante, ou null si le trousseau est épuisé.</returns>
    public string? MarkCurrentAsFailed()
    {
        _failed[_currentIndex] = true;

        for (int offset = 1; offset <= _keys.Count; offset++)
        {
            int candidate = (_currentIndex + offset) % _keys.Count;
            if (!_failed[candidate])
            {
                _currentIndex = candidate;
                return _keys[candidate];
            }
        }

        return null;
    }
}
