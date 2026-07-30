using Donna.Ai;
using Xunit;

namespace Donna.Tests;

public class GroqClientTests
{
    [Fact]
    public void ExtractOutputText_lit_choices_message_content()
    {
        const string body = """
        {
          "choices": [
            { "message": { "role": "assistant", "content": "Bonjour tout le monde" } }
          ]
        }
        """;
        Assert.Equal("Bonjour tout le monde", GroqClient.ExtractOutputText(body));
    }

    [Fact]
    public void ExtractOutputText_leve_si_rien_d_exploitable()
    {
        const string body = """{ "choices": [] }""";
        Assert.Throws<AiApiException>(() => GroqClient.ExtractOutputText(body));
    }
}
