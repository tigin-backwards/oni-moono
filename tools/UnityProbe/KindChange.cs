// What a method gives when its IL names a type as a value type that is now a class, or as a
// class that is now a value type (lines "PROBE ... KC ..."), as a mod built before U57 runs where
// the game's LogicValueChanged is a class (Smart Radbolt Storage's OnLogicValueChanged unboxes
// it into a local), or one built before U60 where Crop.CropVal is. The cases are
// ProbeKindCases' (Kind/Cases), built against ProbeKind v1 (Kind/v1); the mod folder gets
// ProbeKindCases.dll and v2's ProbeKind.dll (Kind/v2). A `KindChangeProbeDone` canvas appears
// at the end. PROBE_KC_CRASH=1 adds, last, the cases that crash the player.
using UnityEngine;

namespace SemanticsProbe
{
    public class KindChange : MonoBehaviour
    {
        static void L(string s) => Rec.Log("KC " + s);

        void Start()
        {
            L("start");
            ProbeKindCases.Cases.Run(L, System.Environment.GetEnvironmentVariable("PROBE_KC_CRASH") == "1");
            L("done");
        }

        int frame;

        void Update()
        {
            if (++frame == 3)
            {
                L("=== done");
                var done = new GameObject("KindChangeProbeDone", typeof(RectTransform));
                DontDestroyOnLoad(done);
                done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }
        }
    }
}
