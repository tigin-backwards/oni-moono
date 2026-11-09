using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProcGenGame;

namespace E2eDriver
{
    /// What worldgen made, captured as each world finishes rendering (a postfix on
    /// WorldGen.RenderOffline, on worldgen's thread), before the game loads it and its
    /// simulation moves gas around: for comparing a seed's map between two players
    /// (README.md, "Worldgen reads"; the exact-worldgen spec).
    ///
    /// `type:E2eDriver.Worldgen::Lines`: one line a world, in ordinal order of its file path,
    /// `PATH WxH polys=N verts=HEX cells=HEX mass=HEX temp=HEX`, each HEX an FNV-1a 64: the
    /// overworld polygons' vertices (float bits, cell by cell in the layout's order), and the
    /// cells' element indices, mass bits and temperature bits as the settling simulation
    /// receives them (a prefix on WorldGenSimUtil.DoSettleSim). The settled cells are not
    /// compared: the settling does not repeat itself even in one player, where a few liquid
    /// cells, and at times a pocket of frozen gas, come out differently from run to run.
    /// `::Digest` hashes the lines. `Save(DIR)` writes each world's cells and polygons in full
    /// (WORLD.unsettled.tsv and WORLD.cells.tsv, before and after settling: `i elem mass_bits
    /// temp_bits`; WORLD.polys.tsv: `x_bits,y_bits` per vertex, a polygon a line) for finding
    /// where two players differ.
    public static class Worldgen
    {
        sealed class World
        {
            public string Path;
            public int Width, Height;
            public ushort[] Elements;
            public float[] Mass, Temperature;
            public List<List<UnityEngine.Vector2>> Polygons;
            public ushort[] Before;
            public float[] MassBefore, TemperatureBefore;
        }

        /// Each world's cells as the settling simulation receives them, by path.
        static readonly Dictionary<string, (ushort[] Elements, float[] Mass, float[] Temperature)> unsettled = new Dictionary<string, (ushort[], float[], float[])>(StringComparer.Ordinal);

