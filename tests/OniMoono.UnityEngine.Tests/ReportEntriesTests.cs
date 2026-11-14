using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fast-serialization's writer for the report history (src/OniMoono.GameTweaks/ReportEntriesCore.cs,
    /// compiled in here) against the game's serializer: KSerialization's Helper.WriteValue,
    /// SerializationTemplate.SerializeData, Manager.GetSerializationTemplate and
    /// IOHelper.WriteKleiString (U59 744825 and U60, copied below for the kinds these types
    /// have), over types shaped as ReportManager.DailyReport, ReportEntry and ArrayRef. The bytes,
    /// the templates asked for and their order, and the large-string warnings must be the game's.
    public class ReportEntriesTests
    {
        enum Kind { Zero, One, Two }

        sealed class Day
        {
            public int day;
            public List<Entry> reportEntries = new List<Entry>();
        }

        sealed class Entry
        {
            public int noteStorageId;
            public int gameHash = -1;
            public Kind reportType;
            public string context;
            public int contextId;
            public float accumulate;
            public float accPositive;
            public float accNegative;
            public Refs contextEntries;
            // Not saved (the game's is not marked [Serialize]).
            public bool isChild;
        }

        /// The game's ArrayRef: `elements` is saved whole, past `sizeImpl` too.
        struct Refs
        {
            public Entry[] elements;
            public int sizeImpl;
            public int capacityImpl;
        }

        // ---- The game's serializer ----

        /// KSerialization, for these types: a template per type (its saved fields in order),
        /// made at the first GetSerializationTemplate and kept in the order made, as
        /// Manager.SerializeDirectory then writes them.
        sealed class Game
        {
            public readonly List<Type> Templates = new List<Type>();
            public readonly List<string> Warnings = new List<string>();
            readonly Dictionary<Type, FieldInfo[]> byType = new Dictionary<Type, FieldInfo[]>();

            static readonly Dictionary<Type, string[]> saved = new Dictionary<Type, string[]>
            {
                [typeof(Day)] = new[] { "day", "reportEntries" },
                [typeof(Entry)] = new[] { "noteStorageId", "gameHash", "reportType", "context", "contextId", "accumulate", "accPositive", "accNegative", "contextEntries" },
                [typeof(Refs)] = new[] { "elements", "sizeImpl", "capacityImpl" },
            };

            /// Manager.GetSerializationTemplate(Type).
            FieldInfo[] GetSerializationTemplate(Type type)
            {
                if (!byType.TryGetValue(type, out var value))
                {
                    value = saved[type].Select(name => type.GetField(name)).ToArray();
                    byType[type] = value;
                    Templates.Add(type);
                }
                return value;
            }

            /// SerializationTemplate.SerializeData (these types have no callbacks or properties).
            void SerializeData(FieldInfo[] template, object obj, BinaryWriter writer)
            {
                foreach (var field in template)
                {
                    object value = field.GetValue(obj);
                    WriteValue(writer, field.FieldType, value);
                }
            }

            /// IOHelper.WriteKleiString.
            void WriteKleiString(BinaryWriter writer, string str)
            {
                if (str != null)
                {
                    Encoding uTF = Encoding.UTF8;
                    int byteCount = uTF.GetByteCount(str);
                    writer.Write(byteCount);
                    if (byteCount < 1024)
                    {
                        var buffer = new byte[1024];
                        uTF.GetBytes(str, 0, str.Length, buffer, 0);
                        writer.Write(buffer, 0, byteCount);
                    }
                    else
                    {
                        Warnings.Add($"Writing large string {str} of {byteCount} bytes");
                        writer.Write(uTF.GetBytes(str));
                    }
                }
                else
                {
                    writer.Write(-1);
                }
            }

            /// Helper.WriteValue, by the kind Helper.EncodeSerializationType gives the type.
            public void WriteValue(BinaryWriter writer, Type type, object value)
            {
                if (type == typeof(int)) writer.Write((int)value);
                else if (type == typeof(float)) writer.Write((float)value); // WriteSingleFast: the same four bytes
                else if (type == typeof(string)) WriteKleiString(writer, (string)value);
                else if (type.IsEnum) writer.Write((int)value);
                else if (type.IsArray)
                {
                    if (value != null)
                    {
                        Array array = value as Array;
                        Type elem = type.GetElementType();
                        long position16 = writer.BaseStream.Position;
                        writer.Write(0);
                        writer.Write(array.Length);
                        long position17 = writer.BaseStream.Position;
                        for (int j = 0; j < array.Length; j++)
                        {
                            WriteValue(writer, elem, array.GetValue(j));
                        }
                        long position18 = writer.BaseStream.Position;
                        long num7 = position18 - position17;
                        writer.BaseStream.Position = position16;
                        writer.Write((int)num7);
                        writer.BaseStream.Position = position18;
                    }
                    else
                    {
                        writer.Write(0);
                        writer.Write(-1);
                    }
                }
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    if (value != null)
                    {
                        Type elem = type.GetGenericArguments()[0];
                        ICollection collection2 = value as ICollection;
                        long position19 = writer.BaseStream.Position;
                        writer.Write(0);
                        writer.Write(collection2.Count);
                        long position20 = writer.BaseStream.Position;
                        foreach (object item4 in collection2)
                        {
                            WriteValue(writer, elem, item4);
                        }
                        long position21 = writer.BaseStream.Position;
                        long num8 = position21 - position20;
                        writer.BaseStream.Position = position19;
                        writer.Write((int)num8);
                        writer.BaseStream.Position = position21;
                    }
                    else
                    {
                        writer.Write(0);
                        writer.Write(-1);
                    }
                }
                else
                {
                    // UserDefined.
                    if (value != null)
                    {
                        long position7 = writer.BaseStream.Position;
                        writer.Write(0);
                        long position8 = writer.BaseStream.Position;
                        var serializationTemplate3 = GetSerializationTemplate(type);
                        SerializeData(serializationTemplate3, value, writer);
                        long position9 = writer.BaseStream.Position;
                        long num4 = position9 - position8;
                        writer.BaseStream.Position = position7;
                        writer.Write((int)num4);
                        writer.BaseStream.Position = position9;
                    }
                    else
                    {
                        writer.Write(-1);
                    }
                }
            }
        }

        // ---- The writer under test ----

        static List<Type> templates;
        static List<string> warnings;

        struct Shapes : IReportShapes<Day, Entry>
        {
            public int Day(Day day) => day.day;
            public List<Entry> Entries(Day day) => day.reportEntries;
            public int NoteStorageId(Entry entry) => entry.noteStorageId;
            public int GameHash(Entry entry) => entry.gameHash;
            public int ReportType(Entry entry) => (int)entry.reportType;
            public string Context(Entry entry) => entry.context;
            public int ContextId(Entry entry) => entry.contextId;
            public float Accumulate(Entry entry) => entry.accumulate;
            public float AccPositive(Entry entry) => entry.accPositive;
            public float AccNegative(Entry entry) => entry.accNegative;
            public Entry[] ContextElements(Entry entry) => entry.contextEntries.elements;
            public int ContextSize(Entry entry) => entry.contextEntries.sizeImpl;
            public int ContextCapacity(Entry entry) => entry.contextEntries.capacityImpl;
            public void DayTemplate() => Template(typeof(Day));
            public void EntryTemplate() => Template(typeof(Entry));
            public void ContextTemplate() => Template(typeof(Refs));
            public void LargeString(string text, int byteCount) => warnings.Add($"Writing large string {text} of {byteCount} bytes");

            /// Manager.GetSerializationTemplate: a type's template is made once.
            static void Template(Type type)
            {
                if (!templates.Contains(type)) templates.Add(type);
            }
        }

        readonly ReportEntryWriter<Day, Entry, Shapes> writer = new ReportEntryWriter<Day, Entry, Shapes>();

        /// ReportManager's two fields as a save writes them, one after the other into one stream
        /// with something before them (the lengths are filled in by seeking back), both ways.
        void Same(List<Day> days, Day today)
        {
            var game = new Game();
            var theirs = new MemoryStream();
            using (var w = new BinaryWriter(theirs, Encoding.UTF8, true))
            {
                w.Write(0x11223344);
                game.WriteValue(w, typeof(List<Day>), days);
                game.WriteValue(w, typeof(Day), today);
                w.Write((byte)0x55);
            }
            templates = new List<Type>();
            warnings = new List<string>();
            var ours = new MemoryStream();
            using (var w = new BinaryWriter(ours, Encoding.UTF8, true))
            {
                w.Write(0x11223344);
                writer.WriteDays(w, days);
                writer.WriteDay(w, today);
                w.Write((byte)0x55);
            }
            Assert.True(theirs.ToArray().AsSpan().SequenceEqual(ours.ToArray()), $"{ours.Length} bytes against the game's {theirs.Length}");
            Assert.Equal(game.Templates, templates);
            Assert.Equal(game.Warnings, warnings);
        }

        static Entry E(int id, string context = null, params Entry[] children)
        {
            var entry = new Entry { isChild = id % 2 == 0, noteStorageId = id, reportType = (Kind)(id % 3), context = context, contextId = id * 7, accumulate = id * 1.5f, accPositive = id * 2.25f, accNegative = -id * 0.75f };
            if (children.Length > 0) entry.contextEntries = new Refs { elements = children, sizeImpl = children.Length, capacityImpl = children.Length };
            return entry;
        }

        static Day D(int day, params Entry[] entries) => new Day { day = day, reportEntries = entries.ToList() };

        [Fact]
        public void NoReportsAtAll()
        {
            Same(null, null);
            Same(new List<Day>(), null);
            Same(null, D(1));
            Same(new List<Day>(), D(1));
        }

        [Fact]
        public void NullsWhereverTheGameWritesOne()
        {
            // A null day, a day without a list, an empty list, a null entry: no template is asked
            // for until something of its type is written.
            Same(new List<Day> { null }, null);
            Same(new List<Day> { null, new Day { day = 3, reportEntries = null }, null }, null);
            Same(new List<Day> { D(1) }, new Day { day = 2, reportEntries = null });
            Same(new List<Day> { D(1, null, null) }, D(2, (Entry)null));
            Same(new List<Day> { D(1, null, E(1), null) }, D(2, E(2)));
            Same(new List<Day> { null }, D(2, E(2, "today")));
        }

        [Fact]
        public void ContextEntriesAsTheArrayRefHoldsThem()
        {
            // Never used (no array), emptied (an array, size 0), with room left (nulls past the
            // size), and entries below entries.
            var never = E(1, "never");
            var emptied = E(2, "emptied");
            emptied.contextEntries = new Refs { elements = new Entry[0], sizeImpl = 0, capacityImpl = 0 };
            var room = E(3, "room");
            room.contextEntries = new Refs { elements = new[] { E(4, "a"), E(5, "b"), null, null }, sizeImpl = 2, capacityImpl = 4 };
            // What RemoveAt leaves, and fields that disagree with the array: written as they are.
            var odd = E(6, "odd");
            odd.contextEntries = new Refs { elements = new[] { null, E(7) }, sizeImpl = 5, capacityImpl = -1 };
            var deep = E(8, "deep", E(9, "child", E(10, "grandchild", E(11)), E(12)), E(13));
            Same(new List<Day> { D(1, never, emptied, room, odd, deep) }, D(2, deep, room));
        }

        [Fact]
        public void Strings()
        {
            string k1023 = new string('x', 1023), k1024 = new string('x', 1024);
            // 341 and 342 three-byte characters: 1,023 and 1,026 bytes, around the warning's limit.
            string wide341 = new string('中', 341), wide342 = new string('中', 342);
            var entries = new[]
            {
                E(1, null), E(2, ""), E(3, "Meep"), E(4, "éñ 中文 😀"),
                // A lone surrogate: UTF-8 writes U+FFFD for it.
                E(5, "a\ud800b"), E(6, "\udc00"),
                E(7, k1023), E(8, k1024), E(9, wide341), E(10, wide342), E(11, new string('y', 5000)),
                E(12, new string('é', 511)), E(13, new string('é', 512)), E(14, "\0"),
            };
            Same(new List<Day> { D(1, entries) }, D(2, E(15, k1024, E(16, wide342))));
        }

        [Fact]
        public void FloatsKeepTheirBits()
        {
            var entry = E(1, "bits");
            entry.accumulate = -0f;
            entry.accPositive = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001));
            entry.accNegative = float.NegativeInfinity;
            var other = E(2);
            other.accumulate = BitConverter.Int32BitsToSingle(0x7FA00000);
            other.accPositive = float.Epsilon;
            other.accNegative = float.MaxValue;
            other.gameHash = int.MinValue;
            other.contextId = -1;
            Same(new List<Day> { D(int.MinValue, entry, other) }, D(int.MaxValue, other));
        }

        /// More than the writer hands over in one block, and one day larger than its buffer: the
        /// blocks end between days, and a day's lengths are filled in before it goes.
        [Fact]
        public void ManyDaysAndAVeryLargeDay()
        {
            var days = new List<Day>();
            for (int d = 0; d < 3000; d++)
                days.Add(D(d, Enumerable.Range(0, 12).Select(i => E(d * 100 + i, i % 3 == 0 ? null : "Duplicant " + i, i % 4 == 0 ? new[] { E(i, "child " + d), E(i + 1) } : new Entry[0])).ToArray()));
            var huge = D(-5, Enumerable.Range(0, 9000).Select(i => E(i, new string((char)('a' + i % 26), i % 200), E(i))).ToArray());
            days.Insert(1500, huge);
            days.Insert(10, null);
            Same(days, huge);
            // The same writer again: nothing of the last write is left in it.
            Same(new List<Day> { D(1, E(1)) }, null);
        }

        [Fact]
        public void RandomHistories()
        {
            var random = new Random(20261113);
            string[] names = { null, "", "Ada", "Meep", "Stinky", "中文", "Hatch", new string('n', 300), new string('中', 400) };
            Entry Make(int depth)
            {
                if (random.Next(20) == 0) return null;
                var entry = new Entry
                {
                    noteStorageId = random.Next(), gameHash = random.Next(3) == 0 ? -1 : random.Next(), reportType = (Kind)random.Next(3),
                    context = names[random.Next(names.Length)], contextId = random.Next(-5, 5),
                    accumulate = (float)(random.NextDouble() * 1000 - 500), accPositive = (float)random.NextDouble(), accNegative = -(float)random.NextDouble(),
                };
                int kind = random.Next(4);
                if (kind == 1) entry.contextEntries = new Refs { elements = new Entry[random.Next(3)] };
                else if (kind >= 2 && depth < 3)
                {
                    int capacity = random.Next(1, 9), size = random.Next(capacity + 1);
                    var elements = new Entry[capacity];
                    for (int i = 0; i < size; i++) elements[i] = Make(depth + 1);
                    entry.contextEntries = new Refs { elements = elements, sizeImpl = size, capacityImpl = capacity };
                }
                return entry;
            }
            Day MakeDay(int day)
            {
                if (random.Next(30) == 0) return null;
                var made = new Day { day = day };
                if (random.Next(30) == 0) made.reportEntries = null;
                else
                    for (int i = random.Next(25); i > 0; i--) made.reportEntries.Add(Make(0));
                return made;
            }
            for (int round = 0; round < 40; round++)
            {
                var days = random.Next(10) == 0 ? null : Enumerable.Range(0, random.Next(60)).Select(MakeDay).ToList();
                Same(days, MakeDay(-1));
            }
        }
    }
}
