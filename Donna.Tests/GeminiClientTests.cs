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
    public void IsQuotaExceeded_detecte_le_format_code_string_observe_en_direct()
    {
        // Format réellement renvoyé par l'API Interactions (vérifié le 28/07/2026),
        // différent du format "status" façon Google classique.
        const string body = """{ "error": { "message": "quota", "code": "resource_exhausted" } }""";
        Assert.True(GeminiClient.IsQuotaExceeded(body));
    }

    [Fact]
    public void IsQuotaExceeded_faux_pour_erreur_invalid_request_reelle()
    {
        // Erreur 400 réellement observée en direct pour une requête mal formée.
        const string body = """{ "error": { "message": "Request contains an invalid argument.", "code": "invalid_request" } }""";
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
    public void ExtractOutputText_lit_output_text()
    {
        const string body = """{ "id": "1", "output_text": "Bonjour tout le monde" }""";
        Assert.Equal("Bonjour tout le monde", GeminiClient.ExtractOutputText(body));
    }

    [Fact]
    public void ExtractOutputText_repli_sur_steps_si_output_text_absent()
    {
        const string body = """
        {
          "id": "1",
          "steps": [
            { "type": "model_output", "content": [ { "text": "Bonjour " }, { "text": "tout le monde" } ] }
          ]
        }
        """;
        Assert.Equal("Bonjour tout le monde", GeminiClient.ExtractOutputText(body));
    }

    [Fact]
    public void ExtractOutputText_leve_si_rien_d_exploitable()
    {
        const string body = """{ "id": "1", "status": "completed" }""";
        Assert.Throws<GeminiApiException>(() => GeminiClient.ExtractOutputText(body));
    }
}
