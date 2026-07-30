namespace Donna.Ai;

/// <summary>Erreur API (Gemini ou Groq) non liée au quota (requête invalide, panne serveur, clé invalide...).</summary>
public sealed class AiApiException(int statusCode, string responseBody)
    : Exception($"Erreur API ({statusCode}) : {responseBody}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>La clé API utilisée a atteint son quota — à faire tourner via KeyRing.</summary>
public sealed class AiQuotaExceededException(string responseBody)
    : Exception("Quota dépassé pour cette clé API.")
{
    public string ResponseBody { get; } = responseBody;
}