        static readonly Dictionary<string, World> worlds = new Dictionary<string, World>(StringComparer.Ordinal);

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var target = HarmonyLib.AccessTools.Method(typeof(WorldGen), "RenderOffline");
            if (target == null)
            {
                Debug.LogWarning("[E2eDriver] worldgen capture: no WorldGen.RenderOffline");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(Worldgen), nameof(Rendered)));
            var settle = HarmonyLib.AccessTools.Method(typeof(WorldGenSimUtil), "DoSettleSim");
            if (settle != null) harmony.Patch(settle, prefix: new HarmonyLib.HarmonyMethod(typeof(Worldgen), nameof(Settling)));
        }

        static void Settling(ProcGen.WorldGenSettings settings, ref WorldgenSimData simData)
        {
            var cells = simData.cells;
            if (cells == null) return;
            var elements = new ushort[cells.Length];
            var mass = new float[cells.Length];
            var temperature = new float[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                elements[i] = cells[i].elementIdx;
                mass[i] = cells[i].mass;
                temperature[i] = cells[i].temperature;
            }
            lock (worlds) unsettled[settings?.world?.filePath ?? "?"] = (elements, mass, temperature);
        }

        static void Rendered(WorldGen __instance, ref WorldgenSimData simData, bool __result)
        {
            if (!__result || simData.cells == null) return;
            var size = __instance.WorldSize;
            var cells = simData.cells;
            var world = new World
            {
                Path = __instance.Settings?.world?.filePath ?? "?",
                Width = size.x,
                Height = size.y,
                Elements = new ushort[cells.Length],
                Mass = new float[cells.Length],
                Temperature = new float[cells.Length],
                Polygons = new List<List<UnityEngine.Vector2>>(),
            };
            lock (worlds)
                if (unsettled.TryGetValue(world.Path, out var before))
                    (world.Before, world.MassBefore, world.TemperatureBefore) = before;
            for (int i = 0; i < cells.Length; i++)
            {
                world.Elements[i] = cells[i].elementIdx;
                world.Mass[i] = cells[i].mass;
                world.Temperature[i] = cells[i].temperature;
            }
            foreach (var cell in __instance.OverworldCells)
                world.Polygons.Add(new List<UnityEngine.Vector2>(cell.poly.Vertices));
            lock (worlds) worlds[world.Path] = world;
        }

        const ulong Basis = 14695981039346656037UL, Prime = 1099511628211UL;

        static ulong Mix(ulong h, uint v)
        {
            for (int b = 0; b < 4; b++) h = (h ^ ((v >> (8 * b)) & 0xFF)) * Prime;
            return h;
        }

        static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

        static string Line(World w)
        {
            ulong verts = Basis, elements = Basis, mass = Basis, temp = Basis;
            foreach (var polygon in w.Polygons)
            {
                verts = Mix(verts, (uint)polygon.Count);
                foreach (var v in polygon) verts = Mix(Mix(verts, Bits(v.x)), Bits(v.y));
            }
            // The cells as worldgen made them: the settling simulation that follows does not give
            // the same cells twice, even in one player (liquids and gases flow differently).
            ushort[] cells = w.Before ?? w.Elements;
            float[] masses = w.MassBefore ?? w.Mass, temperatures = w.TemperatureBefore ?? w.Temperature;
            for (int i = 0; i < cells.Length; i++)
            {
                elements = Mix(elements, cells[i]);
                mass = Mix(mass, Bits(masses[i]));
                temp = Mix(temp, Bits(temperatures[i]));
            }
            return $"{w.Path} {w.Width}x{w.Height} polys={w.Polygons.Count} verts={verts:x16} cells={elements:x16} mass={mass:x16} temp={temp:x16}";
        }

        static List<World> Sorted()
        {
            lock (worlds) return worlds.Values.OrderBy(w => w.Path, StringComparer.Ordinal).ToList();
        }

        /// One line a world rendered since the driver loaded, joined by newlines.
        public static string Lines => string.Join("\n", Sorted().Select(Line).ToArray());

        /// The worlds rendered.
        public static int Count { get { lock (worlds) return worlds.Count; } }

        /// The FNV-1a 64 of Lines.
        public static string Digest
        {
            get
            {
                ulong h = Basis;
                foreach (byte b in Encoding.UTF8.GetBytes(Lines)) h = (h ^ b) * Prime;
                return h.ToString("x16");
            }
        }

        /// Writes each world's cells and polygons under `dir` (made if missing); returns how many
        /// worlds. Element indices are the game's (ElementLoader.elements order: elements.txt
        /// beside them names each).
        public static int Save(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(System.IO.Path.Combine(dir, "elements.txt"), ElementLoader.elements.Select(e => e.id.ToString()).ToArray());
            var all = Sorted();
            foreach (var w in all)
            {
                var name = w.Path.Replace("::", "-").Replace('/', '-');
                var sb = new StringBuilder();
                sb.Append("i\telem\tmass_bits\ttemp_bits\n");
                for (int i = 0; i < w.Elements.Length; i++)
                    sb.Append(i).Append('\t').Append(w.Elements[i]).Append('\t').Append(Bits(w.Mass[i]).ToString("X8")).Append('\t').Append(Bits(w.Temperature[i]).ToString("X8")).Append('\n');
                File.WriteAllText(System.IO.Path.Combine(dir, name + ".cells.tsv"), sb.ToString());
                if (w.Before != null)
                {
                    sb.Length = 0;
                    sb.Append("i\telem\tmass_bits\ttemp_bits\n");
                    for (int i = 0; i < w.Before.Length; i++)
                        sb.Append(i).Append('\t').Append(w.Before[i]).Append('\t').Append(Bits(w.MassBefore[i]).ToString("X8")).Append('\t').Append(Bits(w.TemperatureBefore[i]).ToString("X8")).Append('\n');
                    File.WriteAllText(System.IO.Path.Combine(dir, name + ".unsettled.tsv"), sb.ToString());
                }
                sb.Length = 0;
                foreach (var polygon in w.Polygons)
                    sb.Append(string.Join(" ", polygon.Select(v => Bits(v.x).ToString("X8") + "," + Bits(v.y).ToString("X8")).ToArray())).Append('\n');
                File.WriteAllText(System.IO.Path.Combine(dir, name + ".polys.tsv"), sb.ToString());
            }
            return all.Count;
        }
    }
}
