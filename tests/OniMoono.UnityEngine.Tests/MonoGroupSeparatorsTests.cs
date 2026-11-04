using System;
using System.Globalization;
using System.Linq;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// French digit groups as Mono separates them (ARCHITECTURE D60; src/OniMoono.Host/
    /// MonoGroupSeparators.cs, compiled in). The expected texts are the original player's
    /// (UnityProbe's ns21 scenario, u60-w1-netstandard-mod.yaml) and Mono's culture tables.
    public class MonoGroupSeparatorsTests
    {
        static MonoGroupSeparatorsTests() => MonoGroupSeparators.Install();

        [Fact]
        public void FrenchGroupsTakeANoBreakSpace()
        {
            // The probe's value: "0031 00A0 0032 0033 0034 00A0 0035 0036 0037 002C 0038 0039" in the original.
            Assert.Equal("1\u00A0234\u00A0567,89", string.Format(new CultureInfo("fr-FR"), "{0:N2}", 1234567.891));
            var fr = CultureInfo.GetCultureInfo("fr-FR").NumberFormat;
            Assert.Equal("\u00A0", fr.NumberGroupSeparator);
            Assert.Equal("\u00A0", fr.CurrencyGroupSeparator);
            Assert.Equal("\u00A0", fr.PercentGroupSeparator);
            Assert.True(fr.IsReadOnly);
            Assert.Equal("\u00A0", new CultureInfo("fr-CH").NumberFormat.CurrencyGroupSeparator);
            Assert.Equal("1\u00A0234", 1234.ToString("N0", NumberFormatInfo.GetInstance(new CultureInfo("fr"))));
        }

        [Fact]
        public void EveryListedCultureAndNoOther()
        {
            foreach (var name in MonoGroupSeparators.Cultures)
                Assert.Equal("\u00A0", new CultureInfo(name).NumberFormat.NumberGroupSeparator);
            // ICU's other narrow spaces stay: cultures Mono has no data for, or other data.
            Assert.Equal("\u202F", new CultureInfo("fr-BF").NumberFormat.NumberGroupSeparator);
            Assert.Equal("\u202F", new CultureInfo("fr-BE").NumberFormat.NumberGroupSeparator);
            Assert.Equal(".", new CultureInfo("de-DE").NumberFormat.NumberGroupSeparator);
            Assert.Equal("\u00A0", new CultureInfo("ru-RU").NumberFormat.NumberGroupSeparator);
            Assert.Equal(",", CultureInfo.InvariantCulture.NumberFormat.NumberGroupSeparator);
        }

        [Fact]
        public void ACloneAndAnOverrideKeepTheirOwn()
        {
            var nfi = (NumberFormatInfo)new CultureInfo("fr-FR").NumberFormat.Clone();
            Assert.Equal("\u00A0", nfi.NumberGroupSeparator);
            nfi.NumberGroupSeparator = "\u202F";
            Assert.Equal("1\u202F234", 1234.ToString("N0", nfi));
        }
    }
}
