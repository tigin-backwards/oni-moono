using System;
using System.Collections.Generic;
using OniMoono.Assets.Shaders;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;

namespace OniMoono.Desktop.D3d11
{
    /// --check-shaders on Direct3D 11 (d3d11-plan.md, done criterion 1: how every pass and
    /// variant is proven): every player subprogram of every pass of every loaded shader is made a
    /// shader object on the device from its shipped DXBC, in the program the draw path would
    /// build for its keywords, and each vertex program gets the input layouts a draw makes,
    /// which CreateInputLayout validates against the vertex stage's input signature. Nothing is
    /// drawn. Counted: subprograms selected and created, programs built, layouts made, and every
    /// refusal or failure. Programs refused for structured buffers or multisample textures are
    /// listed one by one and counted apart, as refused and unreachable, not as failures, when
    /// they are among the programs the user accepted (d3d11-plan.md M3 part C:
    /// D3d11Programs.AcceptedUnreachable); a stage entry no program made is a failure unless
    /// it belongs to such a program.
    internal sealed unsafe partial class D3d11Device
    {
        /// --check-shaders: WarmupShaders builds every subprogram (else programs are built at
        /// their first draw; the warm-up's prebuilding is M6's).
        public static bool CheckPrograms;

        /// Programs that failed, were refused (but for the unreachable ones), subprograms no
        /// keyword set selects, input layouts that could not be made, and debug-layer messages
        /// (--check-shaders).
        public static int CheckFailures => D3d11Programs.Failures + D3d11Programs.Refused - D3d11Programs.RefusedUnreachable + checkUnselected + checkLayoutFailures + checkDebugMessages + checkNotCreated;

        /// checkNotCreated: stage entries no built program holds and no accepted, unreachable
        /// program explains (each a failure).
        static int checkUnselected, checkLayoutFailures, checkDebugMessages, checkNotCreated;

        /// Every vertex channel present: positions and normals as 3 floats, tangents 4, colours
        /// as bytes, UVs as 2 floats. With no channel (every input a constant) and the immediate
        /// layout (GL.Begin), the three kinds of input layout a draw makes.
        static MeshLayout FullLayout()
        {
            var layout = new MeshLayout { ByteColors = true };
            layout.Components[(int)Channel.Position] = 3;
            layout.Components[(int)Channel.Normal] = 3;
            layout.Components[(int)Channel.Tangent] = 4;
            layout.Components[(int)Channel.Color] = 4;
            for (int c = (int)Channel.Uv0; c <= (int)Channel.Uv7; c++) layout.Components[c] = 2;
            return layout;
        }

