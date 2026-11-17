using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OniMoono.GameTweaks
{
    /// What the report history's writer asks of the game's types (ReportManager.DailyReport and
    /// ReportEntry, whose context entries are an ArrayRef of ReportEntry): their saved fields,
    /// and what the game's serializer does on the way that is not a byte in the save. A struct,
    /// so the writer is compiled for it.
    public interface IReportShapes<TDay, TEntry>
    {
        int Day(TDay day);
        List<TEntry> Entries(TDay day);
        int NoteStorageId(TEntry entry);
        int GameHash(TEntry entry);
        int ReportType(TEntry entry);
        string Context(TEntry entry);
        int ContextId(TEntry entry);
        float Accumulate(TEntry entry);
        float AccPositive(TEntry entry);
        float AccNegative(TEntry entry);
        /// The ArrayRef's three fields: its array (all of it is saved, not only `size` of it).
        TEntry[] ContextElements(TEntry entry);
        int ContextSize(TEntry entry);
        int ContextCapacity(TEntry entry);
        /// Manager.GetSerializationTemplate for the day's type, the entry's and the ArrayRef's:
        /// the first use of each in a save puts it in the save's directory of templates, in that
        /// order.
        void DayTemplate();
        void EntryTemplate();
        void ContextTemplate();
        /// WriteKleiString's warning for a string of 1,024 bytes or more.
        void LargeString(string text, int byteCount);
    }

    /// The bytes KSerialization writes for ReportManager's report history, without its walk:
    /// Helper.WriteValue for a List of DailyReport (`dailyReports`) and for one DailyReport
    /// (`todaysReport`), each object through its template's SerializeData, which boxes every
    /// field, looks the template up per object and seeks back in the stream for every length.
    ///
    /// The layout (all integers little-endian, as BinaryWriter writes them):
    /// - a list or an array: int32 bytes that follow the count, int32 count, the elements; null is
    ///   0 then -1;
    /// - an object (a day, an entry, an entry's ArrayRef): int32 bytes that follow, its fields in
    ///   the template's order; null is -1;
    /// - a string: int32 UTF-8 bytes, the bytes; null is -1.
    /// A day is built whole in a buffer, its lengths filled in there, and handed to the writer in
    /// blocks; only the list's own length is written by seeking back, once.
    public sealed class ReportEntryWriter<TDay, TEntry, TShapes>
        where TDay : class where TEntry : class where TShapes : struct, IReportShapes<TDay, TEntry>
    {
        const int Block = 1 << 17;
        byte[] buf = new byte[Block + (1 << 15)];
        int n;
        bool dayTemplate, entryTemplate, contextTemplate;
        readonly TShapes shapes = default;

        /// compact-report-entries' store, when it packs days: a packed day is written from its
        /// records, with the same bytes and the same templates asked for at the same points.
        public IPackedDays<TDay> Packed;

        /// Helper.WriteValue(writer, the List's type info, days).
        public void WriteDays(BinaryWriter writer, List<TDay> days)
        {
            if (days == null)
            {
                writer.Write(0);
                writer.Write(-1);
                return;
            }
            Begin();
            Packed?.BeforeWrite();
            long lengthAt = writer.BaseStream.Position;
            writer.Write(0);
            writer.Write(days.Count);
            long from = writer.BaseStream.Position;
            for (int i = 0; i < days.Count; i++)
            {
                Day(days[i]);
                if (n >= Block) Flush(writer);
            }
            Flush(writer);
            long to = writer.BaseStream.Position;
            writer.BaseStream.Position = lengthAt;
            writer.Write((int)(to - from));
            writer.BaseStream.Position = to;
        }

        /// Helper.WriteValue(writer, the DailyReport's type info, day).
        public void WriteDay(BinaryWriter writer, TDay day)
        {
            Begin();
            Day(day);
            Flush(writer);
        }

        void Begin()
        {
            n = 0;
            dayTemplate = entryTemplate = contextTemplate = false;
        }

        void Flush(BinaryWriter writer)
        {
            if (n > 0) writer.Write(buf, 0, n);
            n = 0;
        }

        void Room(int bytes)
        {
            if (n + bytes > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, n + bytes));
        }

        void Int(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(n), value);
            n += 4;
        }

        void Single(float value) => Int(BitConverter.SingleToInt32Bits(value));

        /// Room for a length, filled in by Close.
        int Open()
        {
            int at = n;
            n += 4;
            return at;
        }

        void Close(int at) => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(at), n - at - 4);

        void Day(TDay day)
        {
            Room(24);
            if (day == null)
            {
                Int(-1);
                return;
            }
            if (!dayTemplate)
            {
                dayTemplate = true;
                shapes.DayTemplate();
            }
            int length = Open();
            Int(shapes.Day(day));
            if (Packed != null && Packed.TryPacked(day, out int start, out int count))
            {
                PackedEntries(Packed.Records, start, count);
                Close(length);
                return;
            }
            var entries = shapes.Entries(day);
            if (entries == null)
            {
                Int(0);
                Int(-1);
            }
            else
            {
                int bytes = Open();
                Int(entries.Count);
                // The count is not among the bytes a list's length counts.
                int from = n;
                for (int i = 0; i < entries.Count; i++) Entry(entries[i]);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(bytes), n - from);
            }
            Close(length);
        }

        void Entry(TEntry entry)
        {
            Room(20);
            if (entry == null)
            {
                Int(-1);
                return;
            }
            if (!entryTemplate)
            {
                entryTemplate = true;
                shapes.EntryTemplate();
            }
            int length = Open();
            Int(shapes.NoteStorageId(entry));
            Int(shapes.GameHash(entry));
            Int(shapes.ReportType(entry));
            String(shapes.Context(entry));
            Room(40);
            Int(shapes.ContextId(entry));
            Single(shapes.Accumulate(entry));
            Single(shapes.AccPositive(entry));
            Single(shapes.AccNegative(entry));
            // The ArrayRef: a struct, so never null.
            if (!contextTemplate)
            {
                contextTemplate = true;
                shapes.ContextTemplate();
            }
            int context = Open();
            var elements = shapes.ContextElements(entry);
            if (elements == null)
            {
                Int(0);
                Int(-1);
            }
            else
            {
                int bytes = Open();
                Int(elements.Length);
                int from = n;
                for (int i = 0; i < elements.Length; i++) Entry(elements[i]);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(bytes), n - from);
                Room(16);
            }
            Int(shapes.ContextSize(entry));
            Int(shapes.ContextCapacity(entry));
            Close(context);
            Close(length);
        }

        /// A packed day's list: Day's for its entries, from the records.
        void PackedEntries(PackedReportRecords records, int start, int count)
        {
            Room(8);
            int bytes = Open();
            Int(count);
            int from = n;
            for (int i = 0; i < count; i++) PackedRecord(records, start + i);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(bytes), n - from);
        }

        /// Entry's bytes for a record (its `gameHash` is -1: the store packs no other).
        void PackedRecord(PackedReportRecords records, int r)
        {
            Room(20);
            if (!entryTemplate)
            {
                entryTemplate = true;
                shapes.EntryTemplate();
            }
            ref var rec = ref records[r];
            int length = Open();
            Int(rec.Id);
            Int(-1);
            Int(rec.Type);
            if (rec.Context < 0) Int(-1);
            else
            {
                var utf8 = records.Utf8(rec.Context);
                if (utf8.Length >= 1024) shapes.LargeString(records.String(rec.Context), utf8.Length);
                Room(4 + utf8.Length);
                Int(utf8.Length);
                utf8.AsSpan().CopyTo(buf.AsSpan(n));
                n += utf8.Length;
            }
            Room(40);
            Int(rec.ContextId);
            Single(rec.Accumulate);
            Single(rec.Positive);
            Single(rec.Negative);
            if (!contextTemplate)
            {
                contextTemplate = true;
                shapes.ContextTemplate();
            }
            int context = Open();
            if (rec.Length == PackedEntry.NoArray)
            {
                Int(0);
                Int(-1);
            }
            else
            {
                int bytes = Open();
                Int(rec.Length);
                int from = n;
                int children = rec.Children, size = rec.Size, total = rec.Length;
                for (int j = 0; j < size; j++) PackedRecord(records, children + j);
                Room((total - size) * 4 + 16);
                for (int j = size; j < total; j++) Int(-1);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(bytes), n - from);
            }
            Int(rec.Size);
            Int(rec.Capacity);
            Close(context);
            Close(length);
        }

        /// IOHelper.WriteKleiString.
        void String(string text)
        {
            if (text == null)
            {
                Room(4);
                Int(-1);
                return;
            }
            var utf8 = Encoding.UTF8;
            // A UTF-16 unit is never more than three UTF-8 bytes.
            int most = text.Length * 3;
            if (most >= 1024)
            {
                most = utf8.GetByteCount(text);
                if (most >= 1024) shapes.LargeString(text, most);
            }
            Room(4 + most);
            int count = utf8.GetBytes(text, 0, text.Length, buf, n + 4);
            Int(count);
            n += count;
        }
    }
}
