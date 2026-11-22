using System;

namespace OniMoono.Assets.Shaders
{
    /// The rule a program variant is selected by, given the enabled keywords: an exact match of
    /// the enabled keywords the variants know; otherwise the variant with most enabled keywords,
    /// then fewest others, then the earliest.
    public static class VariantSelection
    {
        /// The position in `variants` of the variant the rule selects (-1 when there is none).
        public static int SelectIndex<T>(ReadOnlySpan<T> variants, Func<T, string[]> keywordsOf, Func<string, bool> isEnabled)
        {
            int best = -1, bestMatches = -1, bestExtras = int.MaxValue;
            for (int i = 0; i < variants.Length; i++)
            {
                int matches = 0, extras = 0;
                foreach (var k in keywordsOf(variants[i]))
                    if (isEnabled(k)) matches++;
                    else extras++;
                if (matches > bestMatches || (matches == bestMatches && extras < bestExtras))
                {
                    best = i;
                    bestMatches = matches;
                    bestExtras = extras;
                }
            }
            return best;
        }
    }
}
