using Donna.Ai;
using Xunit;

namespace Donna.Tests;

public class GeminiClientTests
{
    [Fact]
    public void BuildInput_prompt_seul_renvoie_le_prompt()
    {
        Assert.Equal("écris un haïku sur la mer", GeminiClient.BuildInput("", "écris un haïku sur la mer"));
    }

    [Fact]
    public void BuildInput_source_seule_applique_action_par_defaut()
    {
        string input = GeminiClient.BuildInput("réunion demain 14h", "");

        Assert.Contains(GeminiClient.DefaultAction, input);
        Assert.Contains("réunion demain 14h", input);
    }

    [Fact]
    public void BuildInput_source_et_prompt_combine_les_deux()
    {
        string input = GeminiClient.BuildInput("Bonjour je voulais savoir si le devis est pret", "rends ça formel");

        Assert.Contains("rends ça formel", input);
        Assert.Contains("Bonjour je voulais savoir si le devis est pret", input);
    }

    [Fact]
    public void IsQuotaExceeded_detecte_resource_exhausted()
    {
        const string body = """{ "error": { "code": 429, "message": "quota", "status": "RESOURCE_EXHAUSTED" } }""";
        Assert.True(GeminiClient.IsQuotaExceeded(body));
    }

    [Fact]
    public void IsQuotaExceeded_faux_pour_erreur_invalid_argument()
    {
        const string body = """{ "error": { "code": 400, "message": "Request contains an invalid argument.", "status": "INVALID_ARGUMENT" } }""";
        Assert.False(GeminiClient.IsQuotaExceeded(body));
    }

    [Fact]
    public void IsQuotaExceeded_faux_pour_autre_erreur()
    {
        const string body = """{ "error": { "code": 400, "message": "bad request", "status": "INVALID_ARGUMENT" } }""";
        Assert.False(GeminiClient.IsQuotaExceeded(body));
    }

    [Fact]
    public void IsQuotaExceeded_faux_pour_json_invalide()
    {
        Assert.False(GeminiClient.IsQuotaExceeded("pas du json"));
    }

    [Fact]
    public void IsQuotaExceeded_faux_pour_racine_de_type_tableau()
    {
        // Format observé pour certaines erreurs d'infrastructure Google (mauvais
        // endpoint, etc.) : tableau à la racine au lieu d'un objet — ne doit jamais planter.
        const string body = """[ { "error": { "code": 401, "message": "unauthenticated", "status": "UNAUTHENTICATED" } } ]""";
        Assert.False(GeminiClient.IsQuotaExceeded(body));
    }

    [Fact]
    public void ExtractOutputText_lit_candidates_content_parts_text()
    {
        const string body = """
        {
          "candidates": [
            { "content": { "parts": [ { "text": "Bonjour " }, { "text": "tout le monde" } ], "role": "model" } }
          ]
        }
        """;
        Assert.Equal("Bonjour tout le monde", GeminiClient.ExtractOutputText(body));
    }

    [Fact]
    public void ExtractOutputText_leve_si_rien_d_exploitable()
    {
        const string body = """{ "candidates": [] }""";
        Assert.Throws<AiApiException>(() => GeminiClient.ExtractOutputText(body));
    }
}
