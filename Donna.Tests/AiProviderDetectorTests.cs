using Donna.Ai;
using Xunit;

namespace Donna.Tests;

public class AiProviderDetectorTests
{
    [Fact]
    public void Detecte_groq_sur_prefixe_gsk()
    {
        Assert.Equal(AiProvider.Groq, AiProviderDetector.Detect("gsk_abc123"));
    }

    [Fact]
    public void Detecte_gemini_sur_prefixe_aiza()
    {
        Assert.Equal(AiProvider.Gemini, AiProviderDetector.Detect("AIzaSyABC123"));
    }

    [Fact]
    public void Retombe_sur_gemini_pour_un_prefixe_inconnu()
    {
        Assert.Equal(AiProvider.Gemini, AiProviderDetector.Detect("AQ.Ab8RN6JEh"));
    }
}
