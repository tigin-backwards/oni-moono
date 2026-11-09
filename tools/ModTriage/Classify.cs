// From the facts to the classes and flags of docs/research/mod-play-testing.md §3.1. Every class
// and flag carries the reasons that set it, so a wrong one can be traced to its signal.
namespace ModTriage;

/// A patch target with the class its declaring type maps to. Class is what the patch counts as;
/// Mapped differs from it when a screen patch counts as P, because the mod has no UI of its own
/// and the screen is only where it hooks in. Helper patches (UtilLibs) count for nothing.
public sealed record ClassifiedPatch(string Type, string? Method, string Class, string? Mapped, string Source, string By, bool Helper);

public sealed class Triage
{
    public const string Order = "PBTSKOVMX";

    public SortedDictionary<string, SortedSet<string>> Classes { get; } = new(Comparer<string>.Create((a, b) => Order.IndexOf(a[0]).CompareTo(Order.IndexOf(b[0]))));
    public SortedDictionary<string, SortedSet<string>> Flags { get; } = new(StringComparer.Ordinal);
    public List<ClassifiedPatch> Patches { get; } = [];

    public string ClassString => Classes.Count == 0 ? "–" : string.Join(",", Classes.Keys);

    static void Add(SortedDictionary<string, SortedSet<string>> map, string key, string reason)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new(StringComparer.Ordinal);
        if (set.Count < 6) set.Add(reason);
    }

    public void Class(string c, string reason) => Add(Classes, c, reason);
    public void Flag(string f, string reason) => Add(Flags, f, reason);
}

public static class Classifier
{
    static readonly HashSet<string> MenuTypes = new(StringComparer.Ordinal)
    {
        "MinionSelectScreen", "ModsScreen", "MainMenu", "ColonyDestinationSelectScreen", "ModeSelectScreen",
        "ClusterCategorySelectionScreen", "NewGameSettingsPanel", "WorldGenScreen", "LoadScreen",
    };
    static readonly HashSet<string> VisualTypes = new(StringComparer.Ordinal)
    {
        "CameraController", "OverlayScreen", "OverlayModes", "SimDebugView", "PropertyTextures", "OverlayLegend",
    };
    static readonly HashSet<string> InputTypes = new(StringComparer.Ordinal)
    {
        "KInputController", "GameInputMapping", "KInputHandler", "KInputManager", "InputInit",
    };
    static readonly HashSet<string> LifecycleMethods = new(StringComparer.Ordinal)
    {
        "OnPrefabInit", "OnSpawn", "OnActivate", "OnDeactivate", "OnCleanUp", "OnCmpEnable", "OnCmpDisable", "OnForcedCleanUp",
    };
    static readonly HashSet<string> ToolTypes = new(StringComparer.Ordinal) { "ToolMenu", "PlayerController", "ToolParameterMenu" };

    /// The class a patch target's declaring type maps to (§3.2), by its outermost type's name, or
    /// for a type that derives from KScreen or SideScreenContent in the game, S.
    public static string OfTarget(string type, GameIndex game)
    {
        var outer = Names.OuterSimple(type);
        if (MenuTypes.Contains(outer) || outer.StartsWith("NewGameFlow", StringComparison.Ordinal)) return "M";
        if (VisualTypes.Contains(outer) || outer.EndsWith("Renderer", StringComparison.Ordinal)) return "V";
        if (InputTypes.Contains(outer)) return "K";
        if (ToolTypes.Contains(outer) || outer.EndsWith("Tool", StringComparison.Ordinal)) return "T";
        if (outer.EndsWith("Screen", StringComparison.Ordinal) || outer == "DetailsScreen") return "S";
        var outerFull = type.IndexOf('+') is var p and >= 0 ? type[..p] : type;
        var (bases, _) = game.Chain(outerFull);
        if (bases.Contains("KScreen") || bases.Contains("SideScreenContent")) return "S";
        return "P";
    }

    static bool InUi(string type) =>
        type.StartsWith("UnityEngine.UI.", StringComparison.Ordinal) || type.StartsWith("UnityEngine.EventSystems.", StringComparison.Ordinal)
        || type.StartsWith("TMPro.", StringComparison.Ordinal) || type == "LocText";

    static readonly HashSet<string> ImguiTypes = new(StringComparer.Ordinal)
    {
        "UnityEngine.GUI", "UnityEngine.GUILayout", "UnityEngine.GUIStyle", "UnityEngine.GUIContent", "UnityEngine.GUISkin",
        "UnityEngine.GUIUtility", "UnityEngine.GUILayoutOption", "UnityEngine.GUILayoutUtility",
    };

    static readonly HashSet<string> RenderTypes = new(StringComparer.Ordinal)
    {
        "UnityEngine.Shader", "UnityEngine.RenderTexture", "UnityEngine.Rendering.CommandBuffer", "UnityEngine.Graphics",
        "UnityEngine.GL", "UnityEngine.ComputeShader",
    };

