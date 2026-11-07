// Transform.hasChanged and world reads around position, rotation and scale sets (lines
// "PROBE ... HC ..."), 5 s into the main menu. Each case builds a root with a chain of three
// children (the root and the middle child scaled), clears every hasChanged, does one thing and
// logs "HC <case> <root> <c1> <c2> <c3>" with each level's hasChanged as 0 or 1, then
// "HCP <case> ..." with each level's position as float bits in hex. The cases: nothing; position,
// localPosition, rotation, localRotation, localScale, eulerAngles set to the value they hold;
// position set 1e-7 away (equal by Vector3 ==, not bit for bit) and 1 away, on the root and on
// a child; a child's position set equal, to see whether the root's flag moves; -0 for +0; NaN
// to NaN; SetPositionAndRotation with both values held; SetParent to the same parent; and reads of
// world positions straight after a root and a middle set. With PROBE_SCENARIO=haschanged only
// this probe runs; it ends with a HasChangedProbeDone canvas.
using System;
using System.Collections;
using UnityEngine;

namespace SemanticsProbe
{
    public class HasChanged : MonoBehaviour
    {
        static void L(string s) => Rec.Log(s);
        static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
        static string H(Vector3 v) => H(v.x) + "," + H(v.y) + "," + H(v.z);

        Transform[] chain;

        void Build()
        {
            chain = new Transform[4];
            Transform parent = null;
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("hc" + i);
                if (parent != null) go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(1.25f * (i + 1), -0.5f * i, i == 2 ? 0.75f : 0f);
                if (i == 0) go.transform.localScale = new Vector3(2f, 2f, 1f);
                if (i == 2) go.transform.localScale = new Vector3(0.86f, 0.86f, 0.86f);
                parent = go.transform;
                chain[i] = go.transform;
            }
        }

        void Clear()
        {
            foreach (var t in chain) t.hasChanged = false;
        }

        void Report(string name)
        {
            L("HC " + name + " " + (chain[0].hasChanged ? 1 : 0) + " " + (chain[1].hasChanged ? 1 : 0) + " "
              + (chain[2].hasChanged ? 1 : 0) + " " + (chain[3].hasChanged ? 1 : 0));
            L("HCP " + name + " " + H(chain[0].position) + " " + H(chain[1].position) + " " + H(chain[2].position) + " " + H(chain[3].position));
        }

        void Case(string name, Action<Transform[]> act)
        {
            Build();
            Clear();
            act(chain);
            Report(name);
            Destroy(chain[0].gameObject);
        }

        IEnumerator Start()
        {
            yield return new WaitForSeconds(5f);
            Build();
            L("HC fresh " + (chain[0].hasChanged ? 1 : 0) + " " + (chain[3].hasChanged ? 1 : 0));
            Destroy(chain[0].gameObject);

            Case("none", c => { });
            Case("root_position_same", c => c[0].position = c[0].position);
            Case("root_localPosition_same", c => c[0].localPosition = c[0].localPosition);
            Case("root_rotation_same", c => c[0].rotation = c[0].rotation);
            Case("root_localRotation_same", c => c[0].localRotation = c[0].localRotation);
            Case("root_localScale_same", c => c[0].localScale = c[0].localScale);
            Case("root_eulerAngles_same", c => c[0].eulerAngles = c[0].eulerAngles);
            Case("root_position_near", c => c[0].position = c[0].position + new Vector3(1e-7f, 0f, 0f));
            Case("root_position_far", c => c[0].position = c[0].position + new Vector3(1f, 0f, 0f));
            Case("mid_position_same", c => c[2].position = c[2].position);
            Case("mid_localPosition_same", c => c[2].localPosition = c[2].localPosition);
            Case("mid_position_near", c => c[2].position = c[2].position + new Vector3(0f, 1e-7f, 0f));
            Case("mid_position_far", c => c[2].position = c[2].position + new Vector3(0f, 1f, 0f));
            Case("leaf_position_same", c => c[3].position = c[3].position);
            Case("root_position_negzero", c => c[0].position = new Vector3(c[0].position.x, c[0].position.y, -0f));
            Case("root_position_nan_twice", c =>
            {
                c[0].position = new Vector3(float.NaN, 1f, 0f);
                Clear();
                c[0].position = new Vector3(float.NaN, 1f, 0f);
            });
            Case("root_setposrot_same", c => c[0].SetPositionAndRotation(c[0].position, c[0].rotation));
            Case("mid_setparent_same", c => c[2].SetParent(c[1], true));
            Case("root_position_twice_back", c =>
            {
                var p = c[0].position;
                c[0].position = p + new Vector3(3f, 0f, 0f);
                c[0].position = p;
            });
            // World reads straight after a set, every level, from a clean read of each first.
            Case("read_after_root_set", c =>
            {
                foreach (var t in c) { var _ = t.position; }
                c[0].position = new Vector3(5.5f, 7.25f, -1f);
            });
            Case("read_after_mid_set", c =>
            {
                foreach (var t in c) { var _ = t.position; }
                c[2].position = new Vector3(-3.3f, 0.7f, 2f);
            });
            Case("read_after_mid_then_root_set", c =>
            {
                foreach (var t in c) { var _ = t.position; }
                c[2].position = new Vector3(-3.3f, 0.7f, 2f);
                var _2 = c[3].position;
                c[0].localPosition = new Vector3(0.1f, 0.2f, 0.3f);
            });
            Case("read_after_mid_scale_then_leaf_set", c =>
            {
                foreach (var t in c) { var _ = t.position; }
                c[2].localScale = new Vector3(3f, 0.5f, 1f);
                c[3].position = new Vector3(9f, 9f, 9f);
            });

            var done = new GameObject("HasChangedProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("HC done");
        }
    }
}
