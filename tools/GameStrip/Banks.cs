using System.Buffers.Binary;

namespace GameStrip
{
    /// FMOD Studio banks without their sample data. A bank is a RIFF file ("FEV "): its PROJ
    /// list holds the events, buses, parameters and timelines, and its SND chunks hold the
    /// samples as FSB5 sound banks, 96% of its size. Without the SND chunks FMOD still loads the
    /// bank and every event: events have their lengths and parameters, start, play out their
    /// timelines and stop as before, only silently (an event whose only sound is a sample may end
    /// sooner). Measured against FMOD 2.02 (the game's libfmodstudio).
    static class Banks
    {
        public static byte[] Strip(byte[] bank, string name)
        {
            if (bank.Length < 12 || !bank.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bank.AsSpan(8, 4).SequenceEqual("FEV "u8))
                throw new InvalidDataException($"{name}: not an FMOD Studio bank");
            long end = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(4));
            if (end > bank.Length) throw new InvalidDataException($"{name}: truncated");
            using var output = new MemoryStream();
            output.Write(bank, 0, 12);
            int at = 12;
            while (at + 8 <= end)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at + 4));
                int chunk = checked(8 + (int)size + (int)(size & 1));
                if (at + chunk > end) throw new InvalidDataException($"{name}: chunk at {at} runs past the end");
                if (!bank.AsSpan(at, 4).SequenceEqual("SND "u8)) output.Write(bank, at, chunk);
                at += chunk;
            }
            var result = output.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
            return result;
        }
    }
}
