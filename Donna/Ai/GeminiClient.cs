using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Donna.Ai;

/// <summary>
/// Appel REST vers l'API publique Gemini <c>generateContent</c> — la seule
/// des deux API Gemini de Google qui accepte une simple clé API
/// (<c>x-goog-api-key</c>). L'API « Interactions »
/// (<c>v1beta/interactions</c>) utilisée avant exige un token OAuth2 complet
/// (401 <c>ACCESS_TOKEN_TYPE_UNSUPPORTED</c> avec une clé API — vérifié en
/// conditions réelles le 28/07/2026), incompatible avec un outil grand public
/// qui ne stocke qu'une clé API.
/// </summary>
public sealed class GeminiClient : IDisposable
{
    private const string EndpointTemplate = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";

    // Cadre le modèle pour qu'il ne renvoie que le résultat brut ; ResponseCleaner
    // reste un filet de sécurité par-dessus au cas où le modèle n'obéit pas.
    // Public : réutilisée telle quelle par GroqClient (même consigne, quel que soit le fournisseur).
    public const string SystemInstruction =
        "Tu es un outil de transformation de texte intégré à un logiciel. " +
        "Réponds UNIQUEMENT par le résultat final : sans préambule, sans " +
        "guillemets encadrants, sans bloc markdown, sans commentaire sur ta réponse.";

    /// <summary>
    /// Action appliquée quand l'utilisateur tape une source sans instruction
    /// (« &lt;texte&gt; donna␣␣ », cf. ARCHITECTURE.md §1 « source seule »).
    /// </summary>
    public const string DefaultAction =
        "Corrige uniquement l'orthographe et la grammaire de ce texte, sans changer le style ni le sens.";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="httpClient">
    /// Client à réutiliser (ex. singleton applicatif). Si omis, un client est
    /// créé et détruit avec cette instance.
    /// </param>
    public GeminiClient(HttpClient? httpClient = null)
    {
        // Sans timeout explicite, une requête bloquée (réseau capricieux,
        // proxy antivirus qui retient le trafic pendant une analyse, etc.)
        // resterait plantée jusqu'aux 100s par défaut de HttpClient — bien
        // trop long pour un outil censé répondre en ~1 seconde.
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>
    /// Envoie <paramref name="source"/> + <paramref name="prompt"/> à Gemini et
    /// renvoie le texte généré. Lève <see cref="AiQuotaExceededException"/>
    /// si la clé a atteint son quota (à charge de l'appelant de faire tourner
    /// le <see cref="Donna.Config.KeyRing"/> et de réessayer).
    /// </summary>
    public async Task<string> GenerateAsync(
        string apiKey, string model, string source, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("La clé API ne peut pas être vide.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Le modèle ne peut pas être vide.", nameof(model));

        string endpoint = string.Format(EndpointTemplate, model);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            systemInstruction = new { parts = new[] { new { text = SystemInstruction } } },
            contents = new[] { new { parts = new[] { new { text = BuildInput(source, prompt) } } } },
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || IsQuotaExceeded(body))
                throw new AiQuotaExceededException(body);

            throw new AiApiException((int)response.StatusCode, body);
        }

        return ExtractOutputText(body);
    }

    // ---- Logique pure ci-dessous : aucun appel réseau, testable directement. ----

    /// <summary>Combine source et prompt selon les 3 cas de ARCHITECTURE.md §1.</summary>
    public static string BuildInput(string source, string prompt)
    {
        source = source?.Trim() ?? "";
        prompt = prompt?.Trim() ?? "";

        if (source.Length == 0)
            return prompt; // prompt seul → génération pure

        if (prompt.Length == 0)
            return $"{DefaultAction}\n\nTexte :\n{source}"; // source seule → action par défaut

        return $"{prompt}\n\nTexte :\n{source}"; // les deux → instruction appliquée au texte
    }

    /// <summary>
    /// Détecte une erreur de quota dans le corps JSON d'une réponse d'erreur :
    /// <c>{ "error": { "code": 429, "message": "...", "status": "RESOURCE_EXHAUSTED" } }</c>,
    /// le format d'erreur standard de l'API Google. Le signal le plus fiable
    /// reste de toute façon le code HTTP 429 lui-même, déjà vérifié dans
    /// <see cref="GenerateAsync"/> avant d'appeler cette méthode — ceci couvre
    /// le cas où le quota est signalé dans le corps sans statut HTTP 429.
    /// </summary>
    public static bool IsQuotaExceeded(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            // Certaines erreurs d'infrastructure Google (ex. URL invalide) renvoient un
            // tableau à la racine (`[{ "error": {...} }]`) au lieu d'un objet — à ne
            // jamais supposer, TryGetProperty plante sur un ValueKind autre que Object.
            JsonElement errorHolder = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            if (errorHolder.ValueKind != JsonValueKind.Object || !errorHolder.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (error.TryGetProperty("status", out var status)
                && status.GetString() == "RESOURCE_EXHAUSTED")
            {
                return true;
            }

            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                string codeStr = code.GetString() ?? "";
                return codeStr.Contains("resource_exhausted", StringComparison.OrdinalIgnoreCase)
                    || codeStr.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                    || codeStr.Contains("quota", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extrait le texte généré d'une réponse <c>generateContent</c> :
    /// <c>candidates[0].content.parts[].text</c> (plusieurs parts si la
    /// réponse est découpée, concaténées dans l'ordre).
    /// </summary>
    public static string ExtractOutputText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
            }

            if (sb.Length > 0)
                return sb.ToString();
        }

        throw new AiApiException(0, $"Réponse Gemini inattendue, impossible d'en extraire le texte : {responseJson}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
