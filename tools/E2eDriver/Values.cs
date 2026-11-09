using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// Values as JSON, written the same way in both runtimes: Mono and .NET format a float's or a
    /// double's shortest round-trip form differently, so numbers go through fixed-precision
    /// formats first (each correctly rounded in both).
    static class Values
    {
        public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// A float as the shortest of G6–G9 that reads back as the same float.
        public static JToken Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return NonFinite(f);
            for (int p = 6; p < 9; p++)
            {
                var s = f.ToString("G" + p, CultureInfo.InvariantCulture);
                if (float.Parse(s, CultureInfo.InvariantCulture) == f) return double.Parse(s, CultureInfo.InvariantCulture);
            }
            return double.Parse(f.ToString("G9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// A double to 15 significant digits.
        public static JToken Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return NonFinite(d);
            return double.Parse(d.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// NaN and the infinities as text, spelled the same in both runtimes (Mono writes
        /// "Infinity", .NET Core "∞").
        static JToken NonFinite(double d) => double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity";

        /// Screen and layout numbers: rounded to 0.01.
        public static JToken Px(float f) => Num(Math.Round((double)f, 2));

        public static JObject Rect(float x, float y, float w, float h) => new JObject { ["x"] = Px(x), ["y"] = Px(y), ["w"] = Px(w), ["h"] = Px(h) };

        public static JObject Vec(Vector3 v) => new JObject { ["x"] = Num(v.x), ["y"] = Num(v.y), ["z"] = Num(v.z) };

        /// Any value as JSON: scalars as themselves, Unity objects as a reference ({$type, name,
        /// id}), collections as arrays or objects (at most `limit` entries), other objects as
        /// their fields and properties while depth lasts, then as {$type, $str}.
        public static JToken ToJson(object v, int depth, int limit)
        {
            switch (v)
            {
                case null: return JValue.CreateNull();
                case string s: return s;
                case bool b: return b;
                case char c: return c.ToString();
                case float f: return Num(f);
                case double d: return Num(d);
                case decimal m: return Num((double)m);
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                    return System.Convert.ToInt64(v, CultureInfo.InvariantCulture);
                case ulong u: return u;
                case Enum e: return e.ToString();
                case Type t: return TypeName(t);
                case Vector2 v2: return new JObject { ["x"] = Num(v2.x), ["y"] = Num(v2.y) };
                case Vector3 v3: return Vec(v3);
                case Vector4 v4: return new JObject { ["x"] = Num(v4.x), ["y"] = Num(v4.y), ["z"] = Num(v4.z), ["w"] = Num(v4.w) };
                case Color col: return new JObject { ["r"] = Num(col.r), ["g"] = Num(col.g), ["b"] = Num(col.b), ["a"] = Num(col.a) };
                case Color32 c32: return new JObject { ["r"] = c32.r, ["g"] = c32.g, ["b"] = c32.b, ["a"] = c32.a };
                case Rect r: return new JObject { ["x"] = Num(r.x), ["y"] = Num(r.y), ["w"] = Num(r.width), ["h"] = Num(r.height) };
                case Quaternion q: return new JObject { ["x"] = Num(q.x), ["y"] = Num(q.y), ["z"] = Num(q.z), ["w"] = Num(q.w) };
                case UnityEngine.Object o: return ObjectRef(o);
                case Tag tag: return tag.Name;
                case IDictionary dict:
                {
                    if (depth <= 0) return Summary(v, dict.Count);
                    var obj = new JObject();
                    int n = 0;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (n++ >= limit) { obj["$truncated"] = dict.Count; break; }
                        obj[Key(entry.Key)] = ToJson(entry.Value, depth - 1, limit);
                    }
                    return obj;
                }
                case IEnumerable list:
                {
                    if (depth <= 0) return Summary(v, (v as ICollection)?.Count);
                    var array = new JArray();
                    int n = 0;
                    foreach (var item in list)
                    {
                        if (n++ >= limit) break;
                        array.Add(ToJson(item, depth - 1, limit));
                    }
                    return array;
                }
            }
            if (depth <= 0) return Summary(v, null);
            var fields = new JObject { ["$type"] = TypeName(v.GetType()) };
            foreach (var f in v.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                fields[f.Name] = Read(() => f.GetValue(v), depth - 1, limit);
            foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetMethod != null && p.GetIndexParameters().Length == 0 && fields[p.Name] == null)
                    fields[p.Name] = Read(() => p.GetValue(v), depth - 1, limit);
            return fields;
        }

        static JToken Read(Func<object> get, int depth, int limit)
        {
            try { return ToJson(get(), depth, limit); }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                return "!" + e.GetType().Name;
            }
        }

        static string Key(object key) => key switch
        {
            null => "null",
            Tag t => t.Name,
            UnityEngine.Object o => o == null ? "null" : o.name,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => key.ToString(),
        };

        static JToken Summary(object v, int? count)
        {
            var obj = new JObject { ["$type"] = TypeName(v.GetType()) };
            if (count != null) obj["$count"] = count.Value;
            string text;
            try { text = v.ToString(); } catch (Exception e) { text = "!" + e.GetType().Name; }
            obj["$str"] = text;
            return obj;
        }

        public static JToken ObjectRef(UnityEngine.Object o)
        {
            if (o == null) return JValue.CreateNull();
            var obj = new JObject { ["$type"] = TypeName(o.GetType()), ["name"] = o.name, ["id"] = o.GetInstanceID() };
            if (o is GameObject go) { obj["active"] = go.activeInHierarchy; obj["path"] = Ui.PathOf(go.transform); }
            else if (o is Component c) obj["path"] = Ui.PathOf(c.transform);
            return obj;
        }

        /// A type's name the same in both runtimes: the full name, generic arguments in <> by
        /// their own names (FullName would name each argument's assembly, System.Private.CoreLib
        /// on .NET and mscorlib on Mono).
        public static string TypeName(Type t)
        {
            if (t == null) return null;
            if (t.IsArray) return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (!t.IsGenericType) return t.FullName ?? t.Name;
            var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        static readonly Dictionary<string, Type> types = new Dictionary<string, Type>();

        /// A type by full name (nested types with '+'), assembly-qualified name ("Name, Assembly")
        /// or short name, among the loaded assemblies (Mono's Type.GetType does not find a mod's,
        /// which KMod loads with LoadFrom). Short names prefer the game's assemblies.
        public static Type FindType(string name)
        {
            if (types.TryGetValue(name, out var cached)) return cached;
            var parts = name.Split(',');
            var typeName = parts[0].Trim();
            var assemblyName = parts.Length > 1 ? parts[1].Trim() : null;
            Type found = null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var a in assemblies)
                if (assemblyName == null || a.GetName().Name == assemblyName)
                {
                    try { found = a.GetType(typeName); } catch (Exception) { }
                    if (found != null) break;
                }
            if (found == null && assemblyName == null && typeName.IndexOf('.') < 0 && typeName.IndexOf('+') < 0)
            {
                int best = int.MaxValue;
                foreach (var a in assemblies)
                {
                    int rank = Rank(a.GetName().Name);
                    if (rank >= best) continue;
                    Type[] all;
                    try { all = a.GetTypes(); } catch (ReflectionTypeLoadException e) { all = e.Types; } catch (Exception) { continue; }
                    foreach (var t in all)
                        if (t != null && t.Name == typeName && !t.IsNested) { found = t; best = rank; break; }
                }
            }
            if (found != null) types[name] = found;
            return found;
        }

        static int Rank(string assembly) => assembly switch
        {
            "Assembly-CSharp" => 0,
            "Assembly-CSharp-firstpass" => 1,
            "UnityEngine.CoreModule" => 2,
            "UnityEngine.UI" => 3,
            "Unity.TextMeshPro" => 4,
            _ when assembly.StartsWith("UnityEngine", StringComparison.Ordinal) => 5,
            _ when assembly.StartsWith("System", StringComparison.Ordinal) || assembly == "mscorlib" || assembly == "netstandard" => 7,
            _ => 6,
        };

        static readonly Type[] ListTypes =
        {
            typeof(List<>), typeof(IList<>), typeof(ICollection<>), typeof(IEnumerable<>),
            typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>),
        };

        /// A JSON argument as a parameter of the given type; false when it cannot be.
        public static bool Convert(JToken token, Type type, out object value)
        {
            value = null;
            try
            {
                if (token == null || token.Type == JTokenType.Null)
                    return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
                // {"$": "EXPR"}: the value of a state.get expression, for an argument JSON cannot
                // spell (a Tech, a GameObject). It must be of the parameter's type, or be a
                // component whose GameObject has one of that type.
                if (token is JObject expr && expr.Count == 1 && expr["$"] is JValue text && text.Type == JTokenType.String)
                {
                    var found = State.Eval((string)text);
                    if (found == null) return !type.IsValueType;
                    if (found is Component c && !type.IsInstanceOfType(found) && typeof(Component).IsAssignableFrom(type))
                        found = c.GetComponent(type);
                    if (!type.IsInstanceOfType(found)) return false;
                    value = found;
                    return true;
                }
                var under = Nullable.GetUnderlyingType(type) ?? type;
                if (under == typeof(string)) { if (token.Type != JTokenType.String) return false; value = (string)token; return true; }
                if (under.IsEnum)
                {
                    value = token.Type == JTokenType.String ? Enum.Parse(under, (string)token, true) : Enum.ToObject(under, (long)token);
                    return true;
                }
                if (under == typeof(bool)) { if (token.Type != JTokenType.Boolean) return false; value = (bool)token; return true; }
                if (under.IsPrimitive)
                {
                    if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
                    value = System.Convert.ChangeType(((JValue)token).Value, under, CultureInfo.InvariantCulture);
                    return true;
                }
                if (under == typeof(Type)) { value = FindType((string)token); return value != null; }
                if (under == typeof(Tag)) { value = new Tag((string)token); return true; }
                if (under == typeof(HashedString)) { if (token.Type != JTokenType.String) return false; value = new HashedString((string)token); return true; }
                if ((under == typeof(Vector2) || under == typeof(Vector3)) && token is JArray xyz && xyz.Count >= 2)
                {
                    float x = (float)xyz[0], y = (float)xyz[1], z = xyz.Count > 2 ? (float)xyz[2] : 0f;
                    value = under == typeof(Vector2) ? (object)new Vector2(x, y) : new Vector3(x, y, z);
                    return true;
                }
                if (under == typeof(Vector2I) && token is JArray xy && xy.Count == 2) { value = new Vector2I((int)xy[0], (int)xy[1]); return true; }
                if (under.IsArray && token is JArray items)
                {
                    var element = under.GetElementType();
                    var array = Array.CreateInstance(element, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (!Convert(items[i], element, out var item)) return false;
                        array.SetValue(item, i);
                    }
                    value = array;
                    return true;
                }
                // A list, or a list interface (IList<T>, IReadOnlyList<T>, ...): a List<T>.
                if (under.IsGenericType && token is JArray listItems
                    && Array.IndexOf(ListTypes, under.GetGenericTypeDefinition()) >= 0)
                {
                    var element = under.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
                    foreach (var t in listItems)
                    {
                        if (!Convert(t, element, out var item)) return false;
                        list.Add(item);
                    }
                    value = list;
                    return true;
                }
                if (under == typeof(object)) { value = ((token as JValue)?.Value) ?? token.ToString(); return true; }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
