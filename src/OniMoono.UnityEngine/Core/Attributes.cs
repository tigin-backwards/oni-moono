using System;
using System.Collections.Generic;

// Attribute classes. Their AttributeUsage comes from the generated headers; the state they carry
// is what Unity's declarations expose, because game reflection constructs and reads them.

namespace AOT
{
    public partial class MonoPInvokeCallbackAttribute
    {
        /// Marks a static method as a native callback for ahead-of-time compilers; CoreCLR does
        /// not need it, the attribute is metadata only.
        public MonoPInvokeCallbackAttribute(Type type) { }
    }
}

namespace UnityEngine.Scripting
{
    public partial class PreserveAttribute
    {
    }

    public partial class AlwaysLinkAssemblyAttribute
    {
    }
}

namespace UnityEngine.Serialization
{
    public partial class FormerlySerializedAsAttribute
    {
        private readonly string formerName;
        public FormerlySerializedAsAttribute(string oldName) { formerName = oldName; }
        public string oldName => formerName;
    }
}

namespace UnityEngine
{
    public partial class SerializeField
    {
    }

    public partial class HideInInspector
    {
    }

    public partial class DisallowMultipleComponent
    {
    }

    public partial class ExecuteInEditMode
    {
    }

    public partial class ExecuteAlways
    {
    }

    public partial class SelectionBaseAttribute
    {
    }

    public partial class ExcludeFromPresetAttribute
    {
    }

    public partial class RequireComponent
    {
        public Type m_Type0;
        public Type m_Type1;
        public Type m_Type2;

        public RequireComponent(Type requiredComponent) { m_Type0 = requiredComponent; }
        public RequireComponent(Type requiredComponent, Type requiredComponent2) { m_Type0 = requiredComponent; m_Type1 = requiredComponent2; }

        public RequireComponent(Type requiredComponent, Type requiredComponent2, Type requiredComponent3)
        {
            m_Type0 = requiredComponent; m_Type1 = requiredComponent2; m_Type2 = requiredComponent3;
        }
    }

    public partial class AddComponentMenu
    {
        private readonly string menuPath;
        private readonly int menuOrder;

        public AddComponentMenu(string menuName) { menuPath = menuName; menuOrder = 0; }
        public AddComponentMenu(string menuName, int order) { menuPath = menuName; menuOrder = order; }

        public string componentMenu => menuPath;
        public int componentOrder => menuOrder;
    }

    public partial class ContextMenu
    {
        public readonly string menuItem;
        public readonly bool validate;
        public readonly int priority;

        public ContextMenu(string itemName) : this(itemName, false) { }
        public ContextMenu(string itemName, bool isValidateFunction) : this(itemName, isValidateFunction, 1000000) { }

        public ContextMenu(string itemName, bool isValidateFunction, int priority)
        {
            menuItem = itemName;
            validate = isValidateFunction;
            this.priority = priority;
        }
    }

    public partial class CreateAssetMenuAttribute
    {
        public string menuName { get; set; }
        public string fileName { get; set; }
        public int order { get; set; }
    }

    public partial class HelpURLAttribute
    {
        private readonly string helpUrl;
        public HelpURLAttribute(string url) { helpUrl = url; }
        public string URL => helpUrl;
    }

    /// Read for a mod's MonoBehaviours, which no MonoScript describes (ScriptInfo.DefaultOrder):
    /// Unity's player orders them by it, as UnityProbe's execorder measured. Unsealed, as in
    /// Unity's CoreModule.
    public partial class DefaultExecutionOrder
    {
        private readonly int executionOrder;
        public DefaultExecutionOrder(int order) { executionOrder = order; }
        public int order => executionOrder;
    }

    partial class AssetFileNameExtensionAttribute
    {
        public AssetFileNameExtensionAttribute(string preferredExtension, params string[] otherExtensions)
        {
            this.preferredExtension = preferredExtension;
            this.otherExtensions = otherExtensions;
        }

        public string preferredExtension { get; }
        public IEnumerable<string> otherExtensions { get; }
    }

    public partial class PropertyAttribute
    {
        protected PropertyAttribute() : this(false) { }
        protected PropertyAttribute(bool applyToCollection) { this.applyToCollection = applyToCollection; }

        public int order { get; set; }
        public bool applyToCollection { get; }
    }

    public partial class HeaderAttribute
    {
        public readonly string header;
        public HeaderAttribute(string header) : base(true) { this.header = header; }
    }

    public partial class TooltipAttribute
    {
        public readonly string tooltip;
        public TooltipAttribute(string tooltip) : base(true) { this.tooltip = tooltip; }
    }

    public partial class SpaceAttribute
    {
        public readonly float height;
        public SpaceAttribute() : base(true) { height = 8f; }
        public SpaceAttribute(float height) : base(true) { this.height = height; }
    }

    public partial class RangeAttribute
    {
        public readonly float min;
        public readonly float max;
        public RangeAttribute(float min, float max) { this.min = min; this.max = max; }
    }

    public partial class MultilineAttribute
    {
        public readonly int lines;
        public MultilineAttribute() { lines = 3; }
        public MultilineAttribute(int lines) { this.lines = lines; }
    }

    public partial class TextAreaAttribute
    {
        public readonly int minLines;
        public readonly int maxLines;
        public TextAreaAttribute() { minLines = 3; maxLines = 3; }
        public TextAreaAttribute(int minLines, int maxLines) { this.minLines = minLines; this.maxLines = maxLines; }
    }

    public partial class RuntimeInitializeOnLoadMethodAttribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { loadType = RuntimeInitializeLoadType.AfterSceneLoad; }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { this.loadType = loadType; }
        public RuntimeInitializeLoadType loadType { get; private set; }
    }
}
