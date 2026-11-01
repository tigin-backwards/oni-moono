// U60's new BCL and TMP calls (U60 QA spec L2 and U1), at the main menu, lines "U60 ...":
//
// - CU: the cultures game code runs under: the main thread's CurrentCulture and
//   CurrentUICulture, a new thread's and a pool thread's, DefaultThreadCurrentCulture and LANG.
// - CI: CompareInfo.Compare as FuzzySharp calls it (Tokens.SortStable: Compare(s, start, len, s,
//   start, len, CompareOptions.None)) under the invariant culture, en-US, de-DE, ru-RU, zh-CN,
//   ko-KR and the current culture: the sign for pairs of words that tell orderings apart, and
//   the building names' tokens sorted by FuzzySharp's insertion sort; then each building name's
//   TokenizedString.SortedTokens (the game's own code, under the current culture).
// - IX: String.IndexOf(string, int), culture-sensitive, as Localization.HasMatchingLinkTags
//   calls it, on link-tag texts with ignorable and combining characters.
// - TF: Int32, Int64 and Single TryFormat into a span as KleiStringBuilder.Append calls it, with
//   "G", "N0", "D3", "F1", "0.##" and a null format, a null provider and each culture above.
// - FR: File.Replace(source, destination, backup, true) and File.Move as
//   SteamUGCService.SwapDownloadedFile calls them: over an existing and a missing destination,
//   with no backup, a missing, an existing and a read-only backup, a read-only destination and
//   a missing source. Each case logs the exception's type (or "ok") and the folder afterwards:
//   each file's name, content and whether it is read-only.
// - TS: TMP_Text with unchanged text (U1): a TextMeshProUGUI under a layout group that counts
//   its layout passes; each call is made on a frame of its own, then two frames pass. Each line
//   has the call, havePropertiesChanged right after it, and over those frames the number of
//   OnPreRenderText events, TEXT_CHANGED_EVENT events and layout passes of the parent.
//   Calls: text =, SetText(string), SetText(StringBuilder), SetCharArray(char[]),
//   SetCharArray(char[], start, length), SetCharArray(ReadOnlySpan<char>) (new in U60's
//   package) with the same characters, then with others, and LocText.SetContent with the same
//   characters.
// - AW: a behaviour whose Awake throws (LocText's does without a key): whether OnEnable, Start
//   and Update run, enabled and isActiveAndEnabled after it, on a later deactivation and
//   activation; added to an inactive object then activated, and added to an active one.
//
// Ends with a "U60ProbeDone" object under its canvas (a spec waits for **/U60ProbeDone).
// Run alone with PROBE_SCENARIO=u60, built against U60's Managed (-p:OniManaged=...).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class U60Bcl : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static void L(string s) => Rec.Log("U60 " + s);

        static string Esc(string s)
        {
            if (s == null) return "(null)";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 0x20 || c > 0x7E || c == '|' || c == '\\') sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            Step("CU", Cultures);
            Step("CI", Compare);
            Step("IX", IndexOfs);
            Step("TF", TryFormats);
            Step("FR", Replace);
            var cgo = new GameObject("U60ProbeCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            yield return null;
            IEnumerator ts = null;
            try { ts = SameText(canvas.transform); }
            catch (Exception e) { L("TS setup threw " + e.GetType().Name + ": " + e.Message); }
            while (ts != null)
            {
                bool more;
                try { more = ts.MoveNext(); }
                catch (Exception e) { L("TS threw " + e.GetType().Name + ": " + e.Message); break; }
                if (!more) break;
                yield return ts.Current;
            }
            IEnumerator aw = AwakeThrows();
            while (true)
            {
                bool more;
                try { more = aw.MoveNext(); }
                catch (Exception e) { L("AW threw " + e.GetType().Name + ": " + e.Message); break; }
                if (!more) break;
                yield return aw.Current;
            }
            var done = new GameObject("U60ProbeDone", typeof(RectTransform));
            done.transform.SetParent(canvas.transform, false);
            ((RectTransform)done.transform).sizeDelta = new Vector2(4, 4);
            done.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
            L("=== done");
        }

        static void Step(string name, System.Action run)
        {
            try { run(); }
            catch (Exception e) { L(name + " threw " + e.GetType().Name + ": " + e.Message); }
        }

        // ---- CU ----

        static void Cultures()
        {
            string Name(CultureInfo c) => c == null ? "(null)" : "'" + c.Name + "'";
            L("CU LANG=" + Environment.GetEnvironmentVariable("LANG") + " LC_ALL=" + Environment.GetEnvironmentVariable("LC_ALL"));
            L("CU main CurrentCulture=" + Name(Thread.CurrentThread.CurrentCulture) + " CurrentUICulture=" + Name(Thread.CurrentThread.CurrentUICulture)
              + " CultureInfo.CurrentCulture=" + Name(CultureInfo.CurrentCulture) + " DefaultThreadCurrentCulture=" + Name(CultureInfo.DefaultThreadCurrentCulture)
              + " InstalledUICulture=" + Name(CultureInfo.InstalledUICulture));
            string fromThread = null;
            var t = new Thread(() => fromThread = Name(CultureInfo.CurrentCulture) + "/" + Name(CultureInfo.CurrentUICulture));
            t.Start();
            t.Join();
            L("CU new thread=" + fromThread);
            string fromPool = null;
            using (var ev = new ManualResetEvent(false))
            {
                ThreadPool.QueueUserWorkItem(_ => { fromPool = Name(CultureInfo.CurrentCulture) + "/" + Name(CultureInfo.CurrentUICulture); ev.Set(); });
                ev.WaitOne(5000);
            }
            L("CU pool thread=" + fromPool);
            L("CU formats current: 1.5f=" + 1.5f.ToString() + " 1234567=" + 1234567.ToString("N0") + " 1234.5=" + 1234.5.ToString("N2"));
        }

        // ---- CI ----

        static readonly string[] CultureNames = { "", "en-US", "de-DE", "ru-RU", "zh-CN", "ko-KR" };

        static IEnumerable<KeyValuePair<string, CompareInfo>> Compares()
        {
            foreach (var n in CultureNames)
            {
                CompareInfo ci = null;
                string error = null;
                try { ci = CultureInfo.GetCultureInfo(n).CompareInfo; }
                catch (Exception e) { error = e.GetType().Name; }
                if (ci == null) L("CI culture '" + n + "' " + error);
                else yield return new KeyValuePair<string, CompareInfo>("'" + n + "'", ci);
            }
            yield return new KeyValuePair<string, CompareInfo>("current", CultureInfo.CurrentCulture.CompareInfo);
        }

        static readonly string[] Words =
        {
            "a", "A", "b", "B", "z", "Z", "ä", "Ä", "ae", "af", "é", "e", "f", "ß", "ss", "st", "æ", "ø", "o", "p",
            "co-op", "coop", "cop", "co", "-", "--", "'", "'s", "s", "_x", "x", "x_", "1", "10", "2", "9", "a1", "a10", "a2",
            "Tile", "tile", "Tiles", "Tile's", "Ice-E", "IceE", "Ice", "Ïce", "(Max)", "Max", "[x]", "x",
            "Ж", "ж", "е", "ё", "Е", "Ё", "и", "й", "ь", "ъ", "я", "Я", "Liquid", "Жидкость",
            "中", "日", "文", "가", "나", "ㄱ", "あ", "ア", "­", "a­b", "ab", "a​b", "é", "é",
            "", " ", "  ", "\t", "Gas", "gas", "GAS", "Gás",
        };

        static void Compare()
        {
            var names = BuildingNames();
            L("CI building names " + names.Count);
            foreach (var kv in Compares())
            {
                var ci = kv.Value;
                var sb = new StringBuilder();
                for (int i = 0; i < Words.Length; i++)
                    for (int j = 0; j < Words.Length; j++)
                    {
                        if (i == j) continue;
                        string pair = Words[i] + "\u0001" + Words[j];
                        int r = Math.Sign(ci.Compare(pair, 0, Words[i].Length, pair, Words[i].Length + 1, Words[j].Length, CompareOptions.None));
                        sb.Append(r < 0 ? '<' : r > 0 ? '>' : '=');
                    }
                L("CI " + kv.Key + " pairs " + sb);
                var tokens = new List<string>();
                foreach (var n in names) tokens.AddRange(n.Split(' ').Where(t => t.Length > 0));
                tokens = tokens.Distinct().ToList();
                L("CI " + kv.Key + " sorted " + Esc(string.Join("|", SortStable(tokens, ci).ToArray())));
            }
            // The game's own: FuzzySharp's TokenizedString under the current culture.
            var type = typeof(Db).Assembly.GetType("FuzzySharp.TokenizedString") ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("FuzzySharp.TokenizedString")).FirstOrDefault(t => t != null);
            if (type == null) { L("CI TokenizedString missing"); return; }
            var field = type.GetField("SortedTokens", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var sbt = new StringBuilder();
            foreach (var n in names.Concat(Words))
            {
                object ts = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { n }, null);
                sbt.Append(Esc((string)field.GetValue(ts))).Append('|');
            }
            L("CI TokenizedString.SortedTokens " + sbt);
        }

        static List<string> SortStable(List<string> tokens, CompareInfo ci)
        {
            var a = tokens.ToArray();
            for (int i = 1; i < a.Length; i++)
            {
                var x = a[i];
                int k = i - 1;
                while (k >= 0 && ci.Compare(a[k], 0, a[k].Length, x, 0, x.Length, CompareOptions.None) > 0)
                {
                    a[k + 1] = a[k];
                    k--;
                }
                a[k + 1] = x;
            }
            return a.ToList();
        }

        static List<string> BuildingNames()
        {
            var list = new List<string>();
            foreach (var def in Assets.BuildingDefs)
            {
                if (def == null) continue;
                string n;
                try { n = STRINGS.UI.StripLinkFormatting(def.Name); }
                catch { n = def.PrefabID; }
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
            return list;
        }

        // ---- IX ----

        static void IndexOfs()
        {
            string[] texts =
            {
                "<link=\"A\">a</link>", "x<link=\"A\">a</link><link=\"B\">b</link>", "</link><link>", "<­link>a</link>",
                "<li­nk>a</link>", "<linḱ>a</link>", "<Link>a</LINK>", "<link>a<​/link>", "­link",
                "<link>ä</link>", "<lınk>a</lınk>", "<link>а</link>", "<lin‍k>",
            };
            foreach (var t in texts)
            {
                var sb = new StringBuilder();
                foreach (var tok in new[] { "<link", "</link" })
                    for (int i = 0; i <= t.Length; i++)
                    {
                        int r;
                        try { r = t.IndexOf(tok, i); }
                        catch (Exception e) { sb.Append("!" + e.GetType().Name + " "); continue; }
                        sb.Append(r.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    }
                L("IX \"" + Esc(t) + "\" " + sb + "split=" + t.Split(new[] { "<link" }, StringSplitOptions.None).Length);
            }
        }

        // ---- TF ----

        static void TryFormats()
        {
            var providers = new List<KeyValuePair<string, IFormatProvider>> { new KeyValuePair<string, IFormatProvider>("null", null) };
            foreach (var n in CultureNames)
            {
                try { providers.Add(new KeyValuePair<string, IFormatProvider>("'" + n + "'", CultureInfo.GetCultureInfo(n))); }
                catch (Exception e) { L("TF culture '" + n + "' " + e.GetType().Name); }
            }
            string[] formats = { null, "G", "N0", "D3", "F1", "0.##", "#,##0" };
            int[] ints = { 0, 7, -7, 1234567, int.MinValue, int.MaxValue };
            long[] longs = { 0, -1, 9876543210L, long.MinValue };
            float[] floats = { 0f, -0f, 1.5f, 0.1f, 67.255f, 1234567.8f, -0.004f, float.NaN, float.PositiveInfinity, 1e-7f };
            foreach (var p in providers)
                foreach (var f in formats)
                {
                    var sb = new StringBuilder();
                    Span<char> buf = stackalloc char[64];
                    foreach (var v in ints) sb.Append(v.TryFormat(buf, out int w, f, p.Value) ? buf.Slice(0, w).ToString() : "(false)").Append('|');
                    foreach (var v in longs) sb.Append(v.TryFormat(buf, out int w, f, p.Value) ? buf.Slice(0, w).ToString() : "(false)").Append('|');
                    sb.Append(" floats ");
                    foreach (var v in floats)
                    {
                        string s;
                        try { s = v.TryFormat(buf, out int w, f, p.Value) ? buf.Slice(0, w).ToString() : "(false)"; }
                        catch (Exception e) { s = "!" + e.GetType().Name; }
                        sb.Append(s).Append('|');
                    }
                    // A destination too small: TryFormat must answer false, not throw.
                    Span<char> small = stackalloc char[2];
                    sb.Append(" small=" + 1234567.TryFormat(small, out int ws, f, p.Value) + "/" + ws);
                    L("TF " + p.Key + " " + (f ?? "(null)") + " " + Esc(sb.ToString()));
                }
        }

        // ---- FR ----

        static void Replace()
        {
            string root = Path.Combine(Path.GetTempPath(), "u60-replace-probe-" + System.DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            int n = 0;
            void Case(string name, Action<string> setup, Action<string> act)
            {
                string d = Path.Combine(root, (n++).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(d);
                string result;
                try { setup(d); act(d); result = "ok"; }
                catch (Exception e) { result = e.GetType().Name; }
                L("FR " + name + " = " + result + " | " + Listing(d));
                foreach (var f in Directory.GetFiles(d))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
            }
            void W(string d, string f, string content) => File.WriteAllText(Path.Combine(d, f), content);
            void RO(string d, string f) => File.SetAttributes(Path.Combine(d, f), FileAttributes.ReadOnly);
            string P(string d, string f) => Path.Combine(d, f);

            Case("Replace dest exists, backup new", d => { W(d, "tmp", "new"); W(d, "final", "old"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace dest exists, backup exists", d => { W(d, "tmp", "new"); W(d, "final", "old"); W(d, "final.bak", "older"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace dest exists, backup null", d => { W(d, "tmp", "new"); W(d, "final", "old"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), null, true));
            Case("Replace dest exists, backup null, ignoreMetadataErrors false", d => { W(d, "tmp", "new"); W(d, "final", "old"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), null, false));
            Case("Replace dest missing", d => W(d, "tmp", "new"), d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace dest missing, backup null", d => W(d, "tmp", "new"), d => File.Replace(P(d, "tmp"), P(d, "final"), null, true));
            Case("Replace source missing", d => W(d, "final", "old"), d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace backup read-only", d => { W(d, "tmp", "new"); W(d, "final", "old"); W(d, "final.bak", "older"); RO(d, "final.bak"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace dest read-only", d => { W(d, "tmp", "new"); W(d, "final", "old"); RO(d, "final"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace source read-only", d => { W(d, "tmp", "new"); W(d, "final", "old"); RO(d, "tmp"); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace source is dest", d => W(d, "final", "old"), d => File.Replace(P(d, "final"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace backup is a folder", d => { W(d, "tmp", "new"); W(d, "final", "old"); Directory.CreateDirectory(P(d, "final.bak")); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Replace dest is a folder", d => { W(d, "tmp", "new"); Directory.CreateDirectory(P(d, "final")); }, d => File.Replace(P(d, "tmp"), P(d, "final"), P(d, "final.bak"), true));
            Case("Move to new", d => W(d, "tmp", "new"), d => File.Move(P(d, "tmp"), P(d, "final")));
            Case("Move over existing", d => { W(d, "tmp", "new"); W(d, "final", "old"); }, d => File.Move(P(d, "tmp"), P(d, "final")));
            Case("Move source missing", d => { }, d => File.Move(P(d, "tmp"), P(d, "final")));
            Case("Move source read-only", d => { W(d, "tmp", "new"); RO(d, "tmp"); }, d => File.Move(P(d, "tmp"), P(d, "final")));
            try { Directory.Delete(root, true); } catch (Exception e) { L("FR cleanup " + e.GetType().Name); }
        }

        static string Listing(string d)
        {
            var parts = new List<string>();
            foreach (var e in Directory.GetFileSystemEntries(d).OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(e);
                if (Directory.Exists(e)) { parts.Add(name + "/"); continue; }
                bool ro = (File.GetAttributes(e) & FileAttributes.ReadOnly) != 0;
                parts.Add(name + "=" + File.ReadAllText(e) + (ro ? " (ro)" : ""));
            }
            return string.Join(" ", parts.ToArray());
        }

        // ---- AW ----

        public class AwThrow : MonoBehaviour
        {
            public static string Tag;
            int updates;
            void Awake() { L("AW " + Tag + " Awake"); throw new InvalidOperationException("probe"); }
            void OnEnable() => L("AW " + Tag + " OnEnable");
            void Start() => L("AW " + Tag + " Start");
            void Update() { if (updates++ < 1) L("AW " + Tag + " Update"); }
            void OnDisable() => L("AW " + Tag + " OnDisable");
            void OnDestroy() => L("AW " + Tag + " OnDestroy");
        }

        static string State(MonoBehaviour b) => "enabled=" + b.enabled + " isActiveAndEnabled=" + b.isActiveAndEnabled;

        IEnumerator AwakeThrows()
        {
            AwThrow.Tag = "inactive";
            var go = new GameObject("U60AwInactive");
            go.SetActive(false);
            var b = go.AddComponent<AwThrow>();
            L("AW inactive added " + State(b));
            go.SetActive(true);
            L("AW inactive activated " + State(b));
            yield return null;
            yield return null;
            L("AW inactive two frames " + State(b));
            go.SetActive(false);
            L("AW inactive deactivated " + State(b));
            go.SetActive(true);
            L("AW inactive reactivated " + State(b));
            b.enabled = false;
            b.enabled = true;
            L("AW inactive enabled again " + State(b));
            yield return null;
            yield return null;
            Destroy(go);
            yield return null;
            AwThrow.Tag = "active";
            var go2 = new GameObject("U60AwActive");
            var b2 = go2.AddComponent<AwThrow>();
            L("AW active added " + State(b2));
            yield return null;
            yield return null;
            L("AW active two frames " + State(b2));
            Destroy(go2);
            yield return null;
            L("AW done");
        }

        // ---- TS ----

        // obj.METHOD(ReadOnlySpan<char>) with TEXT's characters. The game's assemblies name
        // mscorlib's ReadOnlySpan, which this netstandard build cannot reference, so the call is
        // emitted: string.AsSpan, then the method, each looked up at run time.
        static readonly Dictionary<string, System.Action<object, string>> spanCalls = new Dictionary<string, System.Action<object, string>>();

        // SetCharArray(char[]) and (char[], int, int), called through reflection: with U60's
        // package their overload resolution would weigh the ReadOnlySpan overload (see above).
        static void CharArray(TMP_Text t, char[] chars) =>
            typeof(TMP_Text).GetMethod("SetCharArray", new[] { typeof(char[]) }).Invoke(t, new object[] { chars });

        static void CharArray(TMP_Text t, char[] chars, int start, int length) =>
            typeof(TMP_Text).GetMethod("SetCharArray", new[] { typeof(char[]), typeof(int), typeof(int) }).Invoke(t, new object[] { chars, start, length });

        static void SpanCall(object obj, string method, string text)
        {
            string key = obj.GetType().FullName + "." + method;
            if (!spanCalls.TryGetValue(key, out var call))
            {
                MethodInfo target = null;
                for (var t = obj.GetType(); t != null && target == null; t = t.BaseType)
                    target = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(m =>
                        m.Name == method && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "ReadOnlySpan`1");
                if (target == null) throw new MissingMethodException(obj.GetType().Name, method + "(ReadOnlySpan<char>)");
                var spanType = target.GetParameters()[0].ParameterType;
                var asSpan = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("System.MemoryExtensions")).Where(t => t != null)
                    .Select(t => t.GetMethod("AsSpan", new[] { typeof(string) })).First(m => m != null && m.ReturnType == spanType);
                var dm = new System.Reflection.Emit.DynamicMethod("SpanCall_" + method, typeof(void), new[] { typeof(object), typeof(string) }, typeof(U60Bcl).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, target.DeclaringType);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Call, asSpan);
                il.Emit(target.IsVirtual ? System.Reflection.Emit.OpCodes.Callvirt : System.Reflection.Emit.OpCodes.Call, target);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                call = (System.Action<object, string>)dm.CreateDelegate(typeof(System.Action<object, string>));
                spanCalls[key] = call;
            }
            call(obj, text);
        }

        public class LayoutCounter : UIBehaviour, ILayoutGroup
        {
            public int Horizontal, Vertical;
            public void SetLayoutHorizontal() => Horizontal++;
            public void SetLayoutVertical() => Vertical++;
        }

        IEnumerator SameText(Transform canvas)
        {
            var parentGo = new GameObject("U60Layout", typeof(RectTransform));
            parentGo.transform.SetParent(canvas, false);
            ((RectTransform)parentGo.transform).sizeDelta = new Vector2(400, 100);
            var counter = parentGo.AddComponent<LayoutCounter>();
            var go = new GameObject("U60Text", typeof(RectTransform));
            go.transform.SetParent(parentGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(400, 50);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "NotoSans-Regular");
            if (font != null) tmp.font = font;
            tmp.fontSize = 14;
            int preRender = 0, changed = 0;
            tmp.OnPreRenderText += _ => preRender++;
            Action<UnityEngine.Object> onChanged = o => { if (o == tmp) changed++; };
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(onChanged);
            tmp.text = "Hello 123";
            yield return null;
            yield return null;
            yield return null;
            var steps = new List<KeyValuePair<string, System.Action>>
            {
                new KeyValuePair<string, System.Action>("text= same", () => tmp.text = "Hello 123"),
                new KeyValuePair<string, System.Action>("SetText(string) same", () => tmp.SetText("Hello 123")),
                new KeyValuePair<string, System.Action>("SetText(StringBuilder) same", () => tmp.SetText(new StringBuilder("Hello 123"))),
                new KeyValuePair<string, System.Action>("SetCharArray(char[]) same", () => CharArray(tmp, "Hello 123".ToCharArray())),
                new KeyValuePair<string, System.Action>("SetCharArray(char[],0,9) same", () => CharArray(tmp, "xHello 123x".ToCharArray(), 1, 9)),
                new KeyValuePair<string, System.Action>("SetCharArray(span) same", () => SpanCall(tmp, "SetCharArray", "Hello 123")),
                new KeyValuePair<string, System.Action>("SetCharArray(span) same again", () => SpanCall(tmp, "SetCharArray", "Hello 123")),
                new KeyValuePair<string, System.Action>("SetCharArray(span) other", () => SpanCall(tmp, "SetCharArray", "Hello 124")),
                new KeyValuePair<string, System.Action>("SetCharArray(span) other same", () => SpanCall(tmp, "SetCharArray", "Hello 124")),
                new KeyValuePair<string, System.Action>("SetCharArray(span) empty", () => SpanCall(tmp, "SetCharArray", "")),
                new KeyValuePair<string, System.Action>("SetCharArray(span) empty again", () => SpanCall(tmp, "SetCharArray", "")),
                new KeyValuePair<string, System.Action>("text= empty", () => tmp.text = ""),
                new KeyValuePair<string, System.Action>("SetCharArray(char[]) back", () => CharArray(tmp, "Hello 123".ToCharArray())),
                new KeyValuePair<string, System.Action>("SetCharArray(char[]) same after", () => CharArray(tmp, "Hello 123".ToCharArray())),
                new KeyValuePair<string, System.Action>("text= same after", () => tmp.text = "Hello 123"),
                new KeyValuePair<string, System.Action>("SetText(\"{0}\",123) same", () => tmp.SetText("Hello {0}", 123)),
                new KeyValuePair<string, System.Action>("SetText(\"{0}\",123) again", () => tmp.SetText("Hello {0}", 123)),
            };
            foreach (var step in steps)
            {
                int p0 = preRender, c0 = changed, h0 = counter.Horizontal, v0 = counter.Vertical;
                step.Value();
                bool hpc = tmp.havePropertiesChanged;
                yield return null;
                yield return null;
                L("TS " + step.Key + " havePropertiesChanged=" + hpc + " preRender=" + (preRender - p0) + " textChanged=" + (changed - c0)
                  + " layoutH=" + (counter.Horizontal - h0) + " layoutV=" + (counter.Vertical - v0) + " text=\"" + Esc(tmp.text) + "\"");
            }
            // LocText.SetContent, the game's caller.
            var lgo = new GameObject("U60LocText", typeof(RectTransform));
            lgo.SetActive(false);
            lgo.transform.SetParent(parentGo.transform, false);
            LocText loc = null;
            try
            {
                loc = lgo.AddComponent<LocText>();
                if (font != null) loc.font = font;
                lgo.SetActive(true);
            }
            catch (Exception e) { L("TS LocText setup threw " + e.GetType().Name + ": " + e.Message); }
            if (loc != null)
            {
                int lp = 0, lc = 0;
                loc.OnPreRenderText += _ => lp++;
                Action<UnityEngine.Object> onLocChanged = o => { if (o == loc) lc++; };
                TMPro_EventManager.TEXT_CHANGED_EVENT.Add(onLocChanged);
                SpanCall(loc, "SetContent", "Hello 123");
                yield return null;
                yield return null;
                foreach (var name in new[] { "SetContent same", "SetContent same again" })
                {
                    int p0 = lp, c0 = lc, h0 = counter.Horizontal;
                    SpanCall(loc, "SetContent", "Hello 123");
                    bool hpc = loc.havePropertiesChanged;
                    yield return null;
                    yield return null;
                    L("TS LocText." + name + " havePropertiesChanged=" + hpc + " preRender=" + (lp - p0) + " textChanged=" + (lc - c0) + " layoutH=" + (counter.Horizontal - h0) + " text=\"" + Esc(loc.text) + "\"");
                }
                TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(onLocChanged);
            }
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(onChanged);
        }
    }
}
