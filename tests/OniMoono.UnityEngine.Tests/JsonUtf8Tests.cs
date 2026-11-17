using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// background-save's colony summary JSON (src/OniMoono.GameTweaks/JsonUtf8.cs, compiled in
    /// here): the bytes in the kept blocks must be Encoding.UTF8.GetBytes(JsonConvert
    /// .SerializeObject(value)), the game's, for data shaped like RetiredColonyData.
    public class JsonUtf8Tests
    {
        public class Duplicant
        {
            public string name;
            public int age;
            public int skillPointsGained;
            public Dictionary<string, string> accessories;
        }

        public class Statistic
        {
            public string id;
            public Tuple<float, float>[] value;
            public string name;
            public string nameX;
            public string nameY;
        }

        /// RetiredColonyData's shape: strings, numbers, arrays, a list of tuples, a dictionary.
        public class Colony
        {
            public string colonyName;
            public int cycleCount;
            public string date;
            public string[] achievements;
            public Duplicant[] Duplicants;
            public List<Tuple<string, int>> buildings;
            public Statistic[] Stats;
            public Dictionary<string, string> worldIdentities;
            public string startWorld;
        }

        static void Same(object value)
        {
            byte[] game = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            var buffer = new JsonUtf8.Blocks();
            // Twice into the same blocks: the second must not keep anything of the first.
            JsonUtf8.Serialize(new Colony { colonyName = new string('x', 200_000) }, buffer);
            JsonUtf8.Serialize(value, buffer);
            Assert.Equal(game.Length, (int)buffer.Length);
            Assert.True(buffer.SameAs(game));
            var file = new MemoryStream();
            buffer.WriteTo(file);
            Assert.Equal(game, file.ToArray());
        }

        static Colony Make(int cycles, string name)
        {
            float[] odd = [0f, -0f, 1e-7f, 3.4028235e38f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.1f, 123456.79f, -42.5f];
            var stats = new Statistic[16];
            for (int s = 0; s < stats.Length; s++)
            {
                var points = new Tuple<float, float>[cycles];
                for (int i = 0; i < cycles; i++) points[i] = new Tuple<float, float>(i, i % 11 == 0 ? odd[(i / 11) % odd.Length] : i * 0.37f * (s + 1));
                stats[s] = new Statistic { id = "Stat" + s, value = points, name = "Ünïcode ✓ " + s, nameX = "Cycles", nameY = "kg" };
            }
            return new Colony
            {
                colonyName = name,
                cycleCount = cycles,
                date = "11/17/2026",
                achievements = ["Survived100Cycles", "Ünïcode"],
                Duplicants =
                [
                    new Duplicant { name = "Méi \"Quote\" \\ Back", age = 17_000, skillPointsGained = 40, accessories = new Dictionary<string, string> { ["Hair"] = "hair_042", ["Hat"] = "hat_🎩" } },
                    new Duplicant { name = "Tab\tNew\nLine\u0001", age = 1, accessories = new Dictionary<string, string>() },
                ],
                buildings = [new Tuple<string, int>("Tile", 120_000), new Tuple<string, int>("LiquidConduit", 9_001)],
                Stats = stats,
                worldIdentities = new Dictionary<string, string> { ["00000"] = "Terra", ["00001"] = "Обломок" },
                startWorld = "00000",
            };
        }

        [Fact]
        public void Small_colony_same_bytes() => Same(Make(12, "Colony"));

        [Fact]
        public void Large_colony_same_bytes() => Same(Make(17_000, "Late Game ☄"));

        [Fact]
        public void Null_and_empty_same_bytes()
        {
            Same(new Colony());
            Same(null);
        }

        /// Surrogate pairs at every offset across the writer's buffer boundaries, and unpaired
        /// halves, which both encodings replace with U+FFFD.
        [Fact]
        public void Surrogates_across_buffers_same_bytes()
        {
            var text = new StringBuilder();
            for (int i = 0; i < 40_000; i++)
            {
                text.Append('a', i % 7);
                text.Append("😀");
                if (i % 997 == 0) text.Append('\uD800');
                if (i % 1009 == 0) text.Append('\uDC00');
            }
            text.Append('\uD83D');
            Same(new Colony { colonyName = text.ToString(), achievements = [text.ToString(0, 20_001)] });
        }
    }
}
