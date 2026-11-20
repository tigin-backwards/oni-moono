using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using UnityEngine;

namespace OniMoono.Engine.Assets
{
    /// The project's settings managers from globalgamemanagers (and the MonoScript table in
    /// globalgamemanagers.assets), installed into the engine before any game code runs.
    internal static class ProjectSettings
    {
        public sealed record ResourceEntry(string Path, PPtr Target);
        public sealed record InputAxis(string Name, string NegativeButton, string PositiveButton, string AltNegativeButton, string AltPositiveButton,
            float Gravity, float Dead, float Sensitivity, bool Snap, bool Invert, int Type, int Axis, int JoyNum);

        public static SerializedFile Managers { get; private set; }
        public static IReadOnlyList<string> BuildScenes { get; private set; } = Array.Empty<string>();
        public static IReadOnlyList<ResourceEntry> ResourceContainer { get; private set; } = Array.Empty<ResourceEntry>();
        public static IReadOnlyList<InputAxis> InputAxes { get; private set; } = Array.Empty<InputAxis>();
        /// The Input Manager's "Use Physical Keys" (m_UsePhysicalKeys): KeyCodes name key positions
        /// (a US layout's) when set, the active layout's keys when not. This game's builds (Linux
        /// and Windows, U57) have it off. True until the settings are loaded.
        public static bool UsePhysicalKeys { get; private set; } = true;
        public static Fields PlayerSettings { get; private set; }
        public static Fields QualitySettings { get; private set; }
        public static Fields GraphicsSettings { get; private set; }
        /// ShaderNameRegistry: ShaderLab name → shader object (Shader.Find).
        public static IReadOnlyDictionary<string, PPtr> ShaderNames { get; private set; } = new Dictionary<string, PPtr>();

        public static void Load()
        {
            Managers = AssetSystem.Get("globalgamemanagers");
            EngineConfig.UnityVersion = Managers.UnityVersion;

            var tags = Read("TagManager");
            Layers.Install(tags.Array("layers").Cast<string>().ToArray());
            Tags.InstallList(tags.Array("tags").Cast<string>().ToArray());
            Rendering.SortingLayers.Install(tags.Array("m_SortingLayers").Cast<Fields>().Select(l => (l.String("name"), (int)l.Get<uint>("uniqueID"))));

            var time = Read("TimeManager");
            var fixedStep = time.Struct("Fixed Timestep");
            var rate = fixedStep.Struct("m_Rate");
            float fixedSeconds = (float)((double)fixedStep.Get<long>("m_Count") * rate.Get<uint>("m_Denominator") / rate.Get<uint>("m_Numerator"));
            Time.Configure(fixedSeconds, time.Float("Maximum Allowed Timestep"), time.Float("m_TimeScale"), time.Float("Maximum Particle Timestep"));

            BuildScenes = Read("BuildSettings").Array("scenes").Cast<string>().ToArray();

            ResourceContainer = Read("ResourceManager").Array("m_Container").Cast<Fields>()
                .Select(p => new ResourceEntry(p.String("first"), p.PPtr("second"))).ToArray();

            var inputManager = Read("InputManager");
            UsePhysicalKeys = inputManager.Bool("m_UsePhysicalKeys");
            InputAxes = inputManager.Array("m_Axes").Cast<Fields>().Select(a => new InputAxis(
                a.String("m_Name"), a.String("negativeButton"), a.String("positiveButton"), a.String("altNegativeButton"), a.String("altPositiveButton"),
                a.Float("gravity"), a.Float("dead"), a.Float("sensitivity"), a.Bool("snap"), a.Bool("invert"), a.Int("type"), a.Int("axis"), a.Int("joyNum"))).ToArray();

            PlayerSettings = Read("PlayerSettings");
            EngineConfig.CompanyName = PlayerSettings.String("companyName");
            EngineConfig.ProductName = PlayerSettings.String("productName");
            EngineConfig.Version = PlayerSettings.String("bundleVersion");

            QualitySettings = Read("QualitySettings");
            var qualityLevels = QualitySettings.Array("m_QualitySettings");
            int current = QualitySettings.Int("m_CurrentQuality");
            if ((uint)current < (uint)qualityLevels.Length) Rendering.TextureQuality.Configure((Fields)qualityLevels[current]);
            GraphicsSettings = Read("GraphicsSettings");
            var shaderNames = new Dictionary<string, PPtr>(StringComparer.Ordinal);
            foreach (Fields pair in Read("ShaderNameRegistry").Struct("m_Shaders").Array("m_ObjectToName"))
                shaderNames[pair.String("second")] = pair.PPtr("first");
            ShaderNames = shaderNames;

            PhysicsWorld.Configure(Read("PhysicsManager"));

            LoadScriptExecutionOrders();
        }

        static Fields Read(string managerName)
        {
            int id = TypeTreeDatabase.GetByName(managerName).Id;
            var obj = Managers.Objects.FirstOrDefault(o => Managers.TypeOf(o).ClassId == id)
                      ?? throw new InvalidDataException("globalgamemanagers has no " + managerName);
            return TypeTreeReader.ReadNative(id, Managers.OpenObject(obj));
        }

        /// Script execution order comes from each MonoScript's m_ExecutionOrder.
        static void LoadScriptExecutionOrders()
        {
            var scripts = AssetSystem.Get("globalgamemanagers.assets");
            foreach (var obj in scripts.Objects)
            {
                if (scripts.TypeOf(obj).ClassId != 115) continue;
                var f = TypeTreeReader.ReadNative(115, scripts.OpenObject(obj));
                int order = f.Int("m_ExecutionOrder");
                if (order == 0) continue;
                var ns = f.String("m_Namespace");
                ScriptInfo.SetExecutionOrder(ns.Length > 0 ? ns + "." + f.String("m_ClassName") : f.String("m_ClassName"), order);
            }
        }
    }
}
