using Donna.Input;
using Xunit;

namespace Donna.Tests;

public class KeyTranslatorTests
{
    // Ces 4 touches sont filtrées AVANT tout appel Win32 (voir KeyTranslator.Translate) :
    // le test n'atteint donc jamais ToUnicodeEx et reste déterministe sur n'importe quelle machine.
    [Theory]
    [InlineData(0x08)] // VK_BACK
    [InlineData(0x09)] // VK_TAB
    [InlineData(0x0D)] // VK_RETURN
    [InlineData(0x1B)] // VK_ESCAPE
    public void Translate_touches_de_controle_ne_renvoie_rien(uint vkCode)
    {
        var translator = new KeyTranslator();
        Assert.Equal("", translator.Translate(vkCode, scanCode: 0));
    }
}
