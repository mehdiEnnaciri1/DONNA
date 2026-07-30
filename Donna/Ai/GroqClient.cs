using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Donna.Ai;

/// <summary>
/// Appel REST vers l'API Groq (compatible OpenAI — <c>/openai/v1/chat/completions</c>),
/// utilisée quand une clé du trousseau est détectée comme une clé Groq
/// (préfixe <c>gsk_</c>, voir <see cref="AiProviderDetector"/>).
/// </summary>
public sealed class GroqClient : IDisposable
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    // Modèle Groq par défaut : DONNA n'expose qu'un seul champ modèle dans Réglages
    // (pensé pour les noms de modèles Gemini) — pas encore de sélecteur par fournisseur.
    public const string DefaultModel = "llama-3.3-70b-versatile";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public GroqClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>
    /// Envoie <paramref name="source"/> + <paramref name="prompt"/> à Groq et
    /// renvoie le texte généré. Lève <see cref="AiQuotaExceededException"/> si
    /// la clé a atteint sa limite de débit (à charge de l'appelant de faire
    /// tourner le <see cref="Donna.Config.KeyRing"/> et de réessayer).
    /// </summary>
    public async Task<string> GenerateAsync(
        string apiKey, string source, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("La clé API ne peut pas être vide.", nameof(apiKey));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = DefaultModel,
            messages = new[]
            {
                new { role = "system", content = GeminiClient.SystemInstruction },
                new { role = "user", content = GeminiClient.BuildInput(source, prompt) },
            },
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AiQuotaExceededException(body);

            throw new AiApiException((int)response.StatusCode, body);
        }

        return ExtractOutputText(body);
    }

    /// <summary>Extrait le texte généré d'une réponse <c>chat/completions</c> : <c>choices[0].message.content</c>.</summary>
    public static string ExtractOutputText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        throw new AiApiException(0, $"Réponse Groq inattendue, impossible d'en extraire le texte : {responseJson}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
