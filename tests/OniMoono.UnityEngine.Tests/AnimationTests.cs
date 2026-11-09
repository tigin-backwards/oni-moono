using System;
using System.IO;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Mecanim;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class AnimatedScript : MonoBehaviour
    {
        [System.Serializable]
        public struct Nested
        {
            public int count;
        }

        [SerializeField] Color m_tint = Color.white;
        public Nested nested;

        public Color Tint => m_tint;
    }

    public class AnimationTests : EngineTest
    {
        /// The reference game install (ONI_GAME_DIR, default ~/projects/oni-linux).
        static string DataDir => GameInstall.DataDir;

        static T Load<T>(string file, long pathId) where T : Object
        {
            AssetSystem.Initialize(DataDir);
            return (T)ObjectLoader.Load(AssetSystem.Get(file), pathId);
        }

        [Fact]
        public void StringToHashIsUnitysCrc32()
        {
            // Values from the game's AnimatorController name tables (m_TOS).
            Assert.Equal(756556552, Animator.StringToHash("Base Layer"));
            Assert.Equal(533282942, Animator.StringToHash("Base Layer.New State"));
            Assert.Equal(758104917, Animator.StringToHash("Toggled"));
            Assert.Equal(unchecked((int)3154465721u), Animator.StringToHash("Press"));
            Assert.Equal(1583203699, Animator.StringToHash("NotificationPing"));
            Assert.Equal(0, Animator.StringToHash(""));
        }

        [Fact]
        public void EveryAnimationAssetInTheGameLoads()
        {
            AssetSystem.Initialize(DataDir);
            int clips = 0, controllers = 0, avatars = 0;
            foreach (var name in new[] { "level0", "level1", "level2", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets" })
            {
                var file = AssetSystem.Get(name);
                foreach (var info in file.Objects)
                {
                    int classId = file.TypeOf(info).ClassId;
                    if (classId is not (74 or 90 or 91 or 319)) continue;
                    var obj = ObjectLoader.Load(file, info.PathId);
                    switch (obj)
                    {
                        case AnimationClip clip:
                            clips++;
                            Assert.True(clip.length >= 0f);
                            break;
                        case RuntimeAnimatorController controller:
                            controllers++;
                            Assert.All(controller.animationClips, c => Assert.NotNull(c));
                            break;
                        case Avatar:
                            avatars++;
                            break;
                    }
                }
            }
            Assert.Equal(14, clips);
            Assert.Equal(10, controllers);
            Assert.Equal(1, avatars);
        }

        [Fact]
        public void StreamedClipDecodesAndSamples()
        {
            // BlurbAnimation: m_SizeDelta.y rises 0 → 420 over 1/6 s, holds, and falls back by 3.1667 s.
            var clip = Load<AnimationClip>("sharedassets0.assets", 6506);
            Assert.Equal("BlurbAnimation", clip.name);
            Assert.Equal(3.1666667f, clip.length, 5);
            Assert.Equal(60f, clip.frameRate);
            Assert.False(clip.legacy);
            Assert.False(clip.empty);
            Assert.False(clip.isLooping);
            Assert.False(clip.hasRootCurves);

            var native = clip.Clip;
            Assert.Equal(3, native.Curves.CurveCount);
            var values = new float[3];
            native.Curves.Sample(0f, values);
            Assert.Equal(0f, values[0], 3);
            native.Curves.Sample(1f / 6f, values);
            Assert.Equal(420f, values[0], 2);
            native.Curves.Sample(1.5f, values);
            Assert.Equal(420f, values[0], 2);
            native.Curves.Sample(3.1666667f, values);
            Assert.Equal(0f, values[0], 2);
            Assert.Equal(0f, values[1]);
        }

        [Fact]
        public void LegacyClipLengthComesFromItsCurves()
        {
            var clip = Load<AnimationClip>("sharedassets0.assets", 6504);
            Assert.Equal("thought_bubble", clip.name);
            Assert.True(clip.legacy);
            Assert.False(clip.empty);
            Assert.True(clip.length >= 1.0833f);
        }

        [Fact]
        public void NewClipIsEmpty()
        {
            var clip = new AnimationClip();
            Assert.True(clip.empty);
            Assert.Equal(0f, clip.length);
            Assert.Equal(60f, clip.frameRate);
            clip.frameRate = 30f;
            clip.legacy = true;
            Assert.Equal(30f, clip.frameRate);
            Assert.True(clip.legacy);
        }

        [Fact]
        public void ControllerLoadsStatesParametersAndClips()
        {
            var controller = Load<RuntimeAnimatorController>("sharedassets2.assets", 92);
            Assert.Equal("AnimatedToggleButton", controller.name);
            Assert.Equal(3, controller.animationClips.Length);
            var data = ((AnimatorControllerNative)controller.Native).Data;
            Assert.Single(data.Layers);
            Assert.Equal(4, data.StateMachines[0].States.Length);
            Assert.Equal(ParameterType.Bool, data.Parameters[data.ParameterIndex((uint)Animator.StringToHash("Toggled"))].Type);
        }

        /// The toggle prefabs: BG and FG children whose anchored positions the clips move.
        (GameObject root, RectTransform bg, RectTransform fg, Animator animator) ToggleButton()
        {
            var root = new GameObject("Toggle", typeof(RectTransform));
            var bg = new GameObject("BG", typeof(RectTransform));
            var fg = new GameObject("FG", typeof(RectTransform));
            bg.transform.SetParent(root.transform, false);
            fg.transform.SetParent(root.transform, false);
            var animator = root.AddComponent<Animator>();
            return (root, (RectTransform)bg.transform, (RectTransform)fg.transform, animator);
        }

        [Fact]
        public void AnimatorRunsTheStateMachineAndAppliesCurves()
        {
            var controller = Load<RuntimeAnimatorController>("sharedassets2.assets", 92);
            var (_, bg, fg, animator) = ToggleButton();
            Assert.False(animator.isInitialized);
            animator.runtimeAnimatorController = controller;
            Assert.True(animator.isInitialized);
            Assert.True(animator.hasBoundPlayables);
            Assert.True(animator.GetCurrentAnimatorStateInfo(0).IsName("idle"));
            Assert.True(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.idle"));

            Frame();
            Assert.Equal(Vector2.zero, bg.anchoredPosition);

            // idle → Pressed (Toggled, no exit time, zero duration); Pressed lasts 1/12 s at speed 4.
            animator.SetBool("Toggled", true);
            Frame();
            Assert.True(animator.GetCurrentAnimatorStateInfo(0).IsName("Pressed"));
            for (int i = 0; i < 5; i++) Frame();
            Assert.Equal(new Vector2(7f, -4f), bg.anchoredPosition);
            Assert.Equal(new Vector2(7f, -4f), fg.anchoredPosition);
            Assert.True(animator.GetBool("Toggled"));

            // Pressed → Released (!Toggled); Released returns to rest over 5/12 s at speed 5.
            animator.SetBool("Toggled", false);
            Frame();
            Assert.True(animator.GetCurrentAnimatorStateInfo(0).IsName("Released"));
            for (int i = 0; i < 10; i++) Frame();
            Assert.Equal(0f, bg.anchoredPosition.x, 3);
            Assert.Equal(0f, bg.anchoredPosition.y, 3);
        }

        [Fact]
        public void PlayAppliesOnTheNextUpdateAndExitTimeTransitionsBack()
        {
            var controller = Load<RuntimeAnimatorController>("sharedassets2.assets", 92);
            var (_, _, _, animator) = ToggleButton();
            animator.runtimeAnimatorController = controller;
            Frame();

            animator.Play("NotificationPing");
            Assert.False(animator.GetCurrentAnimatorStateInfo(0).IsTag("NotificationPing"));
            Frame();
            var info = animator.GetCurrentAnimatorStateInfo(0);
            Assert.True(info.IsTag("NotificationPing"));
            Assert.Equal(Animator.StringToHash("Base Layer.NotificationPing"), info.fullPathHash);
            Assert.Equal(Animator.StringToHash("NotificationPing"), info.shortNameHash);
            Assert.False(info.loop);

            // NotificationPing (2/3 s) → idle at exit time 0.9 with a 0.1 s fade.
            for (int i = 0; i < 45 && !animator.IsInTransition(0); i++) Frame();
            Assert.True(animator.IsInTransition(0));
            Assert.True(animator.GetNextAnimatorStateInfo(0).IsName("idle"));
            for (int i = 0; i < 10; i++) Frame();
            Assert.False(animator.IsInTransition(0));
            Assert.True(animator.GetCurrentAnimatorStateInfo(0).IsName("idle"));
        }

        [Fact]
        public void TriggersAreConsumedByTheTransitionTheyFire()
        {
            var controller = Load<RuntimeAnimatorController>("sharedassets2.assets", 91); // AnimatedButton
            var (_, _, _, animator) = ToggleButton();
            animator.runtimeAnimatorController = controller;
            animator.SetTrigger("Press");
            Assert.True(animator.GetBool("Press"));
            // New State (empty, 1 s) → Pressed needs Press and exit time 0.9.
            for (int i = 0; i < 70 && animator.GetBool("Press"); i++) Frame();
            Assert.False(animator.GetBool("Press"));
            Assert.True(animator.IsInTransition(0) || animator.GetCurrentAnimatorStateInfo(0).IsName("Pressed") || animator.GetCurrentAnimatorStateInfo(0).IsName("Released"));
        }

        [Fact]
        public void DisablingReleasesTheControllerAndResetsParameters()
        {
            var controller = Load<RuntimeAnimatorController>("sharedassets2.assets", 92);
            var (root, _, _, animator) = ToggleButton();
            animator.runtimeAnimatorController = controller;
            animator.SetBool("Toggled", true);
            root.SetActive(false);
            Assert.False(animator.isInitialized);
            root.SetActive(true);
            Assert.True(animator.isInitialized);
            Assert.False(animator.GetBool("Toggled"));

            animator.keepAnimatorStateOnDisable = true;
            animator.SetBool("Toggled", true);
            animator.enabled = false;
            Assert.True(animator.isInitialized);
            animator.enabled = true;
            Assert.True(animator.GetBool("Toggled"));
        }

        [Fact]
        public void ScriptFieldBindingsWriteSerializedFieldsAndNotify()
        {
            var root = new GameObject("root");
            var child = new GameObject("Label");
            child.transform.SetParent(root.transform, false);
            var script = child.AddComponent<AnimatedScript>();
            var binder = new AnimationBinder(root.transform);
            var alpha = new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Label"), ClassId = 114, Attribute = OniMoono.Engine.Crc32.Of("m_tint.a"), Script = typeof(AnimatedScript) };
            var red = new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Label"), ClassId = 114, Attribute = OniMoono.Engine.Crc32.Of("m_tint.r"), Script = typeof(AnimatedScript) };
            var count = new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Label"), ClassId = 114, Attribute = OniMoono.Engine.Crc32.Of("nested.count"), Script = typeof(AnimatedScript) };
            var target = binder.Resolve(alpha);
            Assert.Same(target, binder.Resolve(red));
            Assert.Equal(3, AnimationBinder.ComponentOf(alpha, 0));
            Assert.Equal(1f, target.Read(3));
            target.Stage(3, 0.25f);
            target.Stage(0, 0.5f);
            target.Commit();
            Assert.Equal(new Color(0.5f, 1f, 1f, 0.25f), script.Tint);

            var countTarget = binder.Resolve(count);
            countTarget.Stage(0, 2.6f);
            countTarget.Commit();
            Assert.Equal(3, script.nested.count);
            Assert.Null(binder.Resolve(new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Label"), ClassId = 114, Attribute = OniMoono.Engine.Crc32.Of("missing"), Script = typeof(AnimatedScript) }));
            Assert.Null(binder.Resolve(new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Nope"), ClassId = 1, Attribute = OniMoono.Engine.Crc32.Of("m_IsActive") }));

            var active = binder.Resolve(new ClipBinding { Path = OniMoono.Engine.Crc32.Of("Label"), ClassId = 1, Attribute = OniMoono.Engine.Crc32.Of("m_IsActive") });
            active.Stage(0, 0f);
            active.Commit();
            Assert.False(child.activeSelf);
        }

        [Fact]
        public void AvatarMaskDefaultsAndPaths()
        {
            var mask = new AvatarMask();
            Assert.True(mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Head));
            Assert.Equal(0, mask.transformCount);
            mask.transformCount = 2;
            mask.SetTransformPath(1, "a/b");
            mask.SetTransformActive(1, true);
            Assert.Equal("a/b", mask.GetTransformPath(1));
            Assert.True(mask.GetTransformActive(1));
            Assert.False(mask.GetTransformActive(0));
        }

        [Fact]
        public void GenericAvatarLoadsItsHumanDescription()
        {
            var avatar = Load<Avatar>("sharedassets2.assets", 90);
            Assert.True(avatar.isValid);
            Assert.False(avatar.isHuman);
            var d = avatar.humanDescription;
            Assert.Empty(d.human);
            Assert.Equal(0.5f, d.upperArmTwist);
            Assert.Equal("", d.m_RootMotionBoneName);
        }

        /// The game's thought-bubble pop-in (sharedassets0 #6504): a legacy clip scaling "bg" and
        /// "positioner" over 1.083 s.
        static (GameObject Root, Transform Bg, Transform Positioner, Animation Animation) ThoughtBubble()
        {
            var clip = Load<AnimationClip>("sharedassets0.assets", 6504);
            var root = new GameObject("Thought Bubble");
            var bg = new GameObject("bg").transform;
            bg.SetParent(root.transform, false);
            var positioner = new GameObject("positioner").transform;
            positioner.SetParent(root.transform, false);
            root.SetActive(false);
            var animation = root.AddComponent<Animation>();
            animation.clip = clip;
            animation.AddClip(clip, clip.name);
            return (root, bg, positioner, animation);
        }

        [Fact]
        public void LegacyClipLoadsItsEditorCurves()
        {
            var clip = Load<AnimationClip>("sharedassets0.assets", 6504);
            Assert.True(clip.legacy);
            Assert.Equal("thought_bubble", clip.name);
            Assert.Equal(1.0833334f, clip.length, 5);
            Assert.False(clip.empty);
        }

        [Fact]
        public void LegacyAnimationPlaysAutomaticallyOnEachActivation()
        {
            var (root, bg, positioner, animation) = ThoughtBubble();
            Assert.True(animation.playAutomatically);
            root.SetActive(true);
            Assert.True(animation.isPlaying);
            Frame(0f);
            // Keys: positioner (0,0) until 0.25 s, then (0.5, 0.5) at 0.5 s.
            Assert.Equal(Vector3.forward, positioner.localScale);
            for (int i = 0; i < 30; i++) Frame(1f / 60f);
            Assert.Equal(0.5f, positioner.localScale.x, 4);
            Assert.True(animation.IsPlaying("thought_bubble"));

            // Past the end (Default wrap → Once): the last frame stays, the state stops and rewinds.
            for (int i = 0; i < 40; i++) Frame(1f / 60f);
            Assert.False(animation.isPlaying);
            Assert.Equal(0f, animation["thought_bubble"].time);
            Assert.Equal(0.6287977f, bg.localScale.x, 5);
            Assert.Equal(0.65016603f, bg.localScale.y, 5);
            Assert.Equal(0.5f, positioner.localScale.x, 5);

            // Activating again plays from the start.
            root.SetActive(false);
            root.SetActive(true);
            Frame(1f / 60f);
            Assert.True(animation.isPlaying);
            Assert.Equal(0f, bg.localScale.x);
        }

        [Fact]
        public void LegacyAnimationWrapModesAndManualControl()
        {
            var (root, _, positioner, animation) = ThoughtBubble();
            animation.playAutomatically = false;
            root.SetActive(true);
            Assert.False(animation.isPlaying);

            var state = animation["thought_bubble"];
            Assert.Same(animation.clip, animation.GetClip("thought_bubble"));
            Assert.Equal(1, animation.GetClipCount());
            state.wrapMode = WrapMode.Loop;
            Assert.True(animation.Play());
            for (int i = 0; i < 95; i++) Frame(1f / 60f); // 1.5833 s → 0.5 s into the second loop
            Assert.True(animation.isPlaying);
            Assert.Equal(0.5f, positioner.localScale.x, 3);

            state.wrapMode = WrapMode.ClampForever;
            state.time = 5f;
            Frame(0f);
            Assert.True(animation.isPlaying);
            Assert.Equal(0.5f, positioner.localScale.x, 5);

            // Sample writes the current time without advancing it.
            animation.Stop();
            Assert.False(animation.isPlaying);
            state.enabled = true;
            state.time = 0.1f;
            animation.Sample();
            Assert.Equal(0f, positioner.localScale.x);
            Assert.Equal(0.1f, state.time);
        }
    }
}