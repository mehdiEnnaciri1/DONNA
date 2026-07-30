namespace Donna.Ai;

/// <summary>Fournisseur d'IA identifié à partir du format d'une clé API.</summary>
public enum AiProvider
{
    Gemini,
    Groq,
}

/// <summary>
/// Détecte le fournisseur d'une clé API à partir de son préfixe, pour permettre
/// à DONNA de mélanger des clés de plusieurs fournisseurs dans le même trousseau
/// (<see cref="Donna.Config.KeyRing"/>) et d'appeler le bon client pour chacune.
/// </summary>
public static class AiProviderDetector
{
    /// <summary>Clés Groq : préfixe <c>gsk_</c>. Tout le reste est tenté comme une clé Gemini
    /// (clés Gemini réelles : préfixe <c>AIza</c>), y compris un préfixe inconnu — dans ce cas
    /// l'appel échouera avec une erreur d'authentification explicite plutôt que de bloquer.</summary>
    public static AiProvider Detect(string apiKey) =>
        apiKey.StartsWith("gsk_", StringComparison.Ordinal) ? AiProvider.Groq : AiProvider.Gemini;
}
