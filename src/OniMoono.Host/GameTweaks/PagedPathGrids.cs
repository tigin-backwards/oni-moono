using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using HarmonyLib;
using OniMoono.Engine;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `paged-path-grids` (PathGridPages, PathGridPagesRewrite): enabled before any game
    /// assembly loads, as the rewrite runs while they load; pages or flat arrays from the first
    /// PathGrid on (Decide), for the whole session.
    internal static class PagedPathGrids
    {
        public const string Name = GameTweakNames.PagedPathGrids;

        /// Program, before the game's assemblies load.
        public static void Enable()
        {
            PathGridPagesRewrite.Enabled = true;
            PathGridPages.Decide = Decide;
        }

        /// GameTweaks.Install: whether the game's PathGrid was rewritten, and the tables' summary
        /// (and verify mode's comparisons) in play.
        public static void Install(Harmony harmony)
        {
            if (AccessTools.TypeByName("PathGrid") is not { } pathGrid || AccessTools.Field(pathGrid, PathGridPagesRewrite.PagesField("Cells")) == null)
            {
                EngineLog.Engine($"game tweak {Name}: flat arrays: the game's PathGrid is not rewritten (the log of its first load says why)");
                return;
            }
            harmony.Patch(AccessTools.Method("AsyncPathProber+Manager:TickFrame") ?? throw new MissingMethodException("AsyncPathProber.Manager", "TickFrame"),
                postfix: new HarmonyMethod(typeof(PagedPathGrids), nameof(Ticked)));
        }

        /// At the first PathGrid: null for pages, else why the session runs flat. Logged.
        static string? Decide()
        {
            string? reason = Reason();
            EngineLog.Engine(reason == null
                ? $"game tweak {Name}: pages of {PathGridPages.PageSize} elements, made on first write" + (PathGridPages.Verify ? " (verify: each with the flat array beside it)" : "")
                : $"game tweak {Name}: flat arrays for this session: {reason}");
            return reason;
        }

        static string? Reason()
        {
            lock (PathGridPagesRewrite.Problems)
                if (PathGridPagesRewrite.Problems.Count > 0) return "a mod uses PathGrid's arrays otherwise than to lock them: " + PathGridPagesRewrite.Problems[0];
            // Every assembly that names the arrays was checked by the rewrite.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var name = assembly.GetName().Name ?? "";
                if (name == "Assembly-CSharp" || name.StartsWith("OniMoono", StringComparison.Ordinal) || AssemblyPolicy.IsFrameworkAssembly(name)) continue;
                bool analysed;
                lock (PathGridPagesRewrite.Analysed) analysed = PathGridPagesRewrite.Analysed.Contains(name);
                if (!analysed && NamesArrays(assembly)) return $"{name} names PathGrid's arrays but was loaded without the rewrite's check";
            }
            // No transpiler of a method that reads or writes them: it would see the helpers.
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                if (method.DeclaringType?.FullName != "PathGrid") continue;
                var info = Harmony.GetPatchInfo(method);
                if (info == null || info.Transpilers.Count == 0 || !CallsHelpers(method)) continue;
                var patch = info.Transpilers[0];
                return $"{patch.PatchMethod.DeclaringType?.FullName}.{patch.PatchMethod.Name} ({patch.owner}) transpiles PathGrid.{method.Name}, which reads the arrays";
            }
            return null;
        }

        static bool CallsHelpers(MethodBase method)
        {
            try
            {
                return PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo { DeclaringType: var t } && t == typeof(PathGridPages));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return true;
            }
        }

        /// Whether a loaded assembly's metadata references a field Cells or ProberCells of a type
        /// PathGrid it does not define.
        static unsafe bool NamesArrays(Assembly assembly)
        {
            try
            {
                if (!assembly.TryGetRawMetadata(out byte* blob, out int length)) return true;
                var reader = new MetadataReader(blob, length);
                foreach (var handle in reader.MemberReferences)
                {
                    var member = reader.GetMemberReference(handle);
                    if (member.GetKind() != MemberReferenceKind.Field || member.Parent.Kind != HandleKind.TypeReference) continue;
                    if (!reader.StringComparer.Equals(member.Name, "Cells") && !reader.StringComparer.Equals(member.Name, "ProberCells")) continue;
                    if (reader.StringComparer.Equals(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name, "PathGrid")) return true;
                }
                return false;
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                return true;
            }
        }

        /// --check-types: the loaded game's PathGrid, rewritten, answers as the game's does on a
        /// small grid written and read through its own methods (SetCell, GetCell, GetCost,
        /// GetCostIgnoreProberOffset, ResetProberCells), against a model of the game's arrays.
        internal static string SelfCheck()
        {
            if (!PathGridPagesRewrite.Enabled) return "off: the game's IL as it is";
            // GetCostIgnoreProberOffset asks Grid for the offset cells: the grid's size, for the check.
            var gridType = AccessTools.TypeByName("Grid") ?? throw new InvalidOperationException("no Grid");
            var widthField = AccessTools.Field(gridType, "WidthInCells");
            var countField = AccessTools.Field(gridType, "CellCount");
            object? oldWidth = widthField.GetValue(null), oldCount = countField.GetValue(null);
            widthField.SetValue(null, CheckWidth);
            countField.SetValue(null, CheckWidth * CheckHeight);
            try
            {
                return Exercise();
            }
            finally
            {
                widthField.SetValue(null, oldWidth);
                countField.SetValue(null, oldCount);
            }
        }

        const int CheckWidth = 40, CheckHeight = 20;

        static string Exercise()
        {
            var grid = AccessTools.TypeByName("PathGrid") ?? throw new InvalidOperationException("no PathGrid");
            if (AccessTools.Field(grid, PathGridPagesRewrite.PagesField("Cells")) == null) throw new InvalidOperationException("PathGrid is not rewritten");
            var navType = AccessTools.TypeByName("NavType");
            var cellType = AccessTools.TypeByName("PathFinder+Cell");
            var pathType = AccessTools.TypeByName("PathFinder+PotentialPath");
            var flagsType = AccessTools.TypeByName("PathFinder+PotentialPath+Flags");
            var offsetType = AccessTools.TypeByName("CellOffset");
            // 40×20 cells (3 pages and part of a fourth of prober cells), Floor and Ladder.
            const int width = CheckWidth, height = CheckHeight, cells = width * height;
            var navs = Array.CreateInstance(navType, 2);
            navs.SetValue(Enum.ToObject(navType, 0), 0);
            navs.SetValue(Enum.ToObject(navType, 4), 1);
            var pg = Activator.CreateInstance(grid, width, height, false, navs)!;
            var handle = (Array)AccessTools.Field(grid, "Cells").GetValue(pg)!;
            int length = (int)AccessTools.Field(grid, PathGridPagesRewrite.LengthField("Cells")).GetValue(pg)!;
            var table = (Array)AccessTools.Field(grid, PathGridPagesRewrite.PagesField("Cells")).GetValue(pg)!;
            if (length != cells * 2) throw new InvalidOperationException($"Cells' length {length}, {cells * 2} expected");
            if (PathGridPages.Paged ? handle.Length != 1 || table.Length != (cells * 2 + 255) / 256 : handle.Length != cells * 2 || table.Length != 1)
                throw new InvalidOperationException($"Cells is {handle.Length} long, its table {table.Length}");
            var begin = AccessTools.Method(grid, "BeginUpdate");
            var setCell = AccessTools.Method(grid, "SetCell");
            var getCell = AccessTools.Method(grid, "GetCell", [typeof(int), navType, typeof(bool).MakeByRefType()]);
            var getCost = AccessTools.Method(grid, "GetCost");
            var ignoreOffset = AccessTools.Method(grid, "GetCostIgnoreProberOffset");
            var reset = AccessTools.Method(grid, "ResetProberCells");
            var cost = AccessTools.Field(cellType, "cost");
            var parent = AccessTools.Field(cellType, "parent");
            var queryId = AccessTools.Field(cellType, "queryId");
            // The game's arrays, modelled: (cost, parent, serial) a state; (cost, serial, nav) a cell.
            var modelCells = new (int Cost, int Parent, ushort Serial)[cells * 2];
            var modelProbers = new (int Cost, ushort Serial, int Nav)[cells];
            var random = new Random(7);
            ushort serial = 0;
            int checks = 0;
            for (int round = 0; round < 40; round++)
            {
                if (++serial == 0) serial = 1;
                begin.Invoke(pg, [serial, 0, null]);
                for (int n = 0; n < 300; n++)
                {
                    int cell = random.Next(cells), slot = random.Next(2), c = random.Next(1, 1000);
                    var data = Activator.CreateInstance(cellType)!;
                    cost.SetValue(data, c);
                    parent.SetValue(data, cell - 1);
                    var path = Activator.CreateInstance(pathType, cell, Enum.ToObject(navType, slot == 0 ? 0 : 4), Enum.ToObject(flagsType, 0))!;
                    var args = new object[] { path, data };
                    setCell.Invoke(pg, args);
                    modelCells[cell * 2 + slot] = (c, cell - 1, serial);
                    var p = modelProbers[cell];
                    if (serial != p.Serial || c < p.Cost) modelProbers[cell] = (c, serial, slot);
                }
                for (int cell = 0; cell < cells; cell++)
                {
                    for (int slot = 0; slot < 2; slot++)
                    {
                        var args = new object?[] { cell, Enum.ToObject(navType, slot == 0 ? 0 : 4), false };
                        var got = getCell.Invoke(pg, args)!;
                        var m = modelCells[cell * 2 + slot];
                        bool valid = m.Serial == serial;
                        int expected = valid ? m.Cost : -1;
                        if ((int)cost.GetValue(got)! != expected || valid && ((int)parent.GetValue(got)! != m.Parent || (ushort)queryId.GetValue(got)! != serial) || !(bool)args[2]!)
                            throw new InvalidOperationException($"GetCell({cell}, {slot}) in round {round}: {cost.GetValue(got)}, {expected} expected");
                        checks++;
                    }
                    var mp = modelProbers[cell];
                    int want = mp.Serial == serial ? mp.Cost : -1;
                    if ((int)getCost.Invoke(pg, [cell])! != want) throw new InvalidOperationException($"GetCost({cell}) in round {round}: {getCost.Invoke(pg, [cell])}, {want} expected");
                    checks++;
                }
                // Two cells' offsets, as chores ask.
                var offsets = Array.CreateInstance(offsetType, 2);
                offsets.SetValue(Activator.CreateInstance(offsetType, 0, 0), 0);
                offsets.SetValue(Activator.CreateInstance(offsetType, 1, 0), 1);
                int at = random.Next(cells - 1);
                int a = modelProbers[at].Serial == serial ? modelProbers[at].Cost : -1, b = modelProbers[at + 1].Serial == serial ? modelProbers[at + 1].Cost : -1;
                int best = a == -1 ? b : b == -1 ? a : Math.Min(a, b);
                if ((int)ignoreOffset.Invoke(pg, [at, offsets])! != best) throw new InvalidOperationException($"GetCostIgnoreProberOffset({at}): {ignoreOffset.Invoke(pg, [at, offsets])}, {best} expected");
                if (round % 10 == 9)
                {
                    reset.Invoke(pg, null);
                    Array.Clear(modelProbers);
                }
            }
            return $"{checks:N0} reads of a {width}×{height} grid answer as the game's arrays ({(PathGridPages.Paged ? $"pages of {PathGridPages.PageSize}" : "flat: " + PathGridPages.FlatReason)})";
        }

        static readonly Stopwatch sinceSummary = new(), sinceVerify = new();
        static bool summarized;

        /// Each frame of the path probes (main thread): a summary after the first minute of play
        /// and every 10 minutes; in verify mode every table against its flat array every 30 s.
        static void Ticked()
        {
            if (!PathGridPages.Paged) return;
            if (!sinceSummary.IsRunning) sinceSummary.Start();
            if (!sinceVerify.IsRunning) sinceVerify.Start();
            try
            {
                if (sinceSummary.Elapsed.TotalSeconds >= (summarized ? 600 : 60))
                {
                    summarized = true;
                    sinceSummary.Restart();
                    var (tables, pages, table, flat) = PathGridPages.Size();
                    EngineLog.Engine($"path grids: {tables / 2} grids in pages: {(pages + table) / 1e6:N1} MB ({pages / 1e6:N1} MB of pages written, {table / 1e6:N1} MB of tables); the arrays: {flat / 1e6:N1} MB");
                }
                if (PathGridPages.Verify && sinceVerify.Elapsed.TotalSeconds >= 30)
                {
                    sinceVerify.Restart();
                    var watch = Stopwatch.StartNew();
                    int wrong = PathGridPages.VerifyAll(out int tables, out long elements, out string? first, out int midWrite, out int unsettled);
                    EngineLog.Engine($"path grids verify: {tables} tables, {elements:N0} elements, {wrong} differ from the flat arrays, {midWrite} only mid-write"
                                     + (unsettled > 0 ? $", {unsettled} not compared (written throughout)" : "") + (first != null ? $"; first: {first}" : "")
                                     + $" ({watch.ElapsedMilliseconds} ms)");
                }
            }
            catch (Exception e)
            {
                EngineLog.Engine($"path grids: summary failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
