using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Engine.Assets;
using OniMoono.Engine.IMGUI;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// A behaviour whose OnGUI runs a test-supplied body.
    public class GUIProbe : MonoBehaviour
    {
        public static Action<GUIProbe> Body;
        public readonly List<EventType> Events = new();

        void OnGUI()
        {
            Events.Add(Event.current.type);
            Body?.Invoke(this);
        }
    }

    /// A graphics device that renders frames and records GL immediate draws.
    internal sealed class RecordingDevice : OniMoono.Engine.Rendering.RenderDevice
    {
        public readonly List<(Material Material, OniMoono.Engine.Rendering.ImmediateVertex[] Vertices, Matrix4x4 Projection)> Draws = new();
        Material pass;
        public override OniMoono.Engine.Rendering.GraphicsCaps Caps { get; } = new OniMoono.Engine.Rendering.NullCaps();
        public override bool RendersFrames => true;
        public override void ReadPixels(OniMoono.Engine.Rendering.RenderTextureNative source, RectInt rect, OniMoono.Engine.Rendering.Texture2DNative destination, int destX, int destY) { }
        /// Device resources released, in order.
        public readonly List<object> Released = new();
        public override void ReleaseResource(object gpu) => Released.Add(gpu);
        public override bool SetPass(Material material, int p)
        {
            pass = material;
            return true;
        }
        public override void WarmupShaders() { }
        public override void SetRenderTarget(in OniMoono.Engine.Rendering.RenderTargetBinding target) { }
        public override void SetViewport(Rect pixels) { }
        public override void SetScissor(Rect? pixels) { }
        public override void Clear(bool color, bool depth, Color backgroundColor, float depthValue) { }
        protected override void ApplyCamera(in OniMoono.Engine.Rendering.CameraConstants camera) { }
        /// Mesh draws with the view and projection in effect.
        public readonly List<(Material Material, Matrix4x4 View, Matrix4x4 Projection)> MeshDraws = new();
        /// The property sheet each mesh draw was given.
        public readonly List<OniMoono.Engine.Rendering.PropertySheet> MeshDrawProperties = new();
        /// Each mesh draw's request.
        public readonly List<OniMoono.Engine.Rendering.DrawRequest> MeshDrawRequests = new();
        public override void Draw(in OniMoono.Engine.Rendering.DrawRequest draw)
        {
            MeshDraws.Add((draw.Material, Camera.View, Camera.Projection));
            MeshDrawProperties.Add(draw.Properties);
            MeshDrawRequests.Add(draw);
        }
        public override void DrawImmediate(OniMoono.Engine.Rendering.ImmediatePrimitive primitive, ReadOnlySpan<OniMoono.Engine.Rendering.ImmediateVertex> vertices, Matrix4x4 modelView, Matrix4x4 projection)
            => Draws.Add((pass, vertices.ToArray(), projection));
        /// Graphics.DrawMeshNow draws: the pass's material, mesh, sub-mesh and matrices.
        public readonly List<(Material Material, OniMoono.Engine.Rendering.MeshNative Mesh, int SubMesh, Matrix4x4 ObjectToWorld, Matrix4x4 View, Matrix4x4 Projection)> MeshNowDraws = new();
        public override void DrawMeshNow(OniMoono.Engine.Rendering.MeshNative mesh, int subMesh, Matrix4x4 objectToWorld, Matrix4x4 view, Matrix4x4 projection)
            => MeshNowDraws.Add((pass, mesh, subMesh, objectToWorld, view, projection));
        public override void Present() { }
    }

    public class IMGUITests : EngineTest, IDisposable
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
                return Path.Combine(game, "OxygenNotIncluded_Data");
            }
        }

        readonly List<string> errors = new();

        void OnLog(string message, string stackTrace, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert) errors.Add(message);
        }

        void IDisposable.Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            base.Dispose();
            Assert.Empty(errors);
        }

        public IMGUITests()
        {
            Application.logMessageReceivedThreaded += OnLog;
            AssetSystem.Initialize(DataDir);
            GUIManager.Install();
            GUIManager.Reset();
            GUIStateData.Reset();
            GUIProbe.Body = null;
        }

        static GUIProbe MakeProbe()
        {
            var go = new GameObject("gui");
            return go.AddComponent<GUIProbe>();
        }

        static void Queue(EventType type, Vector2 position, int clickCount = 1)
        {
            var e = new Event { type = type, mousePosition = position, button = 0, clickCount = clickCount };
            Event.Enqueue(e);
        }

        static void QueueKey(KeyCode key, char character = '\0', EventModifiers modifiers = 0)
        {
            Event.Enqueue(new Event { type = EventType.KeyDown, keyCode = key, character = character, modifiers = modifiers });
        }

        [Fact]
        public void LayoutAndRepaintEveryFrameEachPrecededByLayout()
        {
            var probe = MakeProbe();
            Frame();
            Assert.Equal(new[] { EventType.Layout, EventType.Repaint }, probe.Events);
            probe.Events.Clear();
            Queue(EventType.MouseDown, new Vector2(5f, 5f));
            Frame();
            Assert.Equal(new[] { EventType.Layout, EventType.MouseDown, EventType.Layout, EventType.Repaint }, probe.Events);
        }

        [Fact]
        public void PopEventQueueIsIndependentOfOnGUI()
        {
            var probe = MakeProbe();
            while (Event.PopEvent(new Event())) { }
            Queue(EventType.MouseDown, new Vector2(5f, 5f));
            var popped = new Event();
            Assert.True(Event.PopEvent(popped));
            Assert.Equal(EventType.MouseDown, popped.type);
            Frame();
            Assert.Contains(EventType.MouseDown, probe.Events);
        }

        [Fact]
        public void UnpoppedEventsAreDroppedAtTheEndOfTheFrame()
        {
            while (Event.PopEvent(new Event())) { }
            Queue(EventType.MouseDown, new Vector2(5f, 5f));
            Assert.Equal(1, Event.GetEventCount());
            Frame();
            Assert.Equal(0, Event.GetEventCount());
            Assert.False(Event.PopEvent(new Event()));
        }

        [Fact]
        public void GUIFunctionsNeedOnGUI()
        {
            Assert.Throws<ArgumentException>(() => GUI.skin);
            Assert.Throws<ArgumentException>(() => GUIUtility.GetControlID(FocusType.Passive));
        }

        [Fact]
        public void NestedLayoutGroups()
        {
            var probe = MakeProbe();
            var rects = new Rect[4];
            Vector2 areaOrigin = default;
            GUIProbe.Body = _ =>
            {
                GUILayout.BeginArea(new Rect(10f, 20f, 300f, 200f));
                GUILayout.BeginHorizontal();
                rects[0] = GUILayoutUtility.GetRect(50f, 20f, GUILayout.Width(50f), GUILayout.Height(20f));
                rects[1] = GUILayoutUtility.GetRect(30f, 40f, GUILayout.Width(30f), GUILayout.Height(40f));
                GUILayout.EndHorizontal();
                GUILayout.BeginVertical();
                rects[2] = GUILayoutUtility.GetRect(100f, 10f, GUILayout.Width(100f), GUILayout.Height(10f));
                rects[3] = GUILayoutUtility.GetRect(100f, 10f, GUILayout.Width(100f), GUILayout.Height(10f));
                GUILayout.EndVertical();
                if (Event.current.type == EventType.Repaint) areaOrigin = GUIClip.Unclip(Vector2.zero);
                GUILayout.EndArea();
            };
            Frame();
            Assert.Equal(new Rect(0f, 0f, 50f, 20f), rects[0]);
            Assert.Equal(new Rect(50f, 0f, 30f, 40f), rects[1]);
            Assert.Equal(new Rect(0f, 40f, 100f, 10f), rects[2]);
            Assert.Equal(new Rect(0f, 50f, 100f, 10f), rects[3]);
            Assert.Equal(new Vector2(10f, 20f), areaOrigin);
        }

        [Fact]
        public void StretchingEntriesShareTheExtraSpace()
        {
            var probe = MakeProbe();
            Rect a = default, b = default;
            GUIProbe.Body = _ =>
            {
                GUILayout.BeginArea(new Rect(0f, 0f, 200f, 100f));
                GUILayout.BeginHorizontal();
                a = GUILayoutUtility.GetRect(20f, 10f);
                b = GUILayoutUtility.GetRect(40f, 10f);
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            };
            Frame();
            // GUIStyle.none stretches horizontally: (200 - 60) / 2 extra each.
            Assert.Equal(new Rect(0f, 0f, 90f, 10f), a);
            Assert.Equal(new Rect(90f, 0f, 110f, 10f), b);
        }

        [Fact]
        public void ControlIdsAreStablePerPositionAndHint()
        {
            var probe = MakeProbe();
            var ids = new List<int[]>();
            GUIProbe.Body = _ =>
            {
                int a = GUIUtility.GetControlID(1, FocusType.Passive);
                int b = GUIUtility.GetControlID(2, FocusType.Keyboard);
                int c = GUIUtility.GetControlID(1, FocusType.Passive, new Rect(0f, 0f, 1f, 1f));
                ids.Add(new[] { a, b, c });
            };
            Frame();
            Frame();
            Assert.Equal(4, ids.Count);
            Assert.Equal(3, new HashSet<int>(ids[0]).Count);
            foreach (var set in ids) Assert.Equal(ids[0], set);
            // A skipped control keeps the IDs of the controls after it (matched by hint).
            int[] skipped = null;
            GUIProbe.Body = _ =>
            {
                int b = GUIUtility.GetControlID(2, FocusType.Keyboard);
                int c = GUIUtility.GetControlID(1, FocusType.Passive);
                skipped = new[] { b, c };
            };
            Frame();
            Assert.Equal(ids[0][1], skipped[0]);
            Assert.Equal(ids[0][2], skipped[1]);
        }

        [Fact]
        public void ButtonClickSequence()
        {
            var probe = MakeProbe();
            var rect = new Rect(10f, 10f, 100f, 30f);
            var results = new List<(EventType, bool, int)>();
            GUIProbe.Body = _ =>
            {
                var type = Event.current.type;
                bool clicked = GUI.Button(rect, "Press");
                results.Add((type, clicked, GUIUtility.hotControl));
            };
            Queue(EventType.MouseDown, new Vector2(20f, 20f));
            Queue(EventType.MouseUp, new Vector2(25f, 22f));
            Frame();
            Assert.DoesNotContain(results, r => r.Item1 == EventType.MouseDown && r.Item2);
            var down = results.Find(r => r.Item1 == EventType.MouseDown);
            Assert.NotEqual(0, down.Item3);
            var up = results.Find(r => r.Item1 == EventType.MouseUp);
            Assert.True(up.Item2);
            Assert.Equal(0, up.Item3);
            Assert.Equal(0, GUIUtility.hotControl);

            // Releasing outside the button does not click it.
            results.Clear();
            Queue(EventType.MouseDown, new Vector2(20f, 20f));
            Queue(EventType.MouseUp, new Vector2(300f, 300f));
            Frame();
            Assert.DoesNotContain(results, r => r.Item2);
        }

        [Fact]
        public void InputStopsAtTheFrontBehaviourThatUsesIt()
        {
            var back = MakeProbe();
            var front = MakeProbe();
            var log = new List<string>();
            GUIProbe.Body = p =>
            {
                if (p == front) GUI.depth = -1;
                if (Event.current.type == EventType.MouseDown)
                {
                    log.Add(p == front ? "front" : "back");
                    if (p == front) Event.current.Use();
                }
            };
            Frame();
            Queue(EventType.MouseDown, new Vector2(1f, 1f));
            Frame();
            Assert.Equal(new[] { "front" }, log);
        }

        [Fact]
        public void GroupsOffsetAndClipMousePosition()
        {
            var probe = MakeProbe();
            Vector2 inGroup = default, nested = default;
            Rect visible = default;
            GUIProbe.Body = _ =>
            {
                if (Event.current.type != EventType.MouseDown) return;
                GUI.BeginGroup(new Rect(100f, 50f, 200f, 100f));
                inGroup = Event.current.mousePosition;
                GUI.BeginGroup(new Rect(150f, 80f, 200f, 100f));
                nested = Event.current.mousePosition;
                visible = GUIClip.visibleRect;
                GUI.EndGroup();
                GUI.EndGroup();
            };
            Queue(EventType.MouseDown, new Vector2(130f, 70f));
            Frame();
            Assert.Equal(new Vector2(30f, 20f), inGroup);
            Assert.Equal(new Vector2(-120f, -60f), nested);
            // The inner group is clipped by the outer one: 50 x 20 visible.
            Assert.Equal(new Rect(0f, 0f, 50f, 20f), visible);
        }

        [Fact]
        public void MatrixScalesAroundPivot()
        {
            var probe = MakeProbe();
            Vector2 local = default, screen = default;
            GUIProbe.Body = _ =>
            {
                if (Event.current.type != EventType.MouseDown) return;
                GUIUtility.ScaleAroundPivot(new Vector2(2f, 2f), new Vector2(100f, 100f));
                local = Event.current.mousePosition;
                screen = GUIUtility.GUIToScreenPoint(new Vector2(200f, 200f));
            };
            Queue(EventType.MouseDown, new Vector2(140f, 160f));
            Frame();
            Assert.Equal(new Vector2(120f, 130f), local);
            Assert.Equal(new Vector2(300f, 300f), screen);
        }

        [Fact]
        public void GUIClipReflectionMatchesUnity()
        {
            var type = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip", true);
            Assert.Null(type.GetProperty("topmostRect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public));
            Assert.NotNull(type.GetMethod("GetTopRect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            Assert.NotNull(type.GetMethod("Clip", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public, null, new[] { typeof(Rect) }, null));
        }

        [Fact]
        public void BuiltinSkinLoadsWithItsStyles()
        {
            var probe = MakeProbe();
            GUISkin skin = null;
            GUIProbe.Body = _ => skin = GUI.skin;
            Frame();
            Assert.NotNull(skin);
            Assert.Equal("GameSkin", skin.name);
            Assert.Same(skin.label, skin.GetStyle("Label"));
            Assert.NotNull(skin.button.normal.background);
            Assert.NotNull(skin.window.normal.background);
            Assert.True(skin.button.border.left > 0);
            Assert.True(skin.settings.doubleClickSelectsWord);
        }

        [Fact]
        public void CalcSizeWithTheLegacyFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var style = new GUIStyle { font = font, padding = new RectOffset(3, 5, 2, 4) };
            var empty = style.CalcSize(GUIContent.none);
            Assert.True(empty.y > 4f);
            Assert.Equal(8f, empty.x);
            var one = style.CalcSize(new GUIContent("Hello"));
            var two = style.CalcSize(new GUIContent("Hello Hello"));
            Assert.True(one.x > 8f);
            Assert.True(two.x > one.x * 1.5f);
            Assert.Equal(one.y, two.y);
            var lines = style.CalcSize(new GUIContent("Hello\nHello"));
            Assert.True(lines.y > one.y + 8f);
            style.fixedWidth = 77f;
            Assert.Equal(77f, style.CalcSize(new GUIContent("Hello")).x);
            style.fixedWidth = 0f;
            style.wordWrap = true;
            float wrapped = style.CalcHeight(new GUIContent("Hello Hello Hello Hello"), one.x + 2f);
            Assert.True(wrapped > one.y * 2f);
            style.CalcMinMaxWidth(new GUIContent("Hello Hello"), out float min, out float max);
            Assert.Equal(two.x, max);
            Assert.True(min < max);
        }

        [Fact]
        public void TextFieldEditing()
        {
            var probe = MakeProbe();
            string text = "abc";
            var rect = new Rect(10f, 10f, 200f, 24f);
            GUIProbe.Body = _ => text = GUI.TextField(rect, text);
            Frame();
            // Click to focus (the whole text is selected on focus), then edit.
            Queue(EventType.MouseDown, new Vector2(15f, 20f));
            Queue(EventType.MouseUp, new Vector2(15f, 20f));
            Frame();
            Assert.NotEqual(0, GUIUtility.keyboardControl);
            QueueKey(KeyCode.End, '\0', EventModifiers.FunctionKey);
            QueueKey(KeyCode.None, 'd');
            QueueKey(KeyCode.None, 'e');
            QueueKey(KeyCode.Backspace, '\0', EventModifiers.FunctionKey);
            QueueKey(KeyCode.Home, '\0', EventModifiers.FunctionKey);
            QueueKey(KeyCode.Delete, '\0', EventModifiers.FunctionKey);
            Frame();
            Assert.Equal("bcd", text);
            QueueKey(KeyCode.A, 'a', EventModifiers.Control);
            QueueKey(KeyCode.C, 'c', EventModifiers.Control);
            Frame();
            Assert.Equal("bcd", GUIUtility.systemCopyBuffer);
            QueueKey(KeyCode.None, 'x');
            Frame();
            Assert.Equal("x", text);
            QueueKey(KeyCode.V, 'v', EventModifiers.Control);
            Frame();
            Assert.Equal("xbcd", text);
            // A click no control takes ends editing.
            Queue(EventType.MouseDown, new Vector2(500f, 500f));
            Frame();
            Assert.Equal(0, GUIUtility.keyboardControl);
        }

        [Fact]
        public void DisabledControlsIgnoreInput()
        {
            var probe = MakeProbe();
            var rect = new Rect(10f, 10f, 100f, 30f);
            bool clicked = false;
            var raw = new List<EventType>();
            GUIProbe.Body = _ =>
            {
                GUI.enabled = false;
                if (Event.current.rawType == EventType.MouseDown) raw.Add(Event.current.type);
                clicked |= GUI.Button(rect, "Off");
                GUI.enabled = true;
            };
            Queue(EventType.MouseDown, new Vector2(20f, 20f));
            Queue(EventType.MouseUp, new Vector2(20f, 20f));
            Frame();
            Assert.False(clicked);
            Assert.Equal(0, GUIUtility.hotControl);
            Assert.Contains(EventType.Ignore, raw);
        }

        [Fact]
        public void TextEditorCopiesItsText()
        {
            var editor = new TextEditor { text = "copy me" };
            editor.SelectAll();
            editor.Copy();
            Assert.Equal("copy me", GUIUtility.systemCopyBuffer);
            Assert.Equal(7, editor.selectIndex);
            editor.MoveLeft();
            Assert.Equal(0, editor.cursorIndex);
            editor.MoveWordRight();
            Assert.Equal(4, editor.cursorIndex);
            editor.MoveLeft();
            editor.SelectCurrentWord();
            Assert.Equal("copy", editor.SelectedText);
        }

        [Fact]
        public void RepaintDrawsSlicedBackgroundsAndClippedText()
        {
            ProjectSettings.Load();
            var device = new RecordingDevice();
            var previous = OniMoono.Engine.Rendering.GraphicsDevice.Current;
            OniMoono.Engine.Rendering.GraphicsDevice.Install(device);
            try
            {
                var probe = MakeProbe();
                GUIProbe.Body = _ =>
                {
                    GUI.Box(new Rect(10f, 10f, 100f, 40f), GUIContent.none);
                    GUI.BeginGroup(new Rect(200f, 100f, 30f, 20f));
                    GUI.Label(new Rect(0f, 0f, 300f, 20f), "A long label clipped by its group");
                    GUI.EndGroup();
                };
                Frame();
                // The box: nine slices of the skin's box texture inside its rect.
                var box = device.Draws[0];
                Assert.Equal("Hidden/Internal-GUITextureClip", box.Material.shader.name);
                Assert.Equal(36, box.Vertices.Length);
                foreach (var v in box.Vertices)
                {
                    Assert.InRange(v.Position.x, 10f, 110f);
                    Assert.InRange(v.Position.y, 10f, 50f);
                }
                // Pixel space with the origin at the top left.
                var topLeft = box.Projection.MultiplyPoint(new Vector3(0f, 0f, 0f));
                Assert.Equal(-1f, topLeft.x, 3);
                Assert.Equal(1f, topLeft.y, 3);
                // The label's glyphs, clipped to the group.
                var text = device.Draws[1];
                Assert.Equal("Hidden/Internal-GUITextureClipText", text.Material.shader.name);
                Assert.NotEmpty(text.Vertices);
                foreach (var v in text.Vertices)
                {
                    Assert.InRange(v.Position.x, 200f, 230f);
                    Assert.InRange(v.Position.y, 100f, 120f);
                }
            }
            finally
            {
                OniMoono.Engine.Rendering.GraphicsDevice.Install(previous);
            }
        }

        // EventType has lower-case aliases, so ToString is ambiguous.
        static string Name(EventType t) => t switch
        {
            EventType.Layout => "Layout",
            EventType.Repaint => "Repaint",
            EventType.Used => "Used",
            _ => ((int)t).ToString(),
        };

        [Fact]
        public void WindowsDrawAfterOnGUIAndCanBeDragged()
        {
            var probe = MakeProbe();
            var windowRect = new Rect(50f, 50f, 200f, 100f);
            var order = new List<string>();
            GUIProbe.Body = _ =>
            {
                order.Add("ongui:" + Name(Event.current.type));
                windowRect = GUI.Window(7, windowRect, id =>
                {
                    order.Add("window:" + Name(Event.current.type));
                    GUI.DragWindow();
                }, "Title", GUI.skin.window);
            };
            Frame();
            Assert.Equal(new[] { "ongui:Layout", "window:Layout", "ongui:Repaint", "window:Repaint" }, order);
            Queue(EventType.MouseDown, new Vector2(60f, 60f));
            Event.Enqueue(new Event { type = EventType.MouseDrag, mousePosition = new Vector2(90f, 75f), delta = new Vector2(30f, 15f) });
            Queue(EventType.MouseUp, new Vector2(90f, 75f));
            Frame();
            Frame();
            Assert.Equal(new Rect(80f, 65f, 200f, 100f), windowRect);
        }

        [Fact]
        public void PopEventLeavesOnGUIItsEventsAndTheRestIsDropped()
        {
            var probe = MakeProbe();
            while (Event.PopEvent(new Event())) { }
            Queue(EventType.MouseDown, new Vector2(5f, 5f));
            Queue(EventType.MouseUp, new Vector2(5f, 5f));
            Assert.Equal(2, Event.GetEventCount());
            var popped = new Event();
            Assert.True(Event.PopEvent(popped));
            Assert.Equal(EventType.MouseDown, popped.type);
            Assert.Equal(new Vector2(5f, 5f), popped.mousePosition);
            Assert.Equal(1, Event.GetEventCount());
            Frame();
            // OnGUI got both, and the unpopped MouseUp did not survive the frame.
            Assert.Equal(new[] { EventType.Layout, EventType.MouseDown, EventType.Layout, EventType.MouseUp, EventType.Layout, EventType.Repaint }, probe.Events);
            Assert.Equal(0, Event.GetEventCount());
            Frame();
            Assert.False(Event.PopEvent(new Event()));
        }

        [Fact]
        public void BehavioursWithoutGUILayoutGetNoLayoutEvents()
        {
            var probe = MakeProbe();
            probe.useGUILayout = false;
            Frame();
            Assert.Equal(new[] { EventType.Repaint }, probe.Events);
            probe.Events.Clear();
            Queue(EventType.MouseDown, new Vector2(5f, 5f));
            Frame();
            Assert.Equal(new[] { EventType.MouseDown, EventType.Repaint }, probe.Events);
            // GUILayout is an error there; GUI controls work.
            bool reached = false, clicked = false;
            GUIProbe.Body = _ =>
            {
                clicked |= GUI.Button(new Rect(0f, 0f, 50f, 20f), "b");
                GUILayout.Label("not allowed");
                reached = true;
            };
            Frame();
            Assert.False(reached);
            Assert.Single(errors);
            Assert.Contains("useGUILayout", errors[0]);
            errors.Clear();
        }

        [Fact]
        public void ExitGUILeavesTheCallSilently()
        {
            var first = MakeProbe();
            var second = MakeProbe();
            var log = new List<string>();
            bool exiting = false;
            GUIProbe.Body = p =>
            {
                if (p != first) { log.Add("second"); return; }
                GUI.BeginGroup(new Rect(0f, 0f, 10f, 10f));
                try
                {
                    GUIUtility.ExitGUI();
                }
                finally
                {
                    exiting = GUIUtility.guiIsExiting;
                }
                log.Add("after exit");
            };
            Frame();
            Assert.True(exiting);
            Assert.False(GUIUtility.guiIsExiting);
            Assert.DoesNotContain("after exit", log);
            Assert.Contains("second", log);
            // No error, not even for the group left open.
            Assert.Empty(errors);
        }

        [Fact]
        public void UnbalancedClipsAreReported()
        {
            var probe = MakeProbe();
            GUIProbe.Body = _ => GUI.BeginGroup(new Rect(0f, 0f, 10f, 10f));
            Frame();
            Assert.Contains("GUI Error: You are pushing more GUIClips than you are popping. Make sure they are balanced.", errors);
            errors.Clear();
        }

        [Fact]
        public void TabMovesKeyboardFocusThroughTheFields()
        {
            var probe = MakeProbe();
            string a = "one", b = "two";
            var focused = new List<string>();
            GUIProbe.Body = _ =>
            {
                GUI.SetNextControlName("a");
                a = GUI.TextField(new Rect(0f, 0f, 100f, 20f), a);
                GUI.SetNextControlName("b");
                b = GUI.TextField(new Rect(0f, 30f, 100f, 20f), b);
                if (Event.current.type == EventType.Repaint) focused.Add(GUI.GetNameOfFocusedControl());
            };
            Frame();
            Assert.Equal(0, GUIUtility.keyboardControl);
            // A press sends Tab, then its '\t' character; the character is used too.
            QueueKey(KeyCode.Tab);
            QueueKey(KeyCode.None, '\t');
            Frame();
            Assert.Equal("a", focused[^1]);
            QueueKey(KeyCode.Tab);
            QueueKey(KeyCode.None, '\t');
            Frame();
            Assert.Equal("b", focused[^1]);
            QueueKey(KeyCode.Tab, '\0', EventModifiers.Shift);
            QueueKey(KeyCode.None, '\t', EventModifiers.Shift);
            Frame();
            Assert.Equal("a", focused[^1]);
            Assert.Equal("one", a);
            Assert.Equal("two", b);
        }

        [Fact]
        public void ScrollViewsShowTheirContentAtTheScrollPosition()
        {
            var probe = MakeProbe();
            var position = new Rect(10f, 10f, 100f, 100f);
            var view = new Rect(0f, 0f, 80f, 400f);
            var scroll = new Vector2(0f, 50f);
            Vector2 mouse = default;
            Rect visible = default;
            GUIProbe.Body = _ =>
            {
                scroll = GUI.BeginScrollView(position, scroll, view);
                if (Event.current.type == EventType.MouseDown)
                {
                    mouse = Event.current.mousePosition;
                    visible = GUIClip.visibleRect;
                }
                GUI.EndScrollView();
            };
            Queue(EventType.MouseDown, new Vector2(20f, 20f));
            Frame();
            Assert.Equal(new Vector2(0f, 50f), scroll);
            Assert.Equal(new Vector2(10f, 60f), mouse);
            Assert.Equal(50f, visible.y);
            Assert.Equal(100f, visible.height);
            Assert.True(visible.width < 100f); // the vertical scrollbar takes room
            // The returned position is clamped to the content.
            scroll = new Vector2(30f, 1000f);
            Frame();
            Assert.Equal(new Vector2(0f, 300f), scroll);
            // The scroll wheel over the view scrolls it, 20 pixels a unit of delta (a notch on
            // Linux) as in Unity's player.
            scroll = new Vector2(0f, 50f);
            Frame();
            Event.Enqueue(new Event { type = EventType.ScrollWheel, mousePosition = new Vector2(50f, 50f), delta = new Vector2(0f, 1f) });
            Frame();
            Frame();
            Assert.Equal(70f, scroll.y);
            // ScrollTo brings a rect of the content into view.
            GUIProbe.Body = _ =>
            {
                scroll = GUI.BeginScrollView(position, scroll, view);
                if (Event.current.type == EventType.Repaint) GUI.ScrollTo(new Rect(0f, 350f, 10f, 20f));
                GUI.EndScrollView();
            };
            Frame();
            Frame();
            Assert.Equal(270f, scroll.y);
        }

        [Fact]
        public void LayoutScrollViewsLayOutTheirContentAtItsOwnSize()
        {
            var probe = MakeProbe();
            var rects = new Rect[10];
            Rect viewArea = default;
            GUIProbe.Body = _ =>
            {
                GUILayout.BeginArea(new Rect(0f, 0f, 200f, 100f));
                GUILayout.BeginScrollView(Vector2.zero, GUIStyle.none, GUIStyle.none);
                for (int i = 0; i < rects.Length; i++)
                    rects[i] = GUILayoutUtility.GetRect(100f, 30f, GUILayout.Width(100f), GUILayout.Height(30f));
                GUILayout.EndScrollView();
                viewArea = GUILayoutUtility.GetLastRect();
                GUILayout.EndArea();
            };
            Frame();
            Assert.Equal(new Rect(0f, 90f, 100f, 30f), rects[3]);
            Assert.Equal(new Rect(0f, 270f, 100f, 30f), rects[9]);
            // The scroll view itself fills the area.
            Assert.Equal(new Rect(0f, 0f, 200f, 100f), viewArea);
        }

        [Fact]
        public void LayoutWindowsFitTheirContent()
        {
            var probe = MakeProbe();
            var style = new GUIStyle { padding = new RectOffset(5, 5, 20, 5) };
            var small = new Rect(10f, 10f, 0f, 0f);
            var large = new Rect(300f, 10f, 500f, 500f);
            var wide = new Rect(10f, 300f, 0f, 0f);
            Rect inner = default;
            GUIProbe.Body = _ =>
            {
                small = GUILayout.Window(1, small, id =>
                {
                    var r = GUILayoutUtility.GetRect(100f, 40f, GUILayout.Width(100f), GUILayout.Height(40f));
                    if (Event.current.type == EventType.Repaint) inner = r;
                }, "", style);
                large = GUILayout.Window(2, large, id => GUILayoutUtility.GetRect(100f, 40f, GUILayout.Width(100f), GUILayout.Height(40f)), "", style);
                wide = GUILayout.Window(3, wide, id => GUILayoutUtility.GetRect(100f, 40f, GUILayout.Width(100f), GUILayout.Height(40f)), "", style, GUILayout.Width(300f));
            };
            Frame();
            Frame();
            Assert.Equal(new Rect(10f, 10f, 110f, 65f), small);
            // The size passed in is the least it gets ("GUILayout, measured", part 3).
            Assert.Equal(new Rect(300f, 10f, 500f, 500f), large);
            Assert.Equal(new Rect(10f, 300f, 300f, 65f), wide);
            Assert.Equal(new Rect(5f, 20f, 100f, 40f), inner);
        }

        [Fact]
        public void GetStyleFindsStylesByNameAndFallsBack()
        {
            var warnings = new List<string>();
            void OnWarning(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Warning) warnings.Add(message);
            }
            var skin = ScriptableObject.CreateInstance<GUISkin>();
            skin.name = "TestSkin";
            var mine = new GUIStyle { name = "Mine" };
            skin.customStyles = new[] { mine };
            Application.logMessageReceivedThreaded += OnWarning;
            try
            {
                Assert.Same(skin.button, skin.GetStyle("BUTTON"));
                Assert.Same(mine, skin.GetStyle("mine"));
                Assert.Same(mine, skin.FindStyle("MINE"));
                Assert.Null(skin.FindStyle("missing"));
                var fallback = skin.GetStyle("missing");
                Assert.NotNull(fallback);
                Assert.Contains(warnings, w => w.StartsWith("Unable to find style 'missing' in skin 'TestSkin'"));
                int count = 0;
                foreach (GUIStyle s in skin) count++;
                Assert.Equal(21, count);
            }
            finally
            {
                Application.logMessageReceivedThreaded -= OnWarning;
                UnityEngine.Object.DestroyImmediate(skin);
            }
            // A string converts to the current skin's style of that name.
            var probe = MakeProbe();
            GUIStyle converted = null, button = null;
            GUIProbe.Body = _ =>
            {
                converted = "Button";
                button = GUI.skin.button;
            };
            Frame();
            Assert.Same(button, converted);
        }

        [Fact]
        public void KeyboardEventParsesKeyStrings()
        {
            var e = Event.KeyboardEvent("^a");
            Assert.Equal(EventType.KeyDown, e.type);
            Assert.Equal(KeyCode.A, e.keyCode);
            Assert.True(e.control);
            Assert.False(e.shift);
            e = Event.KeyboardEvent("#home");
            Assert.Equal(KeyCode.Home, e.keyCode);
            Assert.True(e.shift);
            Assert.True(e.functionKey);
            e = Event.KeyboardEvent("&%f4");
            Assert.Equal(KeyCode.F4, e.keyCode);
            Assert.True(e.alt);
            Assert.True(e.command);
            e = Event.KeyboardEvent("[+]");
            Assert.Equal(KeyCode.KeypadPlus, e.keyCode);
            Assert.True(e.numeric);
            e = Event.KeyboardEvent("space");
            Assert.Equal(KeyCode.Space, e.keyCode);
            e = Event.KeyboardEvent("#");
            Assert.Equal(KeyCode.Hash, e.keyCode);
            Assert.Equal(EventModifiers.None, e.modifiers);
            Assert.Equal(Event.KeyboardEvent("^a"), Event.KeyboardEvent("^a"));
            Assert.NotEqual(Event.KeyboardEvent("^a"), Event.KeyboardEvent("a"));
        }
    
        [Fact]
        public void LayoutToolbarTakesTheAvailableWidthAndOneButtonRow()
        {
            var probe = MakeProbe();
            Rect bar = default, next = default, narrow = default;
            float buttonHeight = 0f;
            int buttonMargin = 0;
            GUIProbe.Body = _ =>
            {
                buttonHeight = GUI.skin.button.CalcSize(new GUIContent("One")).y;
                buttonMargin = GUI.skin.button.margin.bottom;
                GUILayout.BeginArea(new Rect(0f, 0f, 400f, 300f));
                GUILayout.Toolbar(0, new[] { "One", "Two", "Three" });
                if (Event.current.type == EventType.Repaint) bar = GUILayoutUtility.GetLastRect();
                next = GUILayoutUtility.GetRect(10f, 10f);
                GUILayout.EndArea();
                GUILayout.BeginArea(new Rect(0f, 100f, 400f, 100f));
                GUILayout.Toolbar(0, new[] { "One", "Two", "Three" }, GUILayout.Width(300f));
                if (Event.current.type == EventType.Repaint) narrow = GUILayoutUtility.GetLastRect();
                GUILayout.EndArea();
            };
            Frame();
            // Measured in the original player with the game's skin: (0, 0, 400 x 19), the next
            // control at y = 23. The height is the button style's one-line height (padding 3 + 3
            // plus the line, 13.01), rounded by the area along its axis; the gap is the style's
            // margin (4).
            Assert.Equal(19.01f, buttonHeight, 3);
            Assert.Equal(new Rect(0f, 0f, 400f, 19f), bar);
            Assert.Equal(4, buttonMargin);
            Assert.Equal(23f, next.y);
            Assert.Equal(300f, narrow.width);
            Assert.Equal(19f, narrow.height);
        }

        [Fact]
        public void ToolbarReturnsTheClickedIndex()
        {
            var probe = MakeProbe();
            int selected = 0;
            bool changed = false;
            GUIProbe.Body = _ =>
            {
                selected = GUI.Toolbar(new Rect(0f, 0f, 300f, 20f), selected, new[] { "A", "B", "C" });
                if (GUI.changed) changed = true;
            };
            Frame();
            Assert.Equal(0, selected);
            Queue(EventType.MouseDown, new Vector2(250f, 10f));
            Queue(EventType.MouseUp, new Vector2(250f, 10f));
            Frame();
            Assert.Equal(2, selected);
            Assert.True(changed);
            Queue(EventType.MouseDown, new Vector2(150f, 10f));
            Queue(EventType.MouseUp, new Vector2(150f, 10f));
            Frame();
            Assert.Equal(1, selected);
            // Clicking the selected button keeps it and does not change anything.
            changed = false;
            Queue(EventType.MouseDown, new Vector2(150f, 10f));
            Queue(EventType.MouseUp, new Vector2(150f, 10f));
            Frame();
            Assert.Equal(1, selected);
            Assert.False(changed);
        }

        [Fact]
        public void ModalWindowRunsAfterOnGUIAndTakesTheInputAlone()
        {
            var probe = MakeProbe();
            var order = new List<string>();
            bool showModal = false, outsideClicked = false, insideClicked = false, windowClicked = false;
            bool hasModalInOnGUI = false;
            var modalRect = new Rect(100f, 100f, 200f, 100f);
            GUIProbe.Body = _ =>
            {
                hasModalInOnGUI = GUIUtility.hasModalWindow;
                order.Add("ongui:" + Name(Event.current.type));
                if (GUI.Button(new Rect(0f, 0f, 50f, 20f), "outside")) outsideClicked = true;
                GUI.Window(1, new Rect(0f, 300f, 100f, 100f), id =>
                {
                    if (GUI.Button(new Rect(10f, 30f, 50f, 20f), "w")) windowClicked = true;
                }, "plain");
                if (!showModal) return;
                modalRect = GUI.ModalWindow(2, modalRect, id =>
                {
                    order.Add("modal:" + Name(Event.current.type));
                    if (GUI.Button(new Rect(10f, 30f, 50f, 20f), "in")) insideClicked = true;
                }, "Modal", GUI.skin.window);
            };
            Frame();
            Assert.False(GUIUtility.hasModalWindow);
            showModal = true;
            Frame();
            Assert.True(GUIUtility.hasModalWindow);
            order.Clear();
            Frame();
            Assert.Equal(new[] { "ongui:Layout", "modal:Layout", "ongui:Repaint", "modal:Repaint" }, order);
            Assert.Equal(new Rect(100f, 100f, 200f, 100f), modalRect);
            Assert.True(hasModalInOnGUI);

            // A button outside the modal window, and one in another window, do not react.
            Queue(EventType.MouseDown, new Vector2(10f, 10f));
            Queue(EventType.MouseUp, new Vector2(10f, 10f));
            Queue(EventType.MouseDown, new Vector2(20f, 340f));
            Queue(EventType.MouseUp, new Vector2(20f, 340f));
            order.Clear();
            Frame();
            Assert.False(outsideClicked);
            Assert.False(windowClicked);
            Assert.DoesNotContain("ongui:" + Name(EventType.MouseDown), order);
            Assert.Contains("modal:" + Name(EventType.MouseDown), order);
            // Its own button does.
            Queue(EventType.MouseDown, new Vector2(120f, 140f));
            Queue(EventType.MouseUp, new Vector2(120f, 140f));
            Frame();
            Assert.True(insideClicked);

            // Once it is no longer shown, the input reaches the rest again.
            showModal = false;
            Frame();
            Assert.False(GUIUtility.hasModalWindow);
            Queue(EventType.MouseDown, new Vector2(10f, 10f));
            Queue(EventType.MouseUp, new Vector2(10f, 10f));
            Queue(EventType.MouseDown, new Vector2(20f, 340f));
            Queue(EventType.MouseUp, new Vector2(20f, 340f));
            Frame();
            Assert.True(outsideClicked);
            Assert.True(windowClicked);
        }

        [Fact]
        public void ModalWindowDrawsInFrontOfOtherWindows()
        {
            var probe = MakeProbe();
            var order = new List<string>();
            GUIProbe.Body = _ =>
            {
                GUI.ModalWindow(1, new Rect(100f, 100f, 100f, 100f), id => order.Add("modal:" + Name(Event.current.type)), "m");
                GUI.Window(2, new Rect(0f, 0f, 100f, 100f), id => order.Add("window:" + Name(Event.current.type)), "w");
            };
            Frame();
            order.Clear();
            Frame();
            // The plain window was declared later (so it would be in front), but the modal one draws last.
            Assert.Equal(new[] { "window:Repaint", "modal:Repaint" }, order.FindAll(o => o.EndsWith("Repaint")));
        }

        // ----- GUI.tooltip -----

        static void MoveMouse(Vector2 position) => Queue(EventType.MouseMove, position);

        /// GUI.tooltipRect is protected; a subclass reads it.
        sealed class TooltipRectReader : GUI
        {
            public static Rect Value => tooltipRect;
        }

        [Fact]
        public void TooltipStartsEmptyNeverNullAndReadsOutsideOnGUI()
        {
            Assert.Equal("", GUI.tooltip);
            GUI.tooltip = "x";
            Assert.Equal("x", GUI.tooltip);
            GUI.tooltip = null;
            Assert.Equal("", GUI.tooltip);
            Assert.Equal(new Rect(0f, 0f, 0f, 0f), TooltipRectReader.Value);
        }

        [Fact]
        public void HoveredControlSetsTooltipDuringRepaintAndNothingClearsIt()
        {
            var probe = MakeProbe();
            var reads = new List<string>();
            GUIProbe.Body = _ =>
            {
                string start = GUI.tooltip;
                GUI.Button(new Rect(20f, 20f, 100f, 30f), new GUIContent("A", "tA"));
                string afterA = GUI.tooltip;
                GUI.Button(new Rect(20f, 60f, 100f, 30f), new GUIContent("B"));
                reads.Add(Name(Event.current.rawType) + " " + start + " " + afterA + " " + GUI.tooltip);
            };
            MoveMouse(new Vector2(19f, 19f));
            Frame();
            Assert.Equal("", GUI.tooltip);
            reads.Clear();
            MoveMouse(new Vector2(21f, 21f));
            Frame();
            // The move only places the mouse (the player sends no MouseMove events); A sets the
            // tooltip as it draws in Repaint.
            Assert.Equal(new[] { "Layout   ", "Repaint  tA tA" }, reads);
            Assert.Equal("tA", GUI.tooltip);
            reads.Clear();
            // Over B (no tooltip), then over nothing: the last tooltip stays.
            MoveMouse(new Vector2(30f, 70f));
            Frame();
            MoveMouse(new Vector2(500f, 500f));
            Frame();
            Assert.Equal("tA", GUI.tooltip);
            Assert.All(reads, r => Assert.EndsWith("tA tA tA", r));
        }

        [Fact]
        public void InputEventsOverAControlDoNotSetTheTooltip()
        {
            var probe = MakeProbe();
            var seen = new List<string>();
            GUIProbe.Body = _ =>
            {
                GUI.tooltip = "-";
                GUI.Button(new Rect(20f, 20f, 100f, 30f), new GUIContent("A", "tA"));
                GUI.Label(new Rect(20f, 60f, 100f, 30f), new GUIContent("L", "tL"));
                if (Event.current.rawType != EventType.Repaint) seen.Add(GUI.tooltip);
            };
            Queue(EventType.MouseDown, new Vector2(30f, 30f));
            Event.Enqueue(new Event { type = EventType.MouseDrag, mousePosition = new Vector2(30f, 70f), delta = new Vector2(0f, 40f) });
            Queue(EventType.MouseUp, new Vector2(30f, 70f));
            Frame();
            Assert.NotEmpty(seen);
            Assert.All(seen, v => Assert.Equal("-", v));
            // Repaint, over L.
            Assert.Equal("tL", GUI.tooltip);
        }

        [Fact]
        public void ButtonsTogglesLabelsBoxesAndLayoutControlsSetItWhenHoveredInTheirClip()
        {
            var probe = MakeProbe();
            var tips = new Dictionary<string, string>();
            bool toggle = false;
            GUIProbe.Body = _ =>
            {
                void After(string name)
                {
                    if (Event.current.type == EventType.Repaint) tips[name] = GUI.tooltip;
                    GUI.tooltip = "-";
                }
                GUI.tooltip = "-";
                GUI.Label(new Rect(20f, 100f, 100f, 30f), new GUIContent("L", "tL")); After("L");
                GUI.Box(new Rect(20f, 140f, 100f, 30f), new GUIContent("X", "tX")); After("X");
                toggle = GUI.Toggle(new Rect(140f, 20f, 100f, 30f), toggle, new GUIContent("T", "tT")); After("T");
                GUI.enabled = false;
                GUI.Button(new Rect(140f, 140f, 100f, 30f), new GUIContent("H", "tH")); After("H");
                GUI.enabled = true;
                GUI.Button(new Rect(140f, 60f, 100f, 30f), new GUIContent("Empty", "")); After("Empty");
                GUILayout.BeginArea(new Rect(20f, 180f, 200f, 100f));
                GUILayout.Button(new GUIContent("C", "tC")); After("C");
                GUILayout.Label(new GUIContent("D", "tD")); After("D");
                GUILayout.EndArea();
                GUI.BeginGroup(new Rect(300f, 300f, 200f, 100f));
                GUI.Button(new Rect(10f, 10f, 100f, 30f), new GUIContent("E", "tE")); After("E");
                GUI.EndGroup();
                GUI.BeginScrollView(new Rect(300f, 420f, 200f, 80f), Vector2.zero, new Rect(0f, 0f, 180f, 300f));
                GUI.Button(new Rect(0f, 0f, 100f, 30f), new GUIContent("F", "tF")); After("F");
                GUI.Button(new Rect(0f, 100f, 100f, 30f), new GUIContent("G", "tG")); After("G");
                GUI.EndScrollView();
            };
            string At(Vector2 p, string name)
            {
                MoveMouse(p);
                Frame();
                return tips[name];
            }
            Assert.Equal("tL", At(new Vector2(30f, 110f), "L"));
            Assert.Equal("tX", At(new Vector2(30f, 150f), "X"));
            Assert.Equal("tT", At(new Vector2(150f, 30f), "T"));
            Assert.Equal("tH", At(new Vector2(150f, 150f), "H"));
            Assert.Equal("-", At(new Vector2(150f, 70f), "Empty"));
            Assert.Equal("tC", At(new Vector2(30f, 185f), "C"));
            Assert.Equal("-", At(new Vector2(30f, 185f), "D"));
            Assert.Equal("tE", At(new Vector2(320f, 320f), "E"));
            Assert.Equal("-", At(new Vector2(320f, 320f), "L"));
            Assert.Equal("tF", At(new Vector2(310f, 430f), "F"));
            // G is at y 100 in the scroll view, below its visible 80 pixels.
            Assert.Equal("-", At(new Vector2(310f, 530f), "G"));
        }

        [Fact]
        public void TheLastHoveredControlDrawnWinsAndWindowsHideNothing()
        {
            var probe = MakeProbe();
            string afterO1 = null, afterO2 = null;
            GUIProbe.Body = _ =>
            {
                GUI.Button(new Rect(140f, 100f, 100f, 30f), new GUIContent("O1", "tO1"));
                if (Event.current.type == EventType.Repaint) afterO1 = GUI.tooltip;
                GUI.Button(new Rect(180f, 100f, 100f, 30f), new GUIContent("O2", "tO2"));
                if (Event.current.type == EventType.Repaint) afterO2 = GUI.tooltip;
                // Z lies under window 1.
                GUI.Button(new Rect(420f, 200f, 100f, 30f), new GUIContent("Z", "tZ"));
                GUI.Window(1, new Rect(400f, 20f, 300f, 250f), id =>
                    GUI.Button(new Rect(10f, 25f, 100f, 30f), new GUIContent("W1a", "tW1a")), new GUIContent("Win1", "tWin1"));
            };
            MoveMouse(new Vector2(200f, 110f));
            Frame();
            Assert.Equal("tO1", afterO1);
            Assert.Equal("tO2", afterO2);
            Assert.Equal("tO2", GUI.tooltip);
            MoveMouse(new Vector2(430f, 210f));
            Frame();
            Assert.Equal("tZ", GUI.tooltip);
            MoveMouse(new Vector2(420f, 50f));
            Frame();
            Assert.Equal("tW1a", GUI.tooltip);
            // The window's title does not set it.
            MoveMouse(new Vector2(550f, 28f));
            Frame();
            Assert.Equal("tW1a", GUI.tooltip);
        }

        [Fact]
        public void KeyboardFocusDoesNotSetTheTooltipButHoldingTheMouseDoes()
        {
            var probe = MakeProbe();
            int idA = 0;
            bool focus = false;
            GUIProbe.Body = _ =>
            {
                GUI.tooltip = "-";
                if (focus) GUIUtility.keyboardControl = idA;
                GUI.Button(new Rect(20f, 20f, 100f, 30f), new GUIContent("A", "tA"));
                if (idA == 0 && GUIUtility.hotControl != 0) idA = GUIUtility.hotControl;
            };
            // A press over A, then dragged off: A holds the mouse and sets "tA" each Repaint.
            Queue(EventType.MouseDown, new Vector2(30f, 30f));
            Frame();
            Assert.NotEqual(0, idA);
            Event.Enqueue(new Event { type = EventType.MouseDrag, mousePosition = new Vector2(500f, 500f), delta = new Vector2(470f, 470f) });
            Frame();
            Assert.Equal(idA, GUIUtility.hotControl);
            Assert.Equal("tA", GUI.tooltip);
            Frame();
            Assert.Equal("tA", GUI.tooltip);
            Queue(EventType.MouseUp, new Vector2(500f, 500f));
            Frame();
            Assert.Equal(0, GUIUtility.hotControl);
            Assert.Equal("-", GUI.tooltip);
            focus = true;
            Frame();
            Assert.Equal(idA, GUIUtility.keyboardControl);
            Assert.Equal("-", GUI.tooltip);
        }

        [Fact]
        public void ATooltipSetFromCodeSticksUntilAControlSetsIt()
        {
            var probe = MakeProbe();
            bool set = true;
            var windowReads = new List<string>();
            GUIProbe.Body = _ =>
            {
                if (set) GUI.tooltip = "S";
                GUI.Button(new Rect(20f, 20f, 100f, 30f), new GUIContent("A", "tA"));
                GUI.Window(1, new Rect(400f, 20f, 300f, 250f), id => windowReads.Add(GUI.tooltip), "w");
            };
            MoveMouse(new Vector2(500f, 500f));
            Frame();
            Assert.Equal("S", GUI.tooltip);
            Assert.All(windowReads, v => Assert.Equal("S", v));
            windowReads.Clear();
            MoveMouse(new Vector2(30f, 30f));
            Frame();
            // Layout reads the set; Repaint reads A's tooltip.
            Assert.Equal("tA", GUI.tooltip);
            Assert.Equal("tA", windowReads[^1]);
            set = false;
            MoveMouse(new Vector2(500f, 500f));
            Frame();
            Assert.Equal("tA", GUI.tooltip);
        }

        [Fact]
        public void ALayoutWindowReadsTheSameTooltipInLayoutAndRepaint()
        {
            var probe = MakeProbe();
            var reads = new List<string>();
            var rect = new Rect(750f, 20f, 300f, 300f);
            Vector2 scroll = Vector2.zero;
            Rect button = default;
            GUIProbe.Body = _ =>
            {
                rect = GUILayout.Window(2, rect, id =>
                {
                    GUILayout.Button(new GUIContent("W2a", "tW2a"));
                    if (Event.current.type == EventType.Repaint) button = GUILayoutUtility.GetLastRect();
                    scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(100f));
                    for (int i = 0; i < 6; i++) GUILayout.Label(new GUIContent("W2s" + i, "tW2s" + i));
                    GUILayout.EndScrollView();
                    string read = GUI.tooltip;
                    GUILayout.Label(string.IsNullOrEmpty(read) ? "(none)" : read);
                    reads.Add(Name(Event.current.rawType) + " " + read);
                }, "Win2");
            };
            Frame();
            Frame();
            MoveMouse(rect.position + button.center);
            Frame();
            reads.Clear();
            Frame();
            Assert.Equal(new[] { "Layout tW2a", "Repaint tW2a" }, reads);
        }

        [Fact]
        public void OnlyAHoveredLabelSetsTooltipRect()
        {
            var probe = MakeProbe();
            GUIProbe.Body = _ =>
            {
                GUI.Button(new Rect(20f, 20f, 100f, 30f), new GUIContent("A", "tA"));
                GUI.Label(new Rect(20f, 100f, 100f, 30f), new GUIContent("L", "tL"));
                GUI.BeginGroup(new Rect(20f, 180f, 200f, 100f));
                GUI.Label(new Rect(0f, 30f, 150f, 30f), new GUIContent("D", "tD"));
                GUI.EndGroup();
            };
            MoveMouse(new Vector2(30f, 30f));
            Frame();
            Assert.Equal("tA", GUI.tooltip);
            Assert.Equal(new Rect(0f, 0f, 0f, 0f), TooltipRectReader.Value);
            MoveMouse(new Vector2(70f, 116f));
            Frame();
            Assert.Equal(new Rect(70f, 116f, 0f, 0f), TooltipRectReader.Value);
            MoveMouse(new Vector2(120f, 235f));
            Frame();
            Assert.Equal("tD", GUI.tooltip);
            Assert.Equal(new Rect(100f, 55f, 0f, 0f), TooltipRectReader.Value);
            // It persists like the tooltip.
            MoveMouse(new Vector2(500f, 500f));
            Frame();
            Assert.Equal(new Rect(100f, 55f, 0f, 0f), TooltipRectReader.Value);
        }

        [Fact]
        public void AWindowDeclaredInsideAWindowFunctionIsNotShown()
        {
            var probe = MakeProbe();
            bool innerRan = false;
            Rect inner = new(1f, 1f, 1f, 1f);
            GUIProbe.Body = _ => GUI.Window(1, new Rect(400f, 20f, 300f, 250f), id =>
            {
                inner = GUI.Window(3, new Rect(50f, 600f, 200f, 80f), _ => innerRan = true, "Win3");
            }, "Win1");
            Frame();
            Frame();
            Assert.False(innerRan);
            Assert.Equal(new Rect(0f, 0f, 0f, 0f), inner);
        }

        [Fact]
        public void AReleaseOutsideTheHoldingWindowReachesItAsIgnore()
        {
            var probe = MakeProbe();
            var seen = new List<string>();
            int held = 0;
            bool clicked = false;
            GUIProbe.Body = _ =>
            {
                if (Event.current.rawType == EventType.MouseUp) seen.Add("ongui " + Name(Event.current.type));
                GUI.Window(1, new Rect(400f, 20f, 300f, 250f), id =>
                {
                    var e = Event.current;
                    if (e.rawType == EventType.MouseUp) seen.Add("window " + Name(e.type));
                    GUI.tooltip = "-";
                    if (GUI.Button(new Rect(10f, 25f, 100f, 30f), new GUIContent("W1a", "tW1a"))) clicked = true;
                    if (held == 0) held = GUIUtility.hotControl;
                }, "Win1");
            };
            Queue(EventType.MouseDown, new Vector2(420f, 50f));
            Event.Enqueue(new Event { type = EventType.MouseDrag, mousePosition = new Vector2(800f, 680f), delta = new Vector2(380f, 630f) });
            Queue(EventType.MouseUp, new Vector2(800f, 680f));
            Frame();
            Assert.NotEqual(0, held);
            Assert.Equal(new[] { "window " + Name(EventType.Ignore), "ongui " + Name(EventType.MouseUp) }, seen);
            Assert.False(clicked);
            // The button keeps the mouse, and sets the tooltip, until the next press.
            Assert.Equal(held, GUIUtility.hotControl);
            Assert.Equal("tW1a", GUI.tooltip);
            Frame();
            Assert.Equal("tW1a", GUI.tooltip);
            Queue(EventType.MouseDown, new Vector2(1000f, 600f));
            Frame();
            Assert.Equal(0, GUIUtility.hotControl);
            Assert.Equal("-", GUI.tooltip);
        }
    }
}
