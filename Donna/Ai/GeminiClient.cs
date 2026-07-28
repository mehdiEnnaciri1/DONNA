using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Donna.Ai;

/// <summary>
/// Appel REST vers l'API Gemini « Interactions » (v1beta/interactions),
/// qui remplace progressivement l'ancienne API generateContent — voir
/// ARCHITECTURE.md §7 point 4 : à revérifier si Google fait encore évoluer
/// le format, cette API a été introduite courant 2026.
/// </summary>
public sealed class GeminiClient : IDisposable
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";

    // Cadre le modèle pour qu'il ne renvoie que le résultat brut ; ResponseCleaner
    // reste un filet de sécurité par-dessus au cas où le modèle n'obéit pas.
    private const string SystemInstruction =
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
    /// renvoie le texte généré. Lève <see cref="GeminiQuotaExceededException"/>
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

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            system_instruction = SystemInstruction,
            input = BuildInput(source, prompt),
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || IsQuotaExceeded(body))
                throw new GeminiQuotaExceededException(body);

            throw new GeminiApiException((int)response.StatusCode, body);
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
    /// Détecte une erreur de quota dans le corps JSON d'une réponse d'erreur.
    /// Vérifié en direct le 28/07/2026 : l'API Interactions renvoie
    /// <c>{ "error": { "code": "invalid_request", "message": "..." } }</c> — un
    /// <c>code</c> en string, façon OpenAI, PAS le <c>status</c> enum de
    /// l'ancienne convention Google ({"status":"RESOURCE_EXHAUSTED"}, encore
    /// documentée ailleurs). On vérifie les deux formats par prudence : cette
    /// API est jeune et peut encore changer (voir ARCHITECTURE.md §7.4). Le
    /// signal le plus fiable reste de toute façon le code HTTP 429 lui-même,
    /// déjà vérifié dans <see cref="GenerateAsync"/> avant d'appeler cette méthode.
    /// </summary>
    public static bool IsQuotaExceeded(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return false;

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

    /// <summary>Extrait le texte généré d'une réponse JSON de l'API Interactions.</summary>
    public static string ExtractOutputText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // "output_text" n'apparaît PAS dans la réponse REST brute (vérifié en
        // direct le 28/07/2026) — c'est un confort ajouté par les SDK officiels.
        // On le garde en priorité si jamais Google l'ajoute un jour côté REST,
        // mais le vrai chemin est steps[].content[].text ci-dessous.
        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(outputText.GetString()))
        {
            return outputText.GetString()!;
        }

        // Chemin réel : reconstruit le texte à partir des steps de type
        // "model_output" (on ignore les steps "thought" et autres).
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("type", out var stepType) && stepType.GetString() != "model_output")
                    continue;

                if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        sb.Append(text.GetString());
                }
            }

            if (sb.Length > 0)
                return sb.ToString();
        }

        throw new GeminiApiException(0, $"Réponse Gemini inattendue, impossible d'en extraire le texte : {responseJson}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

/// <summary>Erreur API Gemini non liée au quota (requête invalide, panne serveur, etc.).</summary>
public sealed class GeminiApiException(int statusCode, string responseBody)
    : Exception($"Erreur API Gemini ({statusCode}) : {responseBody}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>La clé API utilisée a atteint son quota — à faire tourner via KeyRing.</summary>
public sealed class GeminiQuotaExceededException(string responseBody)
    : Exception("Quota Gemini dépassé pour cette clé API.")
{
    public string ResponseBody { get; } = responseBody;
}
