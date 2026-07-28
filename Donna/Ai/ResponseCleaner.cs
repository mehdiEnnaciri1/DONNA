using System.Text.RegularExpressions;

namespace Donna.Ai;

/// <summary>
/// Filet de sécurité appliqué à la réponse brute de Gemini avant collage :
/// enlève les préambules (« Voici… »), les guillemets encadrants et les
/// blocs markdown, même si le prompt demandait déjà une réponse « brute ».
/// Aucune dépendance Win32 ni réseau → entièrement testable.
/// </summary>
public static class ResponseCleaner
{
    // Bloc de code markdown qui enveloppe TOUTE la réponse : ```lang\n...\n```
    private static readonly Regex CodeFence = new(
        @"^```[^\n]*\n([\s\S]*?)\n?```$",
        RegexOptions.Compiled);

    // Préambule en tête de réponse, du type « Voici la version reformulée : »,
    // « Bien sûr, voici : », « Here is the result: ». Ne matche qu'en tout début
    // de chaîne et seulement s'il reste du texte après les deux-points.
    private static readonly Regex Preamble = new(
        @"^\s*(?:bien s[uû]r\s*,?\s*|certainement\s*,?\s*|sure\s*,?\s*)?" +
        @"(?:voici|voil[aà]|here(?:'s| is))\b[^:\n]*:\s+(?=\S)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Paires de guillemets encadrants reconnues (droits, simples, français, typographiques).
    private static readonly (char Open, char Close)[] QuotePairs =
    [
        ('"', '"'), ('\'', '\''), ('«', '»'), ('“', '”'), ('‘', '’')
    ];

    /// <summary>
    /// Nettoie une réponse Gemini. Ne renvoie jamais une chaîne vide si
    /// l'entrée ne l'était pas : en cas de doute, on préfère garder trop de
    /// texte plutôt que d'effacer une réponse valide.
    /// </summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        string original = raw.Trim();
        string current = original;

        // Plusieurs passes : un préambule peut précéder un bloc entre guillemets
        // ou un bloc markdown, et inversement.
        for (int pass = 0; pass < 5; pass++)
        {
            string before = current;

            current = StripCodeFence(current);
            current = StripPreamble(current);
            current = StripEnclosingQuotes(current);
            current = current.Trim();

            if (current == before)
                break;
        }

        return current.Length == 0 ? original : current;
    }

    private static string StripCodeFence(string text)
    {
        var match = CodeFence.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : text;
    }

    private static string StripPreamble(string text)
    {
        var match = Preamble.Match(text);
        return match.Success ? text[match.Length..] : text;
    }

    private static string StripEnclosingQuotes(string text)
    {
        if (text.Length < 2)
            return text;

        foreach (var (open, close) in QuotePairs)
        {
            if (text[0] == open && text[^1] == close)
                return text[1..^1];
        }

        return text;
    }
}
