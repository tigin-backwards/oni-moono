#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace OniMoono.Host
{
    /// ARCHITECTURE D60. ICU (CLDR 34 on) separates French digit groups with U+202F, a narrow
    /// no-break space; Mono's culture tables (mono/culture/culture-info-tables.h, Unity's fork)
    /// have U+00A0, a no-break space, so `1234567.891.ToString("N2", fr-FR)` is "1 234 567,89"
    /// with another space in each player, and the game's fonts have both glyphs. Mono takes the
    /// currency group separator from its table and the percent one from the number one. For the
    /// cultures below, where ICU has U+202F and Mono's table U+00A0 for both, every
    /// NumberFormatInfo the runtime builds gets U+00A0 for its number, currency and percent group
    /// separators. The other cultures with U+202F are left as they are: Mono has no data for 34
    /// of them (fr-BF, vec-IT…), and fr-BE's separator is '.' in Mono, another difference.
    internal static class MonoGroupSeparators
    {
        /// The cultures whose group separators are U+202F in .NET 10's ICU data and U+00A0 in
        /// Mono's tables, every one of them (compared over CultureInfo.GetCultures).
        internal static readonly HashSet<string> Cultures = new(StringComparer.OrdinalIgnoreCase)
        {
            "fr", "fr-CD", "fr-CH", "fr-CI", "fr-CM", "fr-FR", "fr-HT", "fr-MC", "fr-ML", "fr-RE", "fr-SN",
        };

        const string Narrow = "\u202F", NoBreak = "\u00A0";

        static Harmony? harmony;
        static AccessTools.FieldRef<object, string>? cultureName;
        static readonly AccessTools.FieldRef<NumberFormatInfo, string> Number = AccessTools.FieldRefAccess<NumberFormatInfo, string>("_numberGroupSeparator");
        static readonly AccessTools.FieldRef<NumberFormatInfo, string> Currency = AccessTools.FieldRefAccess<NumberFormatInfo, string>("_currencyGroupSeparator");
        static readonly AccessTools.FieldRef<NumberFormatInfo, string> Percent = AccessTools.FieldRefAccess<NumberFormatInfo, string>("_percentGroupSeparator");
        static readonly AccessTools.FieldRef<CultureInfo, NumberFormatInfo?> NumInfo = AccessTools.FieldRefAccess<CultureInfo, NumberFormatInfo?>("_numInfo");

        public static void Install()
        {
            if (harmony != null) return;
            harmony = new Harmony("OniMoono.Host.MonoGroupSeparators");
            // CultureData.GetNFIValues fills a new NumberFormatInfo (its constructor, for every
            // CultureInfo.NumberFormat and NumberFormatInfo.GetInstance) from ICU.
            var cultureData = typeof(CultureInfo).Assembly.GetType("System.Globalization.CultureData", throwOnError: true)!;
            cultureName = AccessTools.FieldRefAccess<string>(cultureData, "_sName");
            harmony.Patch(AccessTools.Method(cultureData, "GetNFIValues", [typeof(NumberFormatInfo)]),
                postfix: new HarmonyMethod(typeof(MonoGroupSeparators), nameof(Filled)));
            // Formats the host made before the patch (its log, under the user's culture).
            foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.InstalledUICulture,
                         CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture })
                if (culture != null && Cultures.Contains(culture.Name) && NumInfo(culture) is NumberFormatInfo made) Apply(made);
        }

        static void Filled(object __instance, NumberFormatInfo nfi)
        {
            if (cultureName!(__instance) is string name && Cultures.Contains(name)) Apply(nfi);
        }

        /// The fields, not the setters: a culture's own NumberFormatInfo is read-only.
        internal static void Apply(NumberFormatInfo nfi)
        {
            if (Number(nfi) == Narrow) Number(nfi) = NoBreak;
            if (Currency(nfi) == Narrow) Currency(nfi) = NoBreak;
            if (Percent(nfi) == Narrow) Percent(nfi) = NoBreak;
        }
    }
}
