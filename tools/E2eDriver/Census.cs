using System;
using System.Collections.Generic;
using System.Linq;

namespace E2eDriver
{
    /// Counts of what the world holds, by prefab, for comparing two players' worlds in one value
    /// (README.md, "State reads"): `type:E2eDriver.Census::Pickupables`.
    public static class Census
    {
        /// Every live Pickupable's prefab id with its count, "ID=N" in ordinal order of the id,
        /// joined by spaces.
        public static string Pickupables
        {
            get
            {
                var counts = new Dictionary<string, int>();
                foreach (var p in Components.Pickupables.Items)
                {
                    var id = p.GetComponent<KPrefabID>();
                    string name = id != null ? id.PrefabTag.Name : p.name;
                    counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
                }
                return string.Join(" ", counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
        }

        /// Every live element chunk (a Pickupable with a PrimaryElement and an ElementChunk) as
        /// "ID@CELL", sorted ordinally, joined by spaces.
        public static string Chunks
        {
            get
            {
                var list = new List<string>();
                foreach (var p in Components.Pickupables.Items)
                    if (p.GetComponent<ElementChunk>() != null)
                        list.Add(p.GetComponent<KPrefabID>().PrefabTag.Name + "@" + Grid.PosToCell(p.transform.GetPosition()));
                list.Sort(StringComparer.Ordinal);
                return string.Join(" ", list.ToArray());
            }
        }

        /// The start world's cells as one FNV-1a 64 hash over each cell's element index, then
        /// "solid=N" and the hash of the cells' mass bits: "elements=HEX solid=N mass=HEX".
        public static string StartGrid
        {
            get
            {
                var w = ClusterManager.Instance.GetStartWorld();
                ulong he = 14695981039346656037UL, hm = he;
                int solid = 0;
                for (int y = w.WorldOffset.y; y < w.WorldOffset.y + w.Height; y++)
                    for (int x = w.WorldOffset.x; x < w.WorldOffset.x + w.Width; x++)
                    {
                        int c = Grid.XYToCell(x, y);
                        he = (he ^ Grid.ElementIdx[c]) * 1099511628211UL;
                        hm = (hm ^ BitConverter.ToUInt32(BitConverter.GetBytes(Grid.Mass[c]), 0)) * 1099511628211UL;
                        if (Grid.Solid[c]) solid++;
                    }
                return "elements=" + he.ToString("x16") + " solid=" + solid + " mass=" + hm.ToString("x16");
            }
        }
    }
}
