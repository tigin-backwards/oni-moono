using System;
using System.Text;
using TMPro;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The same text set again (docs/cleanroom/textmeshpro.md, "The same text set again"): the
    // text property skips it, but in U59 not right after a setter from characters; SetText and
    // every SetCharArray overload count it as a change, so the text is parsed and laid out
    // again. Measured with UnityProbe's u60 scenario (TS lines, spec bcl-probe).
    public class TmpSameTextTests : EngineTest
    {
        static TextMeshProUGUI Settled(string text)
        {
            var t = TmpTestSupport.NewText(text, TmpTestSupport.Graystroke, 16f, new Vector2(200f, 50f));
            t.ForceMeshUpdate(true);
            Assert.False(t.havePropertiesChanged);
            return t;
        }

        [Fact]
        public void TheTextPropertySkipsTheSameText()
        {
            var t = Settled("Hello 123");
            t.text = "Hello 123";
            Assert.False(t.havePropertiesChanged);
            t.text = "Hello 124";
            Assert.True(t.havePropertiesChanged);
        }

        public static TheoryData<string, Action<TMP_Text>> Setters => new TheoryData<string, Action<TMP_Text>>
        {
            { "SetText(string)", t => t.SetText("Hello 123") },
            { "SetText(StringBuilder)", t => t.SetText(new StringBuilder("Hello 123")) },
            { "SetText(format, arg)", t => t.SetText("Hello {0}", 123) },
            { "SetCharArray(char[])", t => t.SetCharArray("Hello 123".ToCharArray()) },
            { "SetCharArray(char[], start, length)", t => t.SetCharArray("xHello 123x".ToCharArray(), 1, 9) },
        };

        [Theory]
        [MemberData(nameof(Setters))]
        public void TheSettersCountTheSameTextAsAChange(string name, Action<TMP_Text> set)
        {
            var t = Settled("Hello 123");
            int preRender = 0;
            t.OnPreRenderText += _ => preRender++;
            set(t);
            Assert.True(t.havePropertiesChanged, name);
            Assert.Equal("Hello 123", t.text);
            t.ForceMeshUpdate();
            Assert.Equal(1, preRender);
        }

        public static TheoryData<string, Action<TMP_Text>, bool> TextAfter => new TheoryData<string, Action<TMP_Text>, bool>
        {
            { "SetText(string)", t => t.SetText("Hello 123"), false },
            { "SetText(format, arg)", t => t.SetText("Hello {0}", 123), false },
            { "the text property", t => t.text = "Hello 123", false },
            { "SetText(StringBuilder)", t => t.SetText(new StringBuilder("Hello 123")), true },
            { "SetCharArray(char[])", t => t.SetCharArray("Hello 123".ToCharArray()), true },
            { "SetCharArray(char[], start, length)", t => t.SetCharArray("xHello 123x".ToCharArray(), 1, 9), true },
        };

        // U59's text property after each setter: the text a setter from characters set counts
        // as a change once, then is skipped again.
        [Theory]
        [MemberData(nameof(TextAfter))]
        public void TheTextPropertyAfterASetter(string name, Action<TMP_Text> set, bool changes)
        {
            var t = Settled("Hello 124");
            set(t);
            t.ForceMeshUpdate();
            Assert.False(t.havePropertiesChanged, name);
            t.text = "Hello 123";
            Assert.Equal(changes, t.havePropertiesChanged);
            t.ForceMeshUpdate();
            t.text = "Hello 123";
            Assert.False(t.havePropertiesChanged, name);
        }
    }
}
