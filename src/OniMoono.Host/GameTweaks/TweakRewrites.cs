using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace OniMoono.Host.GameTweaks
{
    /// Which tweak's load-time IL rewrite changed a game method, for the Moono Tweaks screen's
    /// mod overlaps (OniMoono.GameTweaks.ModOverlaps): compact-nav-links' rewrite
    /// (NavGridLinksRewrite) makes a method call NavGridLinks' helpers, paged-path-grids'
    /// (PathGridPagesRewrite) PathGridPages'. A method's IL is read as loaded (the rewritten
    /// Assembly-CSharp, cached or not), so whatever the rewrite did is what is found: a call of a
    /// member reference (or an instantiation of one) whose type is one of the two.
    internal static class TweakRewrites
    {
        sealed record Tokens(Module Module, HashSet<int> Links, HashSet<int> Pages);

        static System.Threading.Tasks.Task<Tokens>? indexing;

        /// At start-up, once the game's assembly is loaded: its helper tokens are indexed on a
        /// worker, so the first main menu does not wait for it (about 14 ms on U60 761211).
        public static void Prepare()
        {
            if (!NavGridLinksRewrite.Enabled && !PathGridPagesRewrite.Enabled) return;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "Assembly-CSharp")
                {
                    var module = assembly.ManifestModule;
                    indexing = System.Threading.Tasks.Task.Run(() => Index(module));
                    return;
                }
        }

        /// The tweak whose rewrite changed `method`, or null. Methods of other assemblies, and
        /// those of a session with neither rewrite, are never rewritten.
        public static string? RewrittenBy(MethodBase method)
        {
            if (!NavGridLinksRewrite.Enabled && !PathGridPagesRewrite.Enabled) return null;
            var module = method.Module;
            if (module.Assembly.GetName().Name != "Assembly-CSharp") return null;
            if (indexing == null || indexing.Result.Module != module) indexing = System.Threading.Tasks.Task.FromResult(Index(module));
            var tokens = indexing.Result;
            if (tokens.Links.Count == 0 && tokens.Pages.Count == 0) return null;
            byte[]? il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or BadImageFormatException) { return null; }
            if (il == null) return null;
            // A call (0x28) of one of the tokens; an operand's bytes that only look like one would
            // have to spell a whole helper token too.
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                if (tokens.Links.Contains(token)) return CompactNavLinks.Name;
                if (tokens.Pages.Contains(token)) return PagedPathGrids.Name;
            }
            return null;
        }

        /// The tokens in the game's module that name the helpers: member references whose
        /// parent is NavGridLinks or PathGridPages (or a type nested in them), and the method
        /// instantiations of those.
        static unsafe Tokens Index(Module module)
        {
            var links = new HashSet<int>();
            var pages = new HashSet<int>();
            try
            {
                if (!module.Assembly.TryGetRawMetadata(out byte* blob, out int length)) return new Tokens(module, links, pages);
                var reader = new MetadataReader(blob, length);
                // The helper types' references first (a few among thousands), then their members.
                var types = new Dictionary<TypeReferenceHandle, HashSet<int>>();
                foreach (var handle in reader.TypeReferences)
                    if (SetOf(reader, handle, links, pages) is { } set) types[handle] = set;
                if (types.Count == 0) return new Tokens(module, links, pages);
                var byRef = new Dictionary<EntityHandle, HashSet<int>>();
                foreach (var handle in reader.MemberReferences)
                {
                    var parent = reader.GetMemberReference(handle).Parent;
                    if (parent.Kind != HandleKind.TypeReference || !types.TryGetValue((TypeReferenceHandle)parent, out var set)) continue;
                    set.Add(MetadataTokens.GetToken(handle));
                    byRef[handle] = set;
                }
                int specs = reader.GetTableRowCount(TableIndex.MethodSpec);
                for (int row = 1; row <= specs; row++)
                {
                    var handle = MetadataTokens.MethodSpecificationHandle(row);
                    if (byRef.TryGetValue(reader.GetMethodSpecification(handle).Method, out var set)) set.Add(MetadataTokens.GetToken(handle));
                }
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                links.Clear();
                pages.Clear();
            }
            return new Tokens(module, links, pages);
        }

        /// The tokens' set for a type reference to one of the helper types (or a type nested in
        /// one), or null.
        static HashSet<int>? SetOf(MetadataReader reader, TypeReferenceHandle handle, HashSet<int> links, HashSet<int> pages)
        {
            for (int depth = 0; depth < 4; depth++)
            {
                var type = reader.GetTypeReference(handle);
                if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                {
                    handle = (TypeReferenceHandle)type.ResolutionScope;
                    continue;
                }
                if (!reader.StringComparer.Equals(type.Namespace, "OniMoono.Host")) return null;
                if (reader.StringComparer.Equals(type.Name, nameof(NavGridLinks))) return links;
                if (reader.StringComparer.Equals(type.Name, nameof(PathGridPages))) return pages;
                return null;
            }
            return null;
        }
    }
}
