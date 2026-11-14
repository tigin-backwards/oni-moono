using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using KSerialization;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using DailyReport = ReportManager.DailyReport;
    using ReportEntry = ReportManager.ReportEntry;
    using TypeInfo = KSerialization.TypeInfo;

    /// Game tweak `fast-serialization`'s writer for the report history (exact): ReportManager's
    /// `dailyReports` and `todaysReport`, every day's report since the colony began, written by
    /// code compiled for their types (ReportEntryWriter, in ReportEntriesCore.cs).
    ///
    /// Why the game's path is slow here, and fast-serialization's plans did not help: a plan
    /// types a template's plain fields, but these two fields are a List and an object, which go
    /// to Helper.WriteValue. That walks the list as an ICollection and, for every DailyReport and
    /// ReportEntry in it, calls itself with the element as an object: a length written, the
    /// template looked up by type in a dictionary, the template's SerializeData (through its
    /// Harmony prefix, and the plan found in a table), then two seeks to fill the length in. An
    /// entry's `context` string and its `contextEntries` go the same way again: the ArrayRef, a
    /// struct, is boxed and written through a template of its own, and its array is read with
    /// Array.GetValue per element, each a recursive WriteValue. On a late-game colony that is 4
    /// million entries, 8 million templates' worth of calls and 16 million seeks.
    ///
    /// Here a day's bytes are built in a buffer from the fields read directly and handed to the
    /// writer in blocks. What the game's path does besides writing is kept: the three templates
    /// are asked of Manager at the first day, entry and ArrayRef (which puts them in the save's
    /// directory, in that order), and a string of 1,024 bytes or more logs WriteKleiString's
    /// warning.
    ///
    /// The game's own path writes instead (Helper.WriteValue, as before) when
    /// - the three types' templates are not exactly the fields this writes, or have a callback,
    ///   a property or a custom serializer (checked once, at start-up);
    /// - another mod patches a method this stands in for (Guarded: checked at every save).
    /// ONI_TWEAK_VERIFY=1 writes both ways into memory and compares the bytes.
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.Helper::WriteValue(System.IO.BinaryWriter,KSerialization.TypeInfo,System.Object)", "2ae90126aff89120 linux-744825 win-744825", "dd15fef912b3eb70 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.SerializationTemplate::SerializeData(System.Object,System.IO.BinaryWriter)", "e283fa414b66f4a2 linux-744825 win-744825", "9678fe80f01a053c linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.IOHelper::WriteKleiString(System.IO.BinaryWriter,System.String)", "8ec45d48f28db0ae linux-744825 win-744825", "6278de59abadb6d4 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.IOHelper::WriteSingleFast(System.IO.BinaryWriter,System.Single)", "06b887c62310083b linux-744825 win-744825", "801f48d5743265aa linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<ReportManager/DailyReport> ReportManager::dailyReports")]
    [GameMember(GameUse.Uses, "ReportManager/DailyReport ReportManager::todaysReport")]
    [GameMember(GameUse.Uses, "KSerialization.SerializationTemplate KSerialization.Manager::GetSerializationTemplate(System.Type)")]
    [MirrorsGame("System.Void KSerialization.Helper::WriteValue(System.IO.BinaryWriter,KSerialization.TypeInfo,System.Object)")]
    [MirrorsGame("System.Void KSerialization.SerializationTemplate::SerializeData(System.Object,System.IO.BinaryWriter)")]
    [MirrorsGame("System.Void KSerialization.IOHelper::WriteKleiString(System.IO.BinaryWriter,System.String)")]
    [MirrorsGame("System.Void KSerialization.IOHelper::WriteSingleFast(System.IO.BinaryWriter,System.Single)")]
    public static class ReportEntries
    {
        public const string Name = "fast-serialization";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's savetest.txt): true writes the game's way, for
        /// comparing saves in one run.
        public static bool Bypass;

        /// The game's types for the writer.
        public struct Shapes : IReportShapes<DailyReport, ReportEntry>
        {
            public int Day(DailyReport day) => day.day;
            public List<ReportEntry> Entries(DailyReport day) => day.reportEntries;
            public int NoteStorageId(ReportEntry entry) => entry.noteStorageId;
            public int GameHash(ReportEntry entry) => entry.gameHash;
            public int ReportType(ReportEntry entry) => (int)entry.reportType;
            public string Context(ReportEntry entry) => entry.context;
            public int ContextId(ReportEntry entry) => entry.contextId;
            public float Accumulate(ReportEntry entry) => entry.accumulate;
            public float AccPositive(ReportEntry entry) => entry.accPositive;
            public float AccNegative(ReportEntry entry) => entry.accNegative;
            public ReportEntry[] ContextElements(ReportEntry entry) => entry.contextEntries.elements;
            public int ContextSize(ReportEntry entry) => entry.contextEntries.sizeImpl;
            public int ContextCapacity(ReportEntry entry) => entry.contextEntries.capacityImpl;
            public void DayTemplate() => Manager.GetSerializationTemplate(typeof(DailyReport));
            public void EntryTemplate() => Manager.GetSerializationTemplate(typeof(ReportEntry));
            public void ContextTemplate() => Manager.GetSerializationTemplate(typeof(ArrayRef<ReportEntry>));
            public void LargeString(string text, int byteCount) => global::Debug.LogWarning($"Writing large string {text} of {byteCount} bytes");
        }

        static readonly ReportEntryWriter<DailyReport, ReportEntry, Shapes> writer = new ReportEntryWriter<DailyReport, ReportEntry, Shapes>();

        /// The host's FastSerialization.Enabled, for the verify mode's game-only write.
        static FieldInfo hostEnabled;

        /// For the host's FastSerialization.Install: each field with a writer here and the writer
        /// (a method shaped as Helper.WriteValue), in pairs; none when the game's types are not
        /// as this writes them.
        public static object[] FieldWriters(FieldInfo enabled)
        {
            hostEnabled = enabled;
            string why = Unexpected();
            if (why != null)
            {
                global::Debug.Log($"{Name}: the report history keeps the game's writer ({why})");
                return new object[0];
            }
            return new object[]
            {
                AccessTools.Field(typeof(ReportManager), nameof(ReportManager.dailyReports)), AccessTools.Method(typeof(ReportEntries), nameof(WriteDays)),
                AccessTools.Field(typeof(ReportManager), nameof(ReportManager.todaysReport)), AccessTools.Method(typeof(ReportEntries), nameof(WriteDay)),
            };
        }

        /// What of the three templates is not what the writer writes, or null. A template is made
        /// from its type alone; these are not put in the save's directory.
        static string Unexpected()
        {
            const SerializationTypeInfo Generic = SerializationTypeInfo.IS_GENERIC_TYPE;
            return Unexpected(typeof(DailyReport),
                       ("day", typeof(int), SerializationTypeInfo.Int32), ("reportEntries", typeof(List<ReportEntry>), Generic | SerializationTypeInfo.List))
                   ?? Unexpected(typeof(ReportEntry),
                       ("noteStorageId", typeof(int), SerializationTypeInfo.Int32), ("gameHash", typeof(int), SerializationTypeInfo.Int32),
                       ("reportType", typeof(ReportManager.ReportType), SerializationTypeInfo.Enumeration), ("context", typeof(string), SerializationTypeInfo.String),
                       ("contextId", typeof(int), SerializationTypeInfo.Int32), ("accumulate", typeof(float), SerializationTypeInfo.Single),
                       ("accPositive", typeof(float), SerializationTypeInfo.Single), ("accNegative", typeof(float), SerializationTypeInfo.Single),
                       ("contextEntries", typeof(ArrayRef<ReportEntry>), Generic | SerializationTypeInfo.UserDefined))
                   ?? Unexpected(typeof(ArrayRef<ReportEntry>),
                       ("elements", typeof(ReportEntry[]), SerializationTypeInfo.Array), ("sizeImpl", typeof(int), SerializationTypeInfo.Int32),
                       ("capacityImpl", typeof(int), SerializationTypeInfo.Int32))
                   ?? (Enum.GetUnderlyingType(typeof(ReportManager.ReportType)) != typeof(int) ? "ReportType is not an int" : null)
                   ?? (BitConverter.IsLittleEndian ? null : "a big-endian machine");
        }

        static string Unexpected(Type type, params (string Name, Type Type, SerializationTypeInfo Info)[] fields)
        {
            var template = new SerializationTemplate(type);
            if (template.onSerializing != null || template.onSerialized != null || template.customSerialize != null) return $"{type.Name} has a serialization callback";
            if (template.serializableProperties.Count != 0) return $"{type.Name} saves a property";
            if (template.serializableFields.Count != fields.Length) return $"{type.Name} saves {template.serializableFields.Count} fields, not {fields.Length}";
            for (int i = 0; i < fields.Length; i++)
            {
                var field = template.serializableFields[i];
                if (field.field.Name != fields[i].Name || field.field.FieldType != fields[i].Type || field.typeInfo.info != fields[i].Info)
                    return $"{type.Name}'s saved field {i} is {field.field.FieldType.Name} {field.field.Name} ({field.typeInfo.info}), not {fields[i].Name}";
            }
            return null;
        }

        /// The methods the writer stands in for: another owner's patch on one sends the report
        /// history through the game's path, where that patch runs.
        internal static IEnumerable<MethodBase> Guarded()
        {
            yield return AccessTools.Method(typeof(Helper), nameof(Helper.WriteValue), new[] { typeof(BinaryWriter), typeof(TypeInfo), typeof(object) });
            yield return AccessTools.Method(typeof(SerializationTemplate), nameof(SerializationTemplate.SerializeData), new[] { typeof(object), typeof(BinaryWriter) });
            yield return AccessTools.Method(typeof(Manager), nameof(Manager.GetSerializationTemplate), new[] { typeof(Type) });
            yield return AccessTools.Method(typeof(IOHelper), nameof(IOHelper.WriteKleiString), new[] { typeof(BinaryWriter), typeof(string) });
            yield return AccessTools.Method(typeof(IOHelper), nameof(IOHelper.WriteSingleFast), new[] { typeof(BinaryWriter), typeof(float) });
            yield return AccessTools.Constructor(typeof(SerializationTemplate), new[] { typeof(Type) });
        }

        static MethodBase[] guarded;
        static string loggedForeign;

        /// Whether another owner patches a guarded method now, logged when it changes.
        static bool Foreign()
        {
            guarded ??= new List<MethodBase>(Guarded()).ToArray();
            string found = null;
            foreach (var method in guarded)
            {
                var info = method == null ? null : Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var owner in info.Owners)
                    if (!ModOverlapsCore.IsOurs(owner))
                    {
                        found = $"{owner} patches {method.DeclaringType.Name}.{method.Name}";
                        break;
                    }
                if (found != null) break;
            }
            if (found != loggedForeign)
            {
                loggedForeign = found;
                global::Debug.Log(found != null ? $"{Name}: the report history keeps the game's writer ({found})" : $"{Name}: the report history has its typed writer back");
            }
            return found != null;
        }

        static long verifiedWrites, mismatches;

        /// `dailyReports`, in place of Helper.WriteValue.
        public static void WriteDays(BinaryWriter target, TypeInfo typeInfo, object value)
        {
            var days = value as List<DailyReport>;
            if (Bypass || (value != null && days == null) || Foreign()) Helper.WriteValue(target, typeInfo, value);
            else if (verify) Verified(target, typeInfo, value, w => writer.WriteDays(w, days), "the report history");
            else writer.WriteDays(target, days);
        }

        /// `todaysReport`, in place of Helper.WriteValue.
        public static void WriteDay(BinaryWriter target, TypeInfo typeInfo, object value)
        {
            var day = value as DailyReport;
            if (Bypass || (value != null && day == null) || Foreign()) Helper.WriteValue(target, typeInfo, value);
            else if (verify) Verified(target, typeInfo, value, w => writer.WriteDay(w, day), "today's report");
            else writer.WriteDay(target, day);
        }

        /// Both ways into memory, compared; ours goes to the save. The game's is written with
        /// fast-serialization's plans off, so it is the game's own code all the way down.
        static void Verified(BinaryWriter target, TypeInfo typeInfo, object value, Action<BinaryWriter> write, string what)
        {
            var ours = new MemoryStream();
            var game = new MemoryStream();
            using (var w = new BinaryWriter(ours, System.Text.Encoding.UTF8, true)) write(w);
            bool plans = hostEnabled != null && (bool)hostEnabled.GetValue(null);
            try
            {
                hostEnabled?.SetValue(null, false);
                using (var w = new BinaryWriter(game, System.Text.Encoding.UTF8, true)) Helper.WriteValue(w, typeInfo, value);
            }
            finally
            {
                hostEnabled?.SetValue(null, plans);
            }
            verifiedWrites++;
            bool same = ours.Length == game.Length && ours.GetBuffer().AsSpan(0, (int)ours.Length).SequenceEqual(game.GetBuffer().AsSpan(0, (int)game.Length));
            if (!same) mismatches++;
            string line = $"{Name}: verify: {what} {(same ? "identical" : "DIFFERS from the game's")} ({ours.Length:N0} bytes against {game.Length:N0}; {verifiedWrites} writes, {mismatches} mismatches)";
            if (same) global::Debug.Log(line);
            else global::Debug.LogWarning(line);
            target.Write(ours.GetBuffer(), 0, (int)ours.Length);
        }
    }
}
