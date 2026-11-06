using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Engine.Assets;
using OniMoono.Engine.IMGUI;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// docs/cleanroom/imgui.md, "GUILayout, measured": mismatches (part 1), entry sizes and
    /// spacing (part 2), GUILayout.Window's rect (part 3) and the order of calls and windows
    /// (part 4), with the game's skin.
    public partial class GUILayoutMeasuredTests : EngineTest, IDisposable
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762230");
                return Path.Combine(game, "OxygenNotIncluded_Data");
            }
        }

        readonly List<(LogType Type, string Message)> logged = new();

        void OnLog(string message, string stackTrace, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert) logged.Add((type, message));
        }

        public GUILayoutMeasuredTests()
        {
            Application.logMessageReceivedThreaded += OnLog;
            AssetSystem.Initialize(DataDir);
            GUIManager.Install();
            GUIManager.Reset();
            GUIStateData.Reset();
            GUIProbe.Body = null;
        }

        void IDisposable.Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            GUIProbe.Body = null;
            base.Dispose();
            Assert.Empty(logged);
        }

        static GUIProbe MakeProbe(string name = "gui") => new GameObject(name).AddComponent<GUIProbe>();

        static void Queue(EventType type, Vector2 position)
            => Event.Enqueue(new Event { type = type, mousePosition = position, button = 0, clickCount = 1 });

        static bool Repaint => Event.current.type == EventType.Repaint;

        static string Name(EventType t) => t switch
        {
            EventType.Layout => "Layout",
            EventType.Repaint => "Repaint",
            EventType.Used => "Used",
            EventType.MouseDown => "MouseDown",
            EventType.MouseUp => "MouseUp",
            EventType.MouseMove => "MouseMove",
            _ => ((int)t).ToString(),
        };

        /// Runs `body` inside an area at (20, 20) of `width` × `height` with `style`, and gives
        /// the rects GetLastRect returned during Repaint, by name.
        Dictionary<string, Rect> InArea(Action<Action<string>> body, float width = 400f, float height = 400f, GUIStyle style = null)
        {
            var rects = new Dictionary<string, Rect>();
            MakeProbe();
            GUIProbe.Body = _ =>
            {
                if (style == null) GUILayout.BeginArea(new Rect(20f, 20f, width, height));
                else GUILayout.BeginArea(new Rect(20f, 20f, width, height), style);
                body(name =>
                {
                    if (Repaint) rects[name] = GUILayoutUtility.GetLastRect();
                });
                GUILayout.EndArea();
            };
            Frame();
            return rects;
        }

        static GUIStyle Copy(GUIStyle s, Action<GUIStyle> change)
        {
            var c = new GUIStyle(s);
            change(c);
            return c;
        }

        // ----- Part 2: text, entry sizes and spacing -----

        [Fact]
        public void TextHeightsFollowTheFontSize()
        {
            MakeProbe();
            var sizes = new Dictionary<string, Vector2>();
            float labelLine = 0f, fontLine = 0f, ascent = 0f;
            GUIProbe.Body = _ =>
            {
                var skin = GUI.skin;
                sizes["x"] = skin.label.CalcSize(new GUIContent("x"));
                sizes["2l"] = skin.label.CalcSize(new GUIContent("a\nb"));
                sizes["3l"] = skin.label.CalcSize(new GUIContent("a\nb\nc"));
                sizes["empty"] = skin.label.CalcSize(new GUIContent(""));
                sizes["button"] = skin.button.CalcSize(new GUIContent("x"));
                sizes["box"] = skin.box.CalcSize(new GUIContent("x"));
                sizes["fs8"] = Copy(skin.label, s => s.fontSize = 8).CalcSize(new GUIContent("x"));
                sizes["fs8.2l"] = Copy(skin.label, s => s.fontSize = 8).CalcSize(new GUIContent("a\nb"));
                sizes["fs30"] = Copy(skin.label, s => s.fontSize = 30).CalcSize(new GUIContent("x"));
                sizes["bold"] = Copy(skin.label, s => s.fontStyle = FontStyle.Bold).CalcSize(new GUIContent("x"));
                labelLine = skin.label.lineHeight;
                fontLine = skin.font.lineHeight;
                ascent = skin.font.ascent;
            };
            Frame();
            // One line is s + 0.01 high, each further line adds 1.1 s; the label's padding is 3 + 3.
            Assert.Equal(19.01f, sizes["x"].y, 4);
            Assert.Equal(33.31f, sizes["2l"].y, 4);
            Assert.Equal(47.61f, sizes["3l"].y, 4);
            Assert.Equal(14.01f, sizes["fs8"].y, 4);
            Assert.Equal(22.81f, sizes["fs8.2l"].y, 4);
            Assert.Equal(36.01f, sizes["fs30"].y, 4);
            Assert.Equal(19.01f, sizes["bold"].y, 4);
            // An empty text is one lineHeight (14).
            Assert.Equal(20f, sizes["empty"].y);
            Assert.Equal(19.01f, sizes["button"].y, 4);
            Assert.Equal(21.01f, sizes["box"].y, 4);
            Assert.Equal(14f, labelLine);
            Assert.Equal(14f, fontLine);
            Assert.Equal(11f, ascent);
        }

        [Fact]
        public void EntriesRoundAlongTheirGroupAndSitTheLargerMarginApart()
        {
            var r = InArea(at =>
            {
                GUILayout.Label("a"); at("a");
                GUILayout.Label("b"); at("b");
                GUILayout.Label("c"); at("c");
                GUILayout.Button("d"); at("button");
                GUILayout.Toggle(false, "e"); at("toggle");
                GUILayout.Box("f"); at("box");
                GUILayout.Label("g", GUILayout.ExpandWidth(false)); at("narrow");
                GUILayout.Box("", GUILayout.Height(2f), GUILayout.ExpandWidth(true)); at("line");
            });
            // Labels 19 high (round(19.01)) at 0, 23, 46; a button ceil(19.01) = 20, a toggle 17, a box 22.
            Assert.Equal(new Rect(0f, 0f, 400f, 19f), r["a"]);
            Assert.Equal(new Rect(0f, 23f, 400f, 19f), r["b"]);
            Assert.Equal(new Rect(0f, 46f, 400f, 19f), r["c"]);
            Assert.Equal(20f, r["button"].height);
            Assert.Equal(17f, r["toggle"].height);
            Assert.Equal(22f, r["box"].height);
            Assert.Equal(2f, r["line"].height);
            // Not stretching: ceil(CalcSize.x).
            Assert.Equal(r["narrow"].width, Mathf.Ceil(r["narrow"].width));
            Assert.True(r["narrow"].width < 20f);
            // Along a horizontal group sizes stay unrounded across: labels 19.01 high.
            var h = InArea(at =>
            {
                var mD = Copy(GUI.skin.label, st => st.margin = new RectOffset(7, 3, 0, 0));
                GUILayout.BeginHorizontal();
                GUILayout.Label("a"); at("a");
                GUILayout.Button("b"); at("b");
                GUILayout.Label("d", mD); at("d");
                GUILayout.EndHorizontal(); at("group");
                GUILayout.Label("after"); at("after");
            });
            Assert.Equal(19.01f, h["a"].height, 4);
            // Inset by its margin less the smallest (mD's 0)...
            Assert.Equal(4f, h["a"].y);
            Assert.Equal(0f, h["d"].y);
            // ...but a horizontal group without a style is as high as its highest entry.
            Assert.Equal(20f, h["group"].height);
            Assert.Equal(24f, h["after"].y);
        }

        [Fact]
        public void GapsAreTheLargerMarginAndSpacesTakeNone()
        {
            var r = InArea(at =>
            {
                var skin = GUI.skin;
                var a = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 0, 10));
                var b = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 3, 0));
                var c = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 20, 2));
                GUILayout.Label("a", a); at("mA");
                GUILayout.Label("b", b); at("mB");
                GUILayout.Label("c", c); at("mC");
            });
            Assert.Equal(0f, r["mA"].y);
            Assert.Equal(29f, r["mB"].y);
            Assert.Equal(68f, r["mC"].y);
            var s = InArea(at =>
            {
                GUILayout.Label("a"); at("a");
                GUILayout.Space(10f); at("space");
                GUILayout.Label("b"); at("b");
            });
            // A space starts where the entry before ends, x -4 (its zero margin less the
            // group's smallest), width 0; the next entry keeps its leading margin after it.
            Assert.Equal(new Rect(-4f, 19f, 0f, 10f), s["space"]);
            Assert.Equal(33f, s["b"].y);
            var t = InArea(at =>
            {
                GUILayout.Box("", GUILayout.Height(2f)); at("line");
                GUILayout.Space(4f);
                GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(50f));
                GUILayout.Label("t");
                GUILayout.EndScrollView(); at("scroll");
            });
            // The box's margin still separates it from the scroll view (margin 0) after the space.
            Assert.Equal(t["line"].yMax + 4f + 4f, t["scroll"].y);
        }

        [Fact]
        public void GroupsWithoutAStylePassTheirMarginsOut()
        {
            var r = InArea(at =>
            {
                var big = Copy(GUI.skin.label, st => st.margin = new RectOffset(8, 8, 8, 8));
                GUILayout.Label("a", big); at("a");
                GUILayout.Label("b"); at("b");
            });
            // The area's smallest margin is 4: the margin-8 label is inset by 4.
            Assert.Equal(new Rect(4f, 0f, 392f, 19f), r["a"]);
            Assert.Equal(new Rect(0f, 27f, 400f, 19f), r["b"]);
            var g = InArea(at =>
            {
                var m0 = Copy(GUI.skin.label, st => st.margin = new RectOffset(0, 0, 0, 0));
                GUILayout.Label("0"); at("l0");
                GUILayout.BeginVertical();
                GUILayout.Label("a", m0); at("a");
                GUILayout.Label("b"); at("b");
                GUILayout.EndVertical(); at("group");
                GUILayout.Label("c"); at("after");
            });
            Assert.Equal(4f, g["l0"].x);
            Assert.Equal(new Rect(0f, 23f, 400f, 42f), g["group"]);
            Assert.Equal(0f, g["a"].x);
            Assert.Equal(4f, g["b"].x);
            Assert.Equal(69f, g["after"].y);
        }

        [Fact]
        public void GroupsWithAStyleInsetByTheLargerOfPaddingAndMargin()
        {
            var box = InArea(at =>
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("a"); at("a");
                GUILayout.EndVertical(); at("group");
                GUILayout.Label("b"); at("after");
            });
            Assert.Equal(new Rect(4f, 4f, 392f, 19f), box["a"]);
            Assert.Equal(new Rect(0f, 0f, 400f, 27f), box["group"]);
            Assert.Equal(31f, box["after"].y);
            var pad2 = new GUIStyle { padding = new RectOffset(2, 2, 2, 2) };
            var p = InArea(at =>
            {
                var big = Copy(GUI.skin.label, st => st.margin = new RectOffset(8, 8, 8, 8));
                GUILayout.Label("a", big); at("a");
                GUILayout.Label("b"); at("b");
            }, style: pad2);
            Assert.Equal(new Rect(8f, 8f, 384f, 19f), p["a"]);
            Assert.Equal(4f, p["b"].x);
            var pad = new GUIStyle { padding = new RectOffset(5, 6, 7, 8), margin = new RectOffset(1, 2, 3, 4) };
            var q = InArea(at =>
            {
                GUILayout.BeginVertical(pad);
                GUILayout.Label("a"); at("a");
                GUILayout.EndVertical(); at("group");
            });
            Assert.Equal(new Rect(5f, 7f, 389f, 19f), q["a"]);
            Assert.Equal(34f, q["group"].height);
        }

        [Fact]
        public void TheRootIsAsWideAsItsWidestEntryFromTheOrigin()
        {
            MakeProbe();
            var rects = new Rect[4];
            bool wide = false;
            float labelWidth = 0f, buttonWidth = 0f;
            GUIProbe.Body = _ =>
            {
                labelWidth = Mathf.Ceil(GUI.skin.label.CalcSize(new GUIContent("root0")).x);
                buttonWidth = Mathf.Ceil(GUI.skin.button.CalcSize(new GUIContent("root1")).x);
                GUILayout.Label("root0"); if (Repaint) rects[0] = GUILayoutUtility.GetLastRect();
                GUILayout.Button("root1"); if (Repaint) rects[1] = GUILayoutUtility.GetLastRect();
                if (wide)
                {
                    GUILayout.Label("root2", GUILayout.Width(300f)); if (Repaint) rects[2] = GUILayoutUtility.GetLastRect();
                }
                GUILayout.BeginScrollView(Vector2.zero, GUILayout.MinHeight(50f), GUILayout.MaxHeight(70f));
                GUILayout.Label("tip");
                GUILayout.EndScrollView(); if (Repaint) rects[3] = GUILayoutUtility.GetLastRect();
            };
            Frame();
            float width = Mathf.Max(labelWidth, buttonWidth);
            // The scroll view (margin 0) is the smallest margin: the labels keep their 4.
            Assert.Equal(new Rect(4f, 0f, width, 19f), rects[0]);
            Assert.Equal(width, rects[1].width);
            // The scroll view gets its MaxHeight: the root takes its entries' largest sizes.
            Assert.Equal(70f, rects[3].height);
            wide = true;
            Frame();
            Assert.Equal(300f, rects[0].width);
            Assert.Equal(300f, rects[1].width);
            Assert.Equal(new Rect(4f, 47f, 300f, 19f), rects[2]);
        }

        [Fact]
        public void ScrollViewsKeepTheirContentsMarginsAndMakeRoomForTheBar()
        {
            void Labels(int n)
            {
                for (int i = 0; i < n; i++) GUILayout.Label("a" + i);
            }
            var small = InArea(at =>
            {
                GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(100f));
                GUILayout.Label("a"); at("a0");
                GUILayout.Label("b"); at("a1");
                GUILayout.EndScrollView(); at("view");
                GUILayout.Label("after"); at("after");
            });
            Assert.Equal(new Rect(4f, 4f, 392f, 19f), small["a0"]);
            Assert.Equal(27f, small["a1"].y);
            Assert.Equal(new Rect(0f, 0f, 400f, 100f), small["view"]);
            Assert.Equal(new Rect(4f, 104f, 392f, 19f), small["after"]);
            // Content taller than the view: the vertical bar takes 15 + 1.
            var large = InArea(at =>
            {
                GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(100f));
                GUILayout.Label("first"); at("a0");
                Labels(11);
                GUILayout.EndScrollView();
            });
            Assert.Equal(376f, large["a0"].width);
            // Without options it fills what the area leaves.
            var fill = InArea(at =>
            {
                GUILayout.BeginScrollView(Vector2.zero);
                Labels(2);
                GUILayout.EndScrollView(); at("view");
                GUILayout.Label("after"); at("after");
            });
            Assert.Equal(new Rect(0f, 0f, 400f, 377f), fill["view"]);
            Assert.Equal(381f, fill["after"].y);
            var minmax = InArea(at =>
            {
                GUILayout.BeginScrollView(Vector2.zero, GUILayout.MinHeight(50f), GUILayout.MaxHeight(70f));
                Labels(1);
                GUILayout.EndScrollView(); at("view");
            });
            Assert.Equal(70f, minmax["view"].height);
        }

        [Fact]
        public void WindowContentStartsAtTheWindowStylesPadding()
        {
            MakeProbe();
            Rect layoutFirst = default, layoutSecond = default, guiFirst = default;
            GUIProbe.Body = _ =>
            {
                GUILayout.Window(1, new Rect(500f, 100f, 300f, 200f), id =>
                {
                    GUILayout.Label("first"); if (Repaint) layoutFirst = GUILayoutUtility.GetLastRect();
                    GUILayout.Button("second"); if (Repaint) layoutSecond = GUILayoutUtility.GetLastRect();
                }, "T1");
                GUI.Window(2, new Rect(850f, 100f, 300f, 200f), id =>
                {
                    GUILayout.Label("first"); if (Repaint) guiFirst = GUILayoutUtility.GetLastRect();
                }, "T2");
            };
            Frame();
            Assert.Equal(new Rect(10f, 20f, 280f, 19f), layoutFirst);
            Assert.Equal(new Rect(10f, 43f, 280f, 20f), layoutSecond);
            Assert.Equal(new Rect(10f, 20f, 280f, 19f), guiFirst);
        }

        // ----- Part 3: GUILayout.Window's rect -----

        [Fact]
        public void LayoutWindowsTakeTheLargerOfContentAndGivenSize()
        {
            MakeProbe();
            var got = new Dictionary<string, Rect>();
            void Win(string name, Rect given, int labels, params GUILayoutOption[] options)
            {
                var r = GUILayout.Window(name.GetHashCode(), given, id =>
                {
                    for (int i = 0; i < labels; i++) GUILayout.Label("ab" + i);
                }, name, options);
                got[name + ":" + Name(Event.current.type)] = r;
            }
            GUIProbe.Body = _ =>
            {
                Win("given", new Rect(10f, 10f, 300f, 300f), 2);
                Win("small", new Rect(320f, 10f, 100f, 50f), 2);
                Win("zero", new Rect(320f, 10f, 0f, 0f), 2);
                Win("height", new Rect(10f, 10f, 300f, 300f), 15, GUILayout.Height(100f));
                Win("maxh", new Rect(320f, 10f, 300f, 300f), 2, GUILayout.MaxHeight(100f));
                Win("fixed", new Rect(630f, 10f, 300f, 300f), 2, GUILayout.Width(100f), GUILayout.Height(100f));
                Win("minw", new Rect(630f, 10f, 100f, 50f), 2, GUILayout.MinWidth(400f));
                Win("expand", new Rect(630f, 10f, 100f, 50f), 2, GUILayout.ExpandHeight(true));
            };
            Frame();
            // During Layout the rect passed in comes back unchanged.
            Assert.Equal(new Rect(320f, 10f, 100f, 50f), got["small:Layout"]);
            Assert.Equal(new Rect(10f, 10f, 300f, 300f), got["given:Repaint"]);
            // Content: padding 20 + 10, labels 19.01 unrounded, 4 between.
            Assert.Equal(new Vector2(320f, 10f), got["small:Repaint"].position);
            Assert.Equal(100f, got["small:Repaint"].width);
            Assert.Equal(72.02f, got["small:Repaint"].height, 4);
            Assert.Equal(72.02f, got["zero:Repaint"].height, 4);
            Assert.True(got["zero:Repaint"].width > 20f);
            Assert.Equal(371.15f, got["height:Repaint"].height, 3);
            Assert.Equal(new Rect(320f, 10f, 300f, 100f), got["maxh:Repaint"]);
            Assert.Equal(new Rect(630f, 10f, 100f, 100f), got["fixed:Repaint"]);
            Assert.Equal(400f, got["minw:Repaint"].width);
            Assert.Equal(72.02f, got["expand:Repaint"].height, 4);
        }

        // ----- Part 1: calls that differ from the Layout before them -----

        [Fact]
        public void LeafCallsTakeTheRecordedRectsByPosition()
        {
            MakeProbe();
            bool alt = false;
            var rects = new Dictionary<string, Rect>();
            string thrown = null;
            void At(string name)
            {
                if (Repaint) rects[name] = GUILayoutUtility.GetLastRect();
            }
            GUIProbe.Body = _ =>
            {
                bool change = alt && Repaint;
                GUILayout.BeginArea(new Rect(0f, 0f, 400f, 400f));
                GUILayout.Label("L0"); At("L0");
                if (change)
                {
                    // Content and size arguments are ignored: the recorded rects are taken, by
                    // position. A leaf where a group was recorded gets the group's whole rect,
                    // and the next call goes on with the group's next sibling.
                    GUILayout.Label("a much longer text\nwith a second line"); At("X");
                    GUILayoutUtility.GetRect(200f, 60f); At("G");
                    GUILayout.Label("L1"); At("L1");
                    try
                    {
                        GUILayout.Label("past");
                    }
                    catch (ArgumentException e)
                    {
                        thrown = e.Message;
                    }
                }
                else
                {
                    GUILayout.Label("L1"); At("L1");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("h1");
                    GUILayout.Label("h2");
                    GUILayout.EndHorizontal(); At("H");
                    GUILayout.Label("L2"); At("L2");
                }
                GUILayout.EndArea();
            };
            Frame();
            var steady = new Dictionary<string, Rect>(rects);
            alt = true;
            Frame();
            Assert.Equal(steady["L0"], rects["L0"]);
            Assert.Equal(steady["L1"], rects["X"]);
            Assert.Equal(steady["H"], rects["G"]);
            Assert.Equal(steady["L2"], rects["L1"]);
            Assert.Equal(new Rect(0f, 46f, 400f, 19f), steady["H"]);
            // Past the end during Repaint: ArgumentException, the count in its message.
            Assert.Equal("Getting control 4's position in a group with only 4 controls when doing repaint\nAborting", thrown);
        }

        [Fact]
        public void CallsPastTheEndOfAGroupGetItsLastEntryOutsideRepaint()
        {
            MakeProbe();
            bool extra = false;
            Rect last = default, past = default;
            Exception thrown = null;
            GUIProbe.Body = _ =>
            {
                GUILayout.BeginArea(new Rect(0f, 0f, 400f, 400f));
                GUILayout.Label("L0");
                GUILayout.Label("L1");
                if (Event.current.type == EventType.MouseDown) last = GUILayoutUtility.GetLastRect();
                try
                {
                    if (extra && Event.current.type == EventType.MouseDown)
                    {
                        GUILayout.Label("past");
                        past = GUILayoutUtility.GetLastRect();
                    }
                }
                catch (Exception e)
                {
                    thrown = e;
                }
                GUILayout.EndArea();
            };
            Frame();
            extra = true;
            Queue(EventType.MouseDown, new Vector2(600f, 600f));
            Frame();
            Assert.Null(thrown);
            Assert.Equal(new Rect(0f, 23f, 400f, 19f), last);
            Assert.Equal(last, past);
        }

        [Fact]
        public void ABeginCallWhereALeafWasRecordedEndsTheCallUnlogged()
        {
            var first = MakeProbe("first");
            var second = MakeProbe("second");
            bool alt = false;
            var reached = new List<string>();
            Rect a = default, b = default;
            string caught = null;
            GUIProbe.Body = p =>
            {
                if (p == second)
                {
                    if (Repaint) reached.Add("second");
                    return;
                }
                bool change = alt && Repaint;
                GUILayout.Label("L0");
                if (change)
                {
                    try
                    {
                        GUILayout.BeginHorizontal();
                    }
                    catch (ExitGUIException e)
                    {
                        caught = e.Message;
                        throw;
                    }
                    reached.Add("after begin");
                }
                else
                {
                    GUILayout.Label("L1");
                }
                // A Begin call where a vertical group was recorded takes it: laid out vertically.
                if (change) GUILayout.BeginHorizontal();
                else GUILayout.BeginVertical();
                GUILayout.Label("a"); if (Repaint) a = GUILayoutUtility.GetLastRect();
                GUILayout.Label("b"); if (Repaint) b = GUILayoutUtility.GetLastRect();
                GUILayout.EndVertical();
            };
            Frame();
            Assert.Equal(a.x, b.x);
            Assert.True(b.y > a.y);
            alt = true;
            reached.Clear();
            Frame();
            Assert.Equal("GUILayout: Mismatched LayoutGroup.repaint", caught);
            Assert.DoesNotContain("after begin", reached);
            // Not logged, and the other behaviour still ran.
            Assert.Contains("second", reached);
            alt = false;
            Frame();
            Assert.Equal(a.x, b.x);
        }

        [Fact]
        public void DuringUsedEventsNothingIsCheckedAndEveryCallGetsTheDummy()
        {
            var first = MakeProbe("first");
            var second = MakeProbe("second");
            var after = new List<Rect>();
            var secondSaw = new List<string>();
            bool clicked = false;
            GUIProbe.Body = p =>
            {
                if (p == second)
                {
                    secondSaw.Add(Name(Event.current.type));
                    return;
                }
                GUILayout.BeginArea(new Rect(0f, 0f, 400f, 400f));
                GUILayout.Label("L0");
                if (GUILayout.Button("B")) clicked = true;
                if (Event.current.type == EventType.Used)
                {
                    // More, fewer and other calls than recorded: no error.
                    after.Add(GUILayoutUtility.GetLastRect());
                    GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(60f));
                    GUILayout.Label("a"); after.Add(GUILayoutUtility.GetLastRect());
                    GUILayout.EndScrollView(); after.Add(GUILayoutUtility.GetLastRect());
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("b"); after.Add(GUILayoutUtility.GetLastRect());
                }
                else
                {
                    GUILayout.Label("none", GUILayout.Height(40f));
                }
                GUILayout.Label("L2");
                GUILayout.EndArea();
            };
            Frame();
            secondSaw.Clear();
            // The button sits at (0, 23, 400 x 20).
            Queue(EventType.MouseDown, new Vector2(100f, 30f));
            Queue(EventType.MouseUp, new Vector2(100f, 30f));
            Frame();
            Assert.True(clicked);
            Assert.NotEmpty(after);
            Assert.All(after, r => Assert.Equal(new Rect(0f, 0f, 1f, 1f), r));
            // The event ended with the behaviour that used it.
            Assert.DoesNotContain("MouseDown", secondSaw);
            Assert.DoesNotContain("MouseUp", secondSaw);
            Assert.DoesNotContain("Used", secondSaw);
        }

        [Fact]
        public void EscapedExceptionsEndOnlyTheirCall()
        {
            var first = MakeProbe("first");
            var second = MakeProbe("second");
            bool overrun = false, scrollExit = false;
            int secondRepaints = 0;
            GUIProbe.Body = p =>
            {
                if (p == second)
                {
                    if (Repaint) secondRepaints++;
                    return;
                }
                GUILayout.Label("L0");
                if (overrun && Repaint) GUILayout.Label("past");
                GUI.Window(1, new Rect(500f, 50f, 300f, 300f), id =>
                {
                    GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(100f));
                    GUILayout.Label("in");
                    if (scrollExit && Repaint) GUILayout.Label("past");
                    GUILayout.EndScrollView();
                }, "W");
            };
            Frame();
            overrun = true;
            Frame();
            Assert.Equal(2, secondRepaints);
            Assert.Equal((LogType.Exception, "ArgumentException: Getting control 1's position in a group with only 1 controls when doing repaint\nAborting"), Assert.Single(logged));
            logged.Clear();
            overrun = false;
            scrollExit = true;
            Frame();
            // Left inside its scroll view, the window function also reports its clips.
            Assert.Equal(2, logged.Count);
            Assert.Equal(LogType.Exception, logged[0].Type);
            Assert.Equal((LogType.Error, GUIManager.UnbalancedClips), logged[1]);
            logged.Clear();
            scrollExit = false;
            Frame();
            Assert.Empty(logged);
        }

        [Fact]
        public void CaughtExceptionsLeaveThePositionAndTheGroupStack()
        {
            MakeProbe();
            bool alt = false;
            var seen = new List<string>();
            GUIProbe.Body = _ =>
            {
                bool change = alt && Repaint;
                string Try(Action a)
                {
                    try
                    {
                        a();
                        return "ok";
                    }
                    catch (Exception e)
                    {
                        return e.GetType().Name + ":" + e.Message;
                    }
                }
                GUILayout.Label("L0");
                if (change)
                {
                    // A BeginArea with no area recorded, then its EndArea with no group left.
                    seen.Add(Try(() => GUILayout.BeginArea(new Rect(5f, 5f, 100f, 60f))));
                    seen.Add(Try(() => GUILayout.EndArea()));
                }
                GUILayout.Label("L1");
                if (change)
                {
                    seen.Add(Try(() => GUILayout.Label("past")));
                    seen.Add(Try(() => GUILayout.Label("past again")));
                }
            };
            Frame();
            alt = true;
            Frame();
            Assert.Equal(new[]
            {
                "ArgumentException:Getting control 0's position in a group with only 0 controls when doing repaint\nAborting",
                "InvalidOperationException:Stack empty.",
                "ArgumentException:Getting control 2's position in a group with only 2 controls when doing repaint\nAborting",
                "ArgumentException:Getting control 2's position in a group with only 2 controls when doing repaint\nAborting",
            }, seen);
        }

        [Fact]
        public void GetLastRectStraightAfterABeginCallIsAnError()
        {
            MakeProbe();
            Rect afterBegin = default, afterEnd = default;
            GUIProbe.Body = _ =>
            {
                GUILayout.BeginArea(new Rect(0f, 0f, 400f, 400f));
                GUILayout.BeginVertical(GUI.skin.box);
                if (Repaint) afterBegin = GUILayoutUtility.GetLastRect();
                GUILayout.Label("a");
                GUILayout.EndVertical();
                if (Repaint) afterEnd = GUILayoutUtility.GetLastRect();
                GUILayout.EndArea();
            };
            Frame();
            Assert.Equal(new Rect(0f, 0f, 1f, 1f), afterBegin);
            Assert.Equal(new Rect(0f, 0f, 400f, 27f), afterEnd);
            Assert.Equal((LogType.Error, "You cannot call GetLast immediately after beginning a group."), Assert.Single(logged));
            logged.Clear();
        }

        // ----- Part 4: order of calls and windows -----

        sealed class OrderLog
        {
            public readonly List<string> Calls = new();
            public string Frame => string.Join(" ", Calls);
        }

        [Fact]
        public void EventsGoToTheBehavioursThenToEveryWindow()
        {
            var gui = MakeProbe("gui");
            var gui2 = MakeProbe("gui2");
            var log = new OrderLog();
            bool add404 = false, add400 = false;
            string call = null;
            void Win(int id, Rect r) => GUI.Window(id, r, w => log.Calls.Add("w" + w + ":" + Name(Event.current.type)), "" + id);
            GUIProbe.Body = p =>
            {
                log.Calls.Add((p == gui ? "gui" : "gui2") + ":" + Name(Event.current.type));
                if (p == gui2)
                {
                    Win(405, new Rect(260f, 210f, 300f, 250f));
                    return;
                }
                if (call != null && (call.EndsWith("@Repaint") ? Repaint : Event.current.type == EventType.Layout))
                {
                    switch (call)
                    {
                        case "front403": GUI.BringWindowToFront(403); break;
                        case "back402@Repaint": GUI.BringWindowToBack(402); break;
                        case "focus401": GUI.FocusWindow(401); break;
                    }
                    call = null;
                }
                if (add400) Win(400, new Rect(60f, 60f, 300f, 250f));
                Win(402, new Rect(140f, 120f, 300f, 250f));
                Win(401, new Rect(100f, 90f, 300f, 250f));
                Win(403, new Rect(180f, 150f, 300f, 250f));
                if (add404) Win(404, new Rect(220f, 180f, 300f, 250f));
            };
            Frame();
            // New together: the first declared frontmost. Layout front to back, Repaint back to front.
            Assert.Equal("gui:Layout gui2:Layout w402:Layout w401:Layout w403:Layout w405:Layout gui2:Repaint gui:Repaint w405:Repaint w403:Repaint w401:Repaint w402:Repaint", log.Frame);
            // A window declared later goes in front of those shown; so does one new but declared first.
            log.Calls.Clear();
            add404 = true;
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w404:Layout w402:Layout", log.Frame);
            log.Calls.Clear();
            add400 = true;
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w400:Layout w404:Layout w402:Layout", log.Frame);
            // BringWindowToFront during Layout: that Layout's window pass already has it first.
            log.Calls.Clear();
            call = "front403";
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w403:Layout w400:Layout w404:Layout w402:Layout w401:Layout w405:Layout", log.Frame);
            // BringWindowToBack during Repaint: from the next frame.
            log.Calls.Clear();
            call = "back402@Repaint";
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w403:Layout w400:Layout w404:Layout w402:Layout", log.Frame);
            log.Calls.Clear();
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w403:Layout w400:Layout w404:Layout w401:Layout w405:Layout w402:Layout", log.Frame);
            // FocusWindow does not reorder; outside OnGUI BringWindowToFront changes nothing.
            log.Calls.Clear();
            call = "focus401";
            GUI.BringWindowToFront(402);
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w403:Layout w400:Layout w404:Layout w401:Layout w405:Layout w402:Layout", log.Frame);
            // A window not declared during a Layout is gone; declared again, it is new: in front.
            log.Calls.Clear();
            add400 = false;
            Frame();
            Assert.DoesNotContain("w400", log.Frame);
            log.Calls.Clear();
            add400 = true;
            Frame();
            Assert.StartsWith("gui:Layout gui2:Layout w400:Layout w403:Layout", log.Frame);
        }

        [Fact]
        public void MouseEventsGoToTheFrontWindowUnderTheMouseThenToTheBehaviours()
        {
            var gui = MakeProbe("gui");
            var gui2 = MakeProbe("gui2");
            var log = new List<string>();
            bool record = false;
            void Win(int id, Rect r) => GUI.Window(id, r, w =>
            {
                if (record) log.Add("w" + w + ":" + Name(Event.current.type));
            }, "" + id);
            GUIProbe.Body = p =>
            {
                if (record) log.Add((p == gui ? "gui" : "gui2") + ":" + Name(Event.current.type));
                if (p == gui2) return;
                Win(1, new Rect(0f, 0f, 200f, 200f));
                Win(2, new Rect(100f, 100f, 200f, 200f));
            };
            Frame();
            record = true;
            // Over both: window 1 is in front (declared first).
            Queue(EventType.MouseDown, new Vector2(150f, 150f));
            Frame();
            // Its own Layout first; a press on a window's empty area reaches the behaviours unused,
            // and the window comes to the front at once.
            Assert.Equal(new[]
            {
                "gui:Layout", "gui2:Layout", "w1:Layout", "w2:Layout",
                "w1:MouseDown", "gui:MouseDown", "gui2:MouseDown",
                "gui:Layout", "gui2:Layout", "w1:Layout", "w2:Layout",
                "gui2:Repaint", "gui:Repaint", "w2:Repaint", "w1:Repaint",
            }, log);
            log.Clear();
            Queue(EventType.MouseDown, new Vector2(250f, 250f));
            Frame();
            Assert.Contains("w2:MouseDown", log);
            Assert.DoesNotContain("w1:MouseDown", log);
            Assert.Equal("w2:Layout", log[log.IndexOf("w2:MouseDown") + 5]);
            // No MouseMove events reach OnGUI.
            log.Clear();
            Queue(EventType.MouseMove, new Vector2(50f, 50f));
            Frame();
            Assert.DoesNotContain(log, l => l.EndsWith(":MouseMove"));
            // Layout and Repaint only: two behaviours and two windows each.
            Assert.Equal(8, log.Count);
        }

        [Fact]
        public void APressAControlUsesReachesTheFrontBehaviourAsUsed()
        {
            var gui = MakeProbe("gui");
            var gui2 = MakeProbe("gui2");
            var log = new List<string>();
            GUIProbe.Body = p =>
            {
                if (Event.current.rawType == EventType.MouseDown || Event.current.rawType == EventType.Used) log.Add((p == gui ? "gui" : "gui2") + ":" + Name(Event.current.type));
                if (p == gui2) return;
                GUI.Window(1, new Rect(0f, 0f, 200f, 200f), w =>
                {
                    if (Event.current.type == EventType.MouseDown) log.Add("w1:MouseDown");
                    GUI.Button(new Rect(10f, 30f, 100f, 30f), "b");
                }, "1");
            };
            Frame();
            Queue(EventType.MouseDown, new Vector2(20f, 40f));
            Frame();
            Assert.Equal(new[] { "w1:MouseDown", "gui:Used" }, log);
            Queue(EventType.MouseUp, new Vector2(20f, 40f));
            Frame();
        }
    }
}