    /// The reference groups of §3.2: namespace or type → the members own code uses.
    public static string? Group(string type) => type switch
    {
        _ when type.StartsWith("UnityEngine.UI.", StringComparison.Ordinal) || type.StartsWith("UnityEngine.EventSystems.", StringComparison.Ordinal) => "UnityEngine.UI",
        _ when type.StartsWith("TMPro.", StringComparison.Ordinal) => "TMPro",
        "LocText" => "LocText",
        _ when ImguiTypes.Contains(type) => "UnityEngine.GUI",
        _ when type.StartsWith("UnityEngine.AssetBundle", StringComparison.Ordinal) => "AssetBundle",
        "UnityEngine.Shader" => "Shader",
        "UnityEngine.Material" => "Material",
        "UnityEngine.Camera" => "Camera",
        _ when RenderTypes.Contains(type) || type.StartsWith("UnityEngine.Rendering.", StringComparison.Ordinal) => "Rendering",
        _ when type.StartsWith("OverlayModes", StringComparison.Ordinal) || type is "OverlayScreen" or "SimDebugView" or "CameraController" => "Overlay/Camera",
        _ => null,
    };

    public static Triage Classify(ModFolder mod, GameIndex game)
    {
        var t = new Triage();
        var own = mod.Assemblies.Where(a => a.WholeLibrary == Library.None).ToList();
        if (own.Count == 0)
        {
            var c = mod.Content;
            var what = c.Translations > 0 ? "translation" : c.Worldgen || c.Templates ? "worldgen or templates" : c.AnimFiles > 0 ? "animations" : c.Elements ? "elements" : "content";
            t.Class("X", mod.Content.Dlls.Count == 0 ? $"no DLL: {what} only" : "only library DLLs");
            return t;
        }

        // The mod's own code; UtilLibs' (the shared helper) only where noted.
        var code = own.Select(a => a.Mod).ToList();
        var refTypes = code.SelectMany(c => c.RefTypes).ToHashSet(StringComparer.Ordinal);
        var refMembers = code.SelectMany(c => c.RefMembers).ToHashSet(StringComparer.Ordinal);
        var plib = code.SelectMany(c => c.PLibRefs).ToHashSet(StringComparer.Ordinal);
        var attributes = code.SelectMany(c => c.Attributes).ToHashSet(StringComparer.Ordinal);
        var types = own.SelectMany(a => a.Types).Where(o => !o.Helper).ToList();
        bool onGui = code.Any(c => c.DefinesOnGUI);

        // Buildings and options belong to the mod wherever its DLL defines them; a helper
        // namespace that holds them is the mod's after all.
        var allTypes = own.SelectMany(a => a.Types).ToList();
        var allPlib = own.SelectMany(a => a.Mod.PLibRefs.Concat(a.Helper.PLibRefs)).ToHashSet(StringComparer.Ordinal);
        attributes.UnionWith(own.SelectMany(a => a.Helper.Attributes));

        bool Derives(OwnType o, string b) => o.Bases.Contains(b) || o.Interfaces.Contains(b);
        IEnumerable<string> Deriving(string b) => types.Where(o => Derives(o, b)).Select(o => o.Name);
        IEnumerable<string> DerivingAny(string b) => allTypes.Where(o => Derives(o, b)).Select(o => o.Name);
        bool PLibUses(string area, params string[] simple) => PLibIn(plib, area, simple);
        static bool PLibIn(HashSet<string> refs, string area, params string[] simple) => refs.Any(p => p.StartsWith(area + ":", StringComparison.Ordinal)
            && (simple.Length == 0 || simple.Any(s => p[(area.Length + 1)..].Split("::")[0] == s)));
        IEnumerable<string> PLibNames(string area) => allPlib.Where(p => p.StartsWith(area + ":", StringComparison.Ordinal))
            .Select(p => p[(area.Length + 1)..].Split("::")[0]).Distinct();

        // ---- flags first: touches_ui decides what a screen patch counts as
        bool bundles = mod.Content.Bundles.Count > 0;
        // Prefabs (GameObjects) loaded from a bundle, by the mod or its helper, and UI in the mod:
        // texture and shader bundles load Texture2D, Material or Shader instead.
        static bool LoadsPrefabs(CodeFacts c) => c.RefMembers.Any(m => m.StartsWith("UnityEngine.AssetBundle::Load", StringComparison.Ordinal)
            && m.EndsWith("<UnityEngine.GameObject>", StringComparison.Ordinal));
        bool loadsPrefabs = own.Any(a => LoadsPrefabs(a.Mod) || LoadsPrefabs(a.Helper));
        bool screens = Deriving("KScreen").Any() || Deriving("SideScreenContent").Any();
        if (bundles && loadsPrefabs && (screens || refTypes.Any(InUi)))
        {
            t.Flag("asset_bundle_ui", $"bundle {mod.Content.Bundles[0]}; prefabs loaded from it, and UI types used");
            t.Flag("touches_ui", "loads UI from an asset bundle");
        }
        foreach (var r in refTypes.Where(InUi).Order(StringComparer.Ordinal).Take(4)) t.Flag("touches_ui", "uses " + r);
        foreach (var n in Deriving("KScreen").Concat(Deriving("SideScreenContent")).Take(3)) t.Flag("touches_ui", "subclasses a screen: " + n);
        if (onGui) t.Flag("touches_ui", "defines OnGUI");
        if (PLibUses("UI")) t.Flag("touches_ui", "PLib UI: " + string.Join(", ", PLibNames("UI").Take(3)));
        if (PLibIn(allPlib, "Options")) t.Flag("plib_options", "PLib options: " + string.Join(", ", PLibNames("Options").Take(3)));
        if (refMembers.Contains("UserMenu::AddButton") || refTypes.Contains("KIconButtonMenu+ButtonInfo"))
            t.Flag("user_menu", "UserMenu.AddButton");
        bool touchesUi = t.Flags.ContainsKey("touches_ui");

        // ---- B
        foreach (var n in DerivingAny("IBuildingConfig")) t.Class("B", "IBuildingConfig: " + n);
        foreach (var n in DerivingAny("IMultiBuildingConfig")) t.Class("B", "IMultiBuildingConfig: " + n);
        if (PLibIn(allPlib, "Buildings", "PBuilding")) t.Class("B", "PLib PBuilding");

        // ---- T
        foreach (var n in Deriving("InterfaceTool")) t.Class("T", "InterfaceTool: " + n);

        // ---- S
        foreach (var n in Deriving("KScreen")) t.Class("S", "KScreen: " + n);
        foreach (var n in Deriving("SideScreenContent")) t.Class("S", "SideScreenContent: " + n);
        if (onGui) t.Class("S", "defines OnGUI");
        if (PLibUses("UI", "PDialog")) t.Class("S", "PLib PDialog");
        if (t.Flags.ContainsKey("asset_bundle_ui")) t.Class("S", "screens from an asset bundle");

        // ---- K
        if (PLibUses("Actions", "PAction", "PActionManager", "PKeyBinding")) t.Class("K", "PLib PAction: " + string.Join(", ", PLibNames("Actions").Take(3)));
        foreach (var m in refMembers.Where(m => m.StartsWith("UnityEngine.Input::GetKey", StringComparison.Ordinal))) t.Class("K", m.Replace("::", "."));
        foreach (var r in new[] { "BindingEntry", "KInputController" }.Where(refTypes.Contains)) t.Class("K", "uses " + r);

        // ---- O
        if (PLibIn(allPlib, "Options")) t.Class("O", "PLib options: " + string.Join(", ", PLibNames("Options").Take(3)));
        foreach (var a in attributes.Where(a => Names.Simple(a) is "OptionAttribute" or "ConfigFileAttribute" or "JsonOptionsAttribute")) t.Class("O", "[" + Names.Simple(a) + "]");

        // ---- V
        foreach (var n in Deriving("OverlayModes+Mode")) t.Class("V", "OverlayModes.Mode: " + n);
        foreach (var r in refTypes.Where(RenderTypes.Contains)) t.Class("V", "uses " + r);

        // ---- M
        foreach (var n in MenuTypes.SelectMany(Deriving)) t.Class("M", "derives a menu screen: " + n);

        // ---- patch targets
        foreach (var a in own)
            foreach (var p in a.Patches)
            {
                var mapped = OfTarget(p.Type, game);
                var counted = mapped;
                // A screen's key handler is input; a screen's lifecycle method, patched by a mod with
                // no UI of its own, is only a hook (Bigger Camera Zoom Out's
                // WattsonMessage.OnDeactivate), not a screen to verify.
                if (mapped == "S" && p.Method is "OnKeyDown" or "OnKeyUp") counted = "K";
                else if (mapped == "S" && !touchesUi && p.Method is { } m && LifecycleMethods.Contains(m)) counted = "P";
                t.Patches.Add(new ClassifiedPatch(p.Type, p.Method, counted, counted == mapped ? null : mapped, p.Source, p.By, p.Helper));
                if (!p.Helper && counted != "P") t.Class(counted, "patches " + p.Display);
                // Changing a game screen's behaviour or layout is UI to verify too.
                if (!p.Helper && counted == "S") t.Flag("touches_ui", "patches a game screen: " + p.Display);
            }

        // ---- P: own code, and nothing a player drives
        if (!t.Classes.Keys.Any(k => k is "B" or "T" or "S" or "K" or "V" or "M"))
        {
            int n = t.Patches.Count(p => !p.Helper);
            t.Class("P", n > 0 ? $"{n} patches on sim or game code" : "no UI, input or content surface found");
        }
        return t;
    }
}
