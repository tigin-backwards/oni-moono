using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Serialization;

namespace UnityEngine
{
    public partial class CanvasGroup
    {
        public CanvasGroup() { }

        internal CanvasGroupNative Group => (CanvasGroupNative)EngineStateOrThrow;

        public float alpha
        {
            get => Group.Alpha;
            set
            {
                var g = Group;
                if (g.Alpha == value) return;
                g.Alpha = value;
                Hierarchy.Touch(g.GameObject?.Transform);
                g.Changed();
            }
        }

        public bool interactable
        {
            get => Group.Interactable;
            set
            {
                var g = Group;
                if (g.Interactable == value) return;
                g.Interactable = value;
                g.Changed();
            }
        }

        public bool blocksRaycasts
        {
            get => Group.BlocksRaycasts;
            set
            {
                var g = Group;
                if (g.BlocksRaycasts == value) return;
                g.BlocksRaycasts = value;
                Hierarchy.Touch(g.GameObject?.Transform); // the canvas walk skips only subtrees that stop raycasts
                g.Changed();
            }
        }

        public bool ignoreParentGroups
        {
            get => Group.IgnoreParentGroups;
            set
            {
                var g = Group;
                if (g.IgnoreParentGroups == value) return;
                g.IgnoreParentGroups = value;
                Hierarchy.Touch(g.GameObject?.Transform);
                g.Changed();
            }
        }

        /// A group lets raycasts through to its graphics only when it blocks raycasts.
        public bool IsRaycastLocationValid(Vector2 sp, Camera eventCamera) => blocksRaycasts;

        internal override void EngineOnEnable() => Group.Changed();
        internal override void EngineOnDisable() => Group.Changed();
    }
}

namespace OniMoono.Engine
{
    internal sealed class CanvasGroupNative : BehaviourNative
    {
        public float Alpha = 1f;
        public bool Interactable = true;
        public bool BlocksRaycasts = true;
        public bool IgnoreParentGroups;

        /// A saved group's settings (a prefab's: the game's port icons are a group that neither
        /// blocks raycasts nor is interactable, so a build drag can start over them).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Alpha = f.Float("m_Alpha");
            Interactable = f.Bool("m_Interactable");
            BlocksRaycasts = f.Bool("m_BlocksRaycasts");
            IgnoreParentGroups = f.Bool("m_IgnoreParentGroups");
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var g = (CanvasGroupNative)source;
            Alpha = g.Alpha;
            Interactable = g.Interactable;
            BlocksRaycasts = g.BlocksRaycasts;
            IgnoreParentGroups = g.IgnoreParentGroups;
        }

        /// Unity tells the scripts on this object and its descendants (uGUI's Selectables and
        /// Graphics listen) that the effective group state changed.
        public void Changed() => Messages.SendToSubtree(GameObject, s => s.OnCanvasGroupChanged);
    }
}
