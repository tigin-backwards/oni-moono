// What a method gives when a reference in it does not resolve, inside a try of its own (lines
// "PROBE ... AC ..."), as ONI Boost Kit's GCSentinel reads JobsUtility.JobWorkerCount and
// Artificial Natural Tiles' TryLoadSprite builds a System.Drawing.Bitmap. The cases are
// ProbeAtCallCases' (AtCall/Cases), built against ProbeAbsent (AtCall/Absent), which the mod
// folder does not ship, and ProbeGone v1 (put Gone/v2's build in the mod folder). Then the
// values of JobsUtility's static properties and what setting JobWorkerCount does, and System.
// Number's Format* methods (ZEROs patches FormatSingle(float, string, NumberFormatInfo)) with
// whether a Harmony postfix on that one runs for float.ToString. An `AtCallProbeDone` canvas
// appears at the end.
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SemanticsProbe
{
    public class AtCall : MonoBehaviour
    {
        static void L(string s) => Rec.Log("AC " + s);

        static string Esc(string s) => s == null ? "null" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        static string Describe(Exception e)
        {
            if (e is TargetInvocationException t && t.InnerException != null) e = t.InnerException;
            return e.GetType().FullName + ": " + Esc(e.Message);
        }

        void Start()
        {
            L("start");
            try { ProbeAtCallCases.Cases.Run(L); }
            catch (Exception e) { L("cases threw " + Describe(e)); }
            Jobs();
            Number();
            L("done");
        }

        static void Jobs()
        {
            L("processorCount " + SystemInfo.processorCount + " Environment.ProcessorCount " + Environment.ProcessorCount);
            var jobs = typeof(Application).Assembly.GetType("Unity.Jobs.LowLevel.Unsafe.JobsUtility");
            if (jobs == null) { L("no JobsUtility"); return; }
            foreach (var p in jobs.GetProperties(BindingFlags.Public | BindingFlags.Static).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string value;
                try { value = Esc("" + p.GetValue(null)); }
                catch (Exception e) { value = "threw " + Describe(e); }
                L("JobsUtility." + p.Name + " : " + p.PropertyType.Name + (p.CanWrite && p.GetSetMethod() != null ? " get/set" : " get") + " = " + value);
            }
            foreach (var f in jobs.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.Name, StringComparer.Ordinal))
                L("JobsUtility field " + f.Name + " : " + f.FieldType.Name + " = " + Esc("" + f.GetValue(null)));
            foreach (var m in jobs.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => !m.IsSpecialName).OrderBy(m => m.Name, StringComparer.Ordinal))
                L("JobsUtility method " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            var count = jobs.GetProperty("JobWorkerCount");
            if (count == null || count.GetSetMethod() == null) return;
            var original = count.GetValue(null);
            foreach (var wanted in new[] { 3, 1, 0, -1, -5, 1000, 100000 })
            {
                string result;
                try { count.SetValue(null, wanted); result = "now " + count.GetValue(null); }
                catch (Exception e) { result = "threw " + Describe(e) + ", now " + count.GetValue(null); }
                L("JobWorkerCount = " + wanted + ": " + result);
            }
            count.SetValue(null, original);
            L("JobWorkerCount restored " + count.GetValue(null));
        }

        static bool postfixRan;

        static void Postfix() => postfixRan = true;

        static void Number()
        {
            var number = typeof(object).Assembly.GetType("System.Number");
            if (number == null) { L("no System.Number"); return; }
            foreach (var m in number.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(m => m.Name.StartsWith("Format", StringComparison.Ordinal)).OrderBy(m => m.ToString(), StringComparer.Ordinal))
                L("Number " + m);
            var target = number.GetMethod("FormatSingle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(float), typeof(string), typeof(System.Globalization.NumberFormatInfo) }, null);
            L("FormatSingle(float, string, NumberFormatInfo) " + (target == null ? "absent" : "found: " + target));
            if (target == null) return;
            try
            {
                var harmony = new HarmonyLib.Harmony("SemanticsProbe.AtCall");
                harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(AtCall).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
                foreach (var format in new[] { null, "F2", "G", "N1" })
                {
                    postfixRan = false;
                    var text = format == null ? 1.25f.ToString() : 1.25f.ToString(format);
                    L("1.25f.ToString(" + (format ?? "") + ") = " + text + " postfix " + postfixRan);
                }
                postfixRan = false;
                var interpolated = $"{2.5f}";
                L("interpolated " + interpolated + " postfix " + postfixRan);
                harmony.UnpatchAll("SemanticsProbe.AtCall");
            }
            catch (Exception e) { L("patch threw " + Describe(e)); }
        }

        int frame;

        void Update()
        {
            if (++frame == 3)
            {
                L("=== done");
                var done = new GameObject("AtCallProbeDone", typeof(RectTransform));
                DontDestroyOnLoad(done);
                done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }
        }
    }
}