        public override void WarmupShaders()
        {
            if (!CheckPrograms) return;
            var work = new List<(ShaderNative Shader, int Key)>();
            int subprograms = 0, shaders = 0, passes = 0;
            var entries = new HashSet<(ShaderNative, int)>();
            foreach (var native in ObjectTable.Snapshot())
            {
                if (native is not ShaderNative shader) continue;
                if (!shader.HasProgramsFor(ShaderNative.D3D11Platform))
                {
                    EngineLog.Engine($"d3d11 check: '{shader.ShaderName}' has no Direct3D 11 programs");
                    continue;
                }
                shaders++;
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                    {
                        if (pass.Type != 0 || pass.D3d11.IsEmpty) continue;
                        passes++;
                        var keys = new HashSet<int>();
                        for (int s = 0; s < pass.D3d11.Stages.Length; s++)
                            foreach (var program in pass.D3d11.Stages[s])
                            {
                                subprograms++;
                                entries.Add((shader, program.BlobIndex));
                                var keywords = program.Keywords;
                                bool IsEnabled(string k) => Array.IndexOf(keywords, k) >= 0;
                                // The draw path's own selection (D3d11Variants.Select) for this
                                // subprogram's keywords must give this subprogram back.
                                var selected = pass.D3d11.Select(IsEnabled);
                                if (selected[s] is not { } chosen || chosen.BlobIndex != program.BlobIndex || chosen.ParameterBlobIndex != program.ParameterBlobIndex)
                                {
                                    checkUnselected++;
                                    EngineLog.Engine($"d3d11 check: '{shader.ShaderName}' {PlayerSubprograms.Stages[s]} #{program.BlobIndex} [{string.Join(" ", keywords)}] is not what its own keywords select");
                                    continue;
                                }
                                int key = D3d11Programs.Select(shader, pass, IsEnabled);
                                if (key < 0)
                                {
                                    checkUnselected++;
                                    EngineLog.Engine($"d3d11 check: '{shader.ShaderName}' {PlayerSubprograms.Stages[s]} #{program.BlobIndex} [{string.Join(" ", keywords)}] has no vertex or pixel program to go with");
                                    continue;
                                }
                                if (keys.Add(key)) work.Add((shader, key));
                            }
                    }
            }
            int programs = 0, layouts = 0;
            var created = new HashSet<(ShaderNative, int)>();
            var unreachableEntries = new HashSet<(ShaderNative, int)>();
            var geometry = new HashSet<string>();
            worker.Call(() =>
            {
                var full = FullLayout();
                long fullKey = Vulkan.VkPipelines.LayoutKey(full), noneKey = Vulkan.VkPipelines.LayoutKey(new MeshLayout());
                foreach (var (shader, key) in work)
                {
                    D3d11Programs.LastRefusal = null;
                    var p = D3d11Programs.Get(Dev, shader, key);
                    DrainDebugMessages($"building '{shader.ShaderName}' program {key}");
                    if (D3d11Programs.LastRefusal is { } why)
                    {
                        bool accepted = D3d11Programs.LastRefusalAccepted;
                        EngineLog.Engine($"d3d11 check: refused{(accepted ? ", unreachable" : "")}: {D3d11Programs.CoverageOfKey(shader, key)}, program {key} ({why})");
                        if (accepted)
                            foreach (int blob in D3d11Programs.EntriesOfKey(shader, key)) unreachableEntries.Add((shader, blob));
                    }
                    if (p.D3d is not D3d11Shaders d3d) continue;
                    programs++;
                    foreach (var part in d3d.Coverage.Split(' '))
                    {
                        if (part.StartsWith("vs#") || part.StartsWith("ps#") || part.StartsWith("gs#")) created.Add((shader, int.Parse(part.AsSpan(3))));
                        if (part.StartsWith("gs#")) geometry.Add($"'{shader.ShaderName}' {part}");
                    }
                    foreach (var (layoutKey, layout) in new[] { (fullKey, full), (noneKey, new MeshLayout()), (Vulkan.VkPipelines.ImmediateLayout, (MeshLayout)null) })
                    {
                        if (Layout(d3d, layoutKey, layout) != null) layouts++;
                        else checkLayoutFailures++;
                        DrainDebugMessages($"input layout of '{shader.ShaderName}' program {key}");
                    }
                }
                DebugTally(always: true);
                checkDebugMessages = (int)debugMessages;
            });
            // The entries no built program holds, by shader (each refusal is logged above): those
            // of accepted, unreachable programs apart; any other is a failure.
            var missing = new SortedDictionary<string, (int Accepted, int Other)>(StringComparer.Ordinal);
            foreach (var entry in entries)
                if (!created.Contains(entry))
                {
                    missing.TryGetValue(entry.Item1.ShaderName, out var n);
                    bool accepted = unreachableEntries.Contains(entry);
                    missing[entry.Item1.ShaderName] = accepted ? (n.Accepted + 1, n.Other) : (n.Accepted, n.Other + 1);
                    if (!accepted) checkNotCreated++;
                }
            foreach (var (name, n) in missing)
                EngineLog.Engine($"d3d11 check: '{name}': {n.Accepted + n.Other} entries not created ({n.Accepted} only in refused, unreachable programs{(n.Other > 0 ? $"; {n.Other} otherwise: failures" : "")})");
            foreach (var g in geometry) EngineLog.Engine($"d3d11 check: geometry stage created: {g}");
            EngineLog.Engine($"d3d11 check: {shaders} shaders, {passes} passes, {subprograms} subprograms ({entries.Count} distinct entries); "
                             + $"{work.Count} programs to build, {programs} built, {created.Count} distinct stage entries created; "
                             + $"{layouts} input layouts made, {checkLayoutFailures} not; {D3d11Programs.Failures} failed, {D3d11Programs.Refused - D3d11Programs.RefusedUnreachable} refused, "
                             + $"{D3d11Programs.RefusedUnreachable} refused, unreachable; {checkUnselected} subprograms not selected by their own keywords; {checkNotCreated} entries not created otherwise; {debugMessages} debug-layer messages ({infoMessages} INFO or MESSAGE apart)");
            Console.WriteLine($"d3d11 check: {subprograms} subprograms, {created.Count} distinct stage entries created in {programs} programs, {layouts} input layouts; "
                              + $"{D3d11Programs.Failures} failed, {D3d11Programs.Refused - D3d11Programs.RefusedUnreachable} refused, {D3d11Programs.RefusedUnreachable} refused, unreachable, {checkUnselected} unselected, {checkLayoutFailures} layouts not made, {checkNotCreated} entries not created otherwise, {debugMessages} debug-layer messages");
        }
    }
}
