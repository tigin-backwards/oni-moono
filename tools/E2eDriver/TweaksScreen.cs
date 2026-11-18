using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace E2eDriver
{
    /// Our main menu's Moono Tweaks screen (OniMoono.GameTweaks.MoonoTweaksScreen), read as state
    /// for its specs (moono-tweaks-search-en.yaml and the others): what its search shows, checked
    /// against what each row shows rather than against a list of tweaks, so that adding, renaming
    /// or regrouping tweaks changes no spec. Read as `type:E2eDriver.TweaksScreen::NAME`; each
    /// fails with a driver error while the screen is not open (the original has none).
    public static class TweaksScreen
    {
        /// How many tweak rows the screen has, shown or not.
        public static int Rows => Read().Rows.Count;

        /// How many rows the search shows now.
        public static int Shown => Read().Rows.Count(r => r.Row.activeSelf);

        /// "ok", or what the screen shows that disagrees with its search field's text: rows
        /// shown that the query does not match or hidden that it does, a heading shown over no
        /// shown row or hidden over one, the "no matches" line shown while a row is (or hidden
        /// while none is). A row matches as the game's searches match a list
        /// (StringSearchableListUtil): the whole query, or every word of it, lowercased, is in
        /// the row's name (its launch option), its title, its one-liner, its tooltip (without
        /// colour tags) or its choices; an empty query matches every row.
        public static string Problems
        {
            get
            {
                var screen = Read();
                var query = screen.Query.Trim().ToLowerInvariant();
                var words = query.Split(' ').Where(w => w.Length > 0).ToArray();
                bool Matches(string text) => query.Length == 0 || text.Contains(query) || words.All(text.Contains);
                var extra = new List<string>();
                var missing = new List<string>();
                foreach (var row in screen.Rows)
                {
                    bool match = Matches(row.Text);
                    if (row.Row.activeSelf && !match) extra.Add(row.Row.name);
                    else if (!row.Row.activeSelf && match) missing.Add(row.Row.name);
                }
                var problems = new List<string>();
                if (extra.Count > 0) problems.Add("shown without a match: " + List(extra));
                if (missing.Count > 0) problems.Add("hidden but matching: " + List(missing));
                foreach (var (heading, rows) in screen.Sections)
                {
                    bool any = rows.Any(r => r.activeSelf);
                    if (heading.activeSelf != any) problems.Add($"'{heading.name}' {(any ? "hidden over a shown row" : "shown over no shown row")}");
                }
                bool none = screen.Rows.All(r => !r.Row.activeSelf);
                if (screen.NoMatches == null) problems.Add("no NoMatches line");
                else if (screen.NoMatches.activeSelf != none) problems.Add(none ? "NoMatches hidden with no row shown" : "NoMatches shown with a row shown");
                return problems.Count == 0 ? "ok" : $"search '{screen.Query}': " + string.Join("; ", problems);
            }
        }

        /// How many rows show their title and one-liner in another text than English: in another
        /// language, the rows the player's translation (translations/CODE.po) translates. A
        /// translation may lag behind new rows, which the screen shows in English
        /// (TweakTranslationTests reports them), so specs compare this with Rows loosely.
        public static int Translated
        {
            get
            {
                var screen = Read();
                var menu = Values.FindType("OniMoono.GameTweaks.MoonoTweaksMenu") ?? throw new DriverError("no MoonoTweaksMenu in this player");
                var ids = (string[])Static(menu, "Ids");
                var english = (string[])Static(menu, "english");
                var keyIndex = (Dictionary<string, int>)Static(menu, "keyIndex");
                string English(string keys, int i)
                {
                    var key = ((string[])Static(menu, keys))[i];
                    return key.Length > 0 && keyIndex.TryGetValue(key, out var at) ? english[at] : null;
                }
                int translated = 0;
                foreach (var row in screen.Rows)
                {
                    int i = Array.IndexOf(ids, row.Row.name);
                    if (row.Title != English("titleKeys", i) && row.Effect != English("effectKeys", i)) translated++;
                }
                return translated;
            }
        }

        static string Tip(ToolTip tip)
        {
            if (tip == null) return "";
            var parts = new List<string>();
            for (int i = 0; i < tip.multiStringCount; i++) parts.Add(tip.GetMultiString(i));
            return string.Join("\n", parts);
        }

        static string List(List<string> names) => string.Join(", ", names.Take(12)) + (names.Count > 12 ? $" and {names.Count - 12} more" : "");

        static object Static(Type type, string name) =>
            (type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new DriverError($"no {type.Name}.{name}")).GetValue(null);

        sealed class RowText
        {
            public GameObject Row;
            public string Title, Effect, Text;
        }

        sealed class State
        {
            public string Query;
            public List<RowText> Rows = new List<RowText>();
            public List<(GameObject Heading, List<GameObject> Rows)> Sections = new List<(GameObject, List<GameObject>)>();
            public GameObject NoMatches;
        }

        static readonly Regex Tags = new Regex("<[^<>]*>");

        /// The open screen's list (the Mods screen's ListView content: section headings, rows and
        /// the "no matches" line, in order), each row's text as the player sees it, and the search
        /// field's text.
        static State Read()
        {
            var type = Values.FindType("OniMoono.GameTweaks.MoonoTweaksScreen") ?? throw new DriverError("no Moono Tweaks screen in this player");
            var screen = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.InstanceID).OfType<Component>().FirstOrDefault(c => c.gameObject.activeInHierarchy)
                ?? throw new DriverError("the Moono Tweaks screen is not open");
            var content = screen.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(t => t.name == "Content" && t.parent != null && t.parent.name == "Viewport")
                ?? throw new DriverError("no list content in the Moono Tweaks screen");
            var input = screen.GetComponentsInChildren<KInputTextField>(true).FirstOrDefault(f => f.transform.parent != null && f.transform.parent.name == "Search")
                ?? throw new DriverError("no search field in the Moono Tweaks screen");
            var menu = Values.FindType("OniMoono.GameTweaks.MoonoTweaksMenu") ?? throw new DriverError("no MoonoTweaksMenu in this player");
            var ids = (string[])Static(menu, "Ids");
            var choices = (string[][])Static(menu, "Choices");
            var state = new State { Query = input.text ?? "" };
            for (int c = 0; c < content.childCount; c++)
            {
                var child = content.GetChild(c).gameObject;
                if (child.name == "NoMatches") state.NoMatches = child;
                else if (child.name.StartsWith("Heading ", StringComparison.Ordinal)) state.Sections.Add((child, new List<GameObject>()));
                else
                {
                    int i = Array.IndexOf(ids, child.name);
                    if (i < 0) throw new DriverError("a row the menu does not list: " + child.name);
                    var refs = child.GetComponent<HierarchyReferences>();
                    var title = child.transform.Find("TitleAndEffect/Title")?.GetComponent<LocText>();
                    var effect = child.transform.Find("TitleAndEffect/Effect")?.GetComponent<LocText>();
                    var tip = refs != null && refs.HasReference("Description") ? refs.GetReference<ToolTip>("Description") : null;
                    var parts = new List<string> { child.name, title?.text ?? "", effect?.text ?? "", Tip(tip) };
                    if (choices[i] != null) parts.AddRange(choices[i]);
                    var row = new RowText { Row = child, Title = title?.text, Effect = effect?.text, Text = Tags.Replace(string.Join("\n", parts), "").ToLowerInvariant() };
                    state.Rows.Add(row);
                    if (state.Sections.Count == 0) throw new DriverError("a row above the first heading: " + child.name);
                    state.Sections[state.Sections.Count - 1].Rows.Add(child);
                }
            }
            if (state.Rows.Count == 0) throw new DriverError("no rows in the Moono Tweaks screen");
            return state;
        }
    }
}
