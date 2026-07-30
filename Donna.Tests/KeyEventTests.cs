using Donna.Input;
using Xunit;

namespace Donna.Tests;

public class KeyEventTests
{
    private const uint LLKHF_EXTENDED = 0x00000001;
    private const uint LLKHF_INJECTED = 0x00000010;

    [Fact]
    public void IsInjected_faux_sans_le_bit_correspondant()
    {
        var evt = new KeyEvent(VkCode: 0x41, ScanCode: 0x1E, Flags: 0);
        Assert.False(evt.IsInjected);
    }

    [Fact]
    public void IsInjected_vrai_pour_une_touche_virtuelle_injectee()
    {
        // Cas historique : SendInput avec un vrai code de touche (ex. Backspace).
        var evt = new KeyEvent(VkCode: 0x08, ScanCode: 0, Flags: LLKHF_INJECTED);
        Assert.True(evt.IsInjected);
    }

    [Fact]
    public void IsInjected_vrai_pour_une_frappe_unicode_injectee()
    {
        // TextInjector envoie désormais des évènements Unicode (wVk=0, KEYEVENTF_UNICODE) :
        // Windows pose LLKHF_INJECTED exactement comme pour une touche virtuelle classique,
        // donc KeyboardHook doit les ignorer de la même façon (sinon le buffer de frappe
        // de DONNA serait pollué par ses propres caractères injectés).
        var evt = new KeyEvent(VkCode: 0, ScanCode: 'é', Flags: LLKHF_INJECTED);
        Assert.True(evt.IsInjected);
    }

    [Fact]
    public void IsExtended_lit_le_bon_bit()
    {
        var evt = new KeyEvent(VkCode: 0x25, ScanCode: 0, Flags: LLKHF_EXTENDED);
        Assert.True(evt.IsExtended);
        Assert.False(evt.IsInjected);
    }
}
