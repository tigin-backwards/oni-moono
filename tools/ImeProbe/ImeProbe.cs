// TEST INFRASTRUCTURE: the window's IME state in both players (issue #30), with Microsoft Pinyin
// selected for the game's own thread only. Lines "IME ..." in Player.log. Needs zh-CN with
// Microsoft Pinyin in the user's language list. Runs once a colony has loaded, then quits.
// Install it with scripts/windows-run-home.sh RUN_HOME SAVE out/bin/ImeProbe/release:110152723
// (the stamp: the Mono string hash of "ImeProbe"), then run-windows.sh or original-windows.sh.
// What it checks: the input context, Text Services Framework's state and the keys it would
// take, as a field is focused and left by each route, the focus lost and regained, and a
// composition through ImmSetCompositionString. TSF takes no keys for a window that is not in
// the foreground, so its lines read the same in both players in a test run.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ImeProbe
{
    public class Mod : KMod.UserMod2
    {
        public override void OnLoad(HarmonyLib.Harmony harmony)
        {
            var go = new GameObject("ImeProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            Debug.Log("IME probe loaded");
        }
    }

    public class Runner : MonoBehaviour
    {
        static void Log(string s) => Debug.Log($"IME f{Time.frameCount} {s}");

        IntPtr game;
        IntPtr threadMgr, keystroke, profileMgr;
        IntPtr originalHkl, userHklBefore;
        static readonly Guid IID_ITfKeystrokeMgr = new Guid("aa80e7f0-2021-11d2-93e0-0060b067b86e");
        static readonly Guid IID_ITfInputProcessorProfileMgr = new Guid("71c6e74c-0f28-11d8-a82a-00065b84435c");
        static readonly Guid TFCAT_TIP_KEYBOARD = new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31");
        static readonly Guid PinyinClsid = new Guid("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");
        static readonly Guid PinyinProfile = new Guid("FA550B04-5AD7-411F-A5AC-CA038EC515D7");
        static readonly Guid COMP_OPENCLOSE = new Guid("58273AAD-01BB-4164-95C6-755BA0B5162D");
        static readonly Guid COMP_CONVERSION = new Guid("CCF05DD8-4A87-11D7-A6E2-00065B84435C");
        HashSet<IntPtr> baseline = new HashSet<IntPtr>();
        readonly List<string> msgs = new List<string>();

        IEnumerator Start()
        {
            while (Game.Instance == null) yield return null;
            for (int i = 0; i < 300; i++) yield return null;
            try { Env(); } catch (Exception e) { Log("env failed " + e); }
            State("play-us");
            try { ActivatePinyin(); } catch (Exception e) { Log("pinyin failed " + e); }
            yield return Frames(30);
            State("play-pinyin-unfocused");
            Log($"SetFocus(game) -> {Hex(SetFocus(game))}");
            yield return Frames(30);
            State("play-focused");
            Snapshot();

            var field = FindField();
            if (field != null)
            {
                yield return Select(field);
                State("field-selected");
                yield return Keys("keystroke", 'N', 'I');
                State("field-composing-keystroke");
                Candidates("keystroke");
                yield return Keys("keystroke-esc", (char)0x1B);
                State("field-after-esc");

                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
                yield return Frames(10);
                State("after-click-away");

                yield return Select(field);
                field.DeactivateInputField();
                yield return Frames(10);
                State("after-deactivate");

                yield return Select(field);
                field.gameObject.SetActive(false);
                yield return Frames(10);
                State("after-disable");
                field.gameObject.SetActive(true);
                yield return Frames(10);
                State("after-reenable");

                yield return Select(field);
                Post('N'); Post('I');
                yield return Frames(15);
                State("field-after-post");
                FieldGeometry(field);
                Candidates("post");
                field.DeactivateInputField();
                yield return Frames(10);

                yield return Select(field);
                ImmSet("ni");
                yield return null;
                State("field-after-immset-1");
                Candidates("immset-1");
                yield return Frames(15);
                State("field-after-immset");
                Candidates("immset");
                ImmCancel();
                field.DeactivateInputField();
                yield return Frames(10);
                State("after-immset-deactivate");
            }
            else Log("no text field found");

            Log($"SetFocus(0) -> {Hex(SetFocus(IntPtr.Zero))}");
            yield return Frames(15);
            State("focus-none");
            Log($"SetFocus(game) -> {Hex(SetFocus(game))}");
            yield return Frames(15);
            State("focus-back");
            yield return Keys("play-keys", 'W', 'A');
            State("after-play-keys");

            // A key the IME takes reaches the window as VK_PROCESSKEY with the real key's scan code.
            yield return ProcessKey("play", 'N', null);
            if (field != null)
            {
                yield return Select(field);
                yield return ProcessKey("field", '\b', field);
                field.DeactivateInputField();
                yield return Frames(5);
            }

            if (originalHkl != IntPtr.Zero) Log($"restore layout -> {Hex(ActivateKeyboardLayout(originalHkl, 0))}");
            State("restored");
            Log("done");
            yield return Frames(30);
            Application.Quit();
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        IEnumerator Select(TMPro.TMP_InputField field)
        {
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
            yield return Frames(10);
        }

        TMPro.TMP_InputField FindField()
        {
            TMPro.TMP_InputField pick = null;
            var sb = new StringBuilder("fields:");
            foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_InputField>())
            {
                if (f.gameObject.scene.name == null) continue;
                sb.Append($" [{f.GetType().Name} {Path(f.transform)} active={f.isActiveAndEnabled} interactable={f.interactable}]");
                if (pick == null && f.isActiveAndEnabled && f.interactable) pick = f;
            }
            Log(sb.ToString());
            if (BuildingGroupScreen.Instance != null && BuildingGroupScreen.Instance.inputField != null && BuildingGroupScreen.Instance.inputField.isActiveAndEnabled)
                pick = BuildingGroupScreen.Instance.inputField;
            Log("picked " + (pick != null ? Path(pick.transform) : "none"));
            return pick;
        }

        static string Path(Transform t)
        {
            var s = t.name;
            for (t = t.parent; t != null; t = t.parent) s = t.name + "/" + s;
            return s;
        }

        // ---------- state ----------

        void State(string tag)
        {
            var sb = new StringBuilder();
            sb.Append($"STATE {tag}: mode={Input.imeCompositionMode} comp=\"{Input.compositionString}\" cursor={Input.compositionCursorPos} imeSelected={Input.imeIsSelected}");
            var sel = UnityEngine.EventSystems.EventSystem.current != null ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
            sb.Append($" selected={(sel != null ? sel.name : "none")}");
            var focus = GetFocus();
            sb.Append($" focus={Hex(focus)}{(focus == game ? "(game)" : "")} active={Hex(GetActiveWindow())} game={Hex(game)} fgOurs={IsOurs(GetForegroundWindow())}");
            sb.Append($" hkl={Hex(GetKeyboardLayout(0))} userHkl={Hex(UserHkl())}");
            sb.Append(" imm(game)=" + Imm(game));
            if (focus != IntPtr.Zero && focus != game) sb.Append(" imm(focus)=" + Imm(focus));
            sb.Append(" tsf=" + Tsf());
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (GetGUIThreadInfo(GetCurrentThreadId(), ref gti))
                sb.Append($" caret={Hex(gti.hwndCaret)}@{gti.rcCaret.Left},{gti.rcCaret.Top},{gti.rcCaret.Right},{gti.rcCaret.Bottom} flags={gti.flags:X}");
            Log(sb.ToString());
            var user = UserHkl();
            if (userHklBefore != IntPtr.Zero && user != userHklBefore && IsOurs(GetForegroundWindow()) == false)
                Log($"SAFETY: the foreground thread's layout changed from {Hex(userHklBefore)} to {Hex(user)}");
        }

        static string Imm(IntPtr hwnd)
        {
            var himc = ImmGetContext(hwnd);
            var sb = new StringBuilder($"himc={Hex(himc)}");
            if (himc != IntPtr.Zero)
            {
                ImmGetConversionStatus(himc, out uint conv, out uint sent);
                sb.Append($",open={ImmGetOpenStatus(himc)},conv={conv:X},sent={sent:X}");
                var cf = new COMPOSITIONFORM();
                if (ImmGetCompositionWindow(himc, ref cf)) sb.Append($",compwin=style{cf.dwStyle:X}@{cf.x},{cf.y}");
                var cand = new CANDIDATEFORM();
                if (ImmGetCandidateWindow(himc, 0, ref cand)) sb.Append($",candwin=style{cand.dwStyle:X}@{cand.x},{cand.y}");
                int len = ImmGetCompositionStringW(himc, 8 /*GCS_COMPSTR*/, null, 0);
                if (len > 0)
                {
                    var buf = new byte[len];
                    ImmGetCompositionStringW(himc, 8, buf, len);
                    sb.Append($",compstr=\"{Encoding.Unicode.GetString(buf)}\"");
                }
                ImmReleaseContext(hwnd, himc);
            }
            sb.Append($",imewnd={Hex(ImmGetDefaultIMEWnd(hwnd))}");
            return sb.ToString();
        }

        string Tsf()
        {
            if (threadMgr == IntPtr.Zero)
            {
                TF_GetThreadMgr(out threadMgr);
                if (threadMgr != IntPtr.Zero) keystroke = Qi(threadMgr, IID_ITfKeystrokeMgr);
            }
            if (threadMgr == IntPtr.Zero) return "no-thread-mgr";
            var sb = new StringBuilder();
            var getFocus = Fn<PtrOutFn>(threadMgr, 7);
            getFocus(threadMgr, out var doc);
            sb.Append($"focusDoc={(doc != IntPtr.Zero ? "yes" : "none")}");
            if (doc != IntPtr.Zero) Release(doc);
            sb.Append($",open={Compartment(COMP_OPENCLOSE)},conv={Compartment(COMP_CONVERSION)}");
            if (keystroke != IntPtr.Zero)
            {
                foreach (char c in "WN")
                {
                    int hr = Fn<KeyFn>(keystroke, 6)(keystroke, (IntPtr)c, KeyLParam(c, false), out int eaten);
                    sb.Append($",eat{c}={(hr == 0 ? eaten.ToString() : "hr" + hr.ToString("X"))}");
                }
            }
            if (profileMgr != IntPtr.Zero)
            {
                var cat = TFCAT_TIP_KEYBOARD;
                int hr = Fn<GetActiveProfileFn>(profileMgr, 10)(profileMgr, ref cat, out var p);
                sb.Append(hr == 0 ? $",tip={p.langid:X4}:{(p.clsid == PinyinClsid ? "Pinyin" : p.clsid.ToString())},hklTip={Hex(p.hkl)}" : $",tip=hr{hr:X}");
            }
            return sb.ToString();
        }

        string Compartment(Guid g)
        {
            IntPtr mgr = IntPtr.Zero, comp = IntPtr.Zero, v = Marshal.AllocHGlobal(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(v, i, 0);
                if (Fn<PtrOutFn>(threadMgr, 13)(threadMgr, out mgr) != 0 || mgr == IntPtr.Zero) return "?";
                if (Fn<GetCompartmentFn>(mgr, 3)(mgr, ref g, out comp) != 0 || comp == IntPtr.Zero) return "?";
                if (Fn<GetValueFn>(comp, 4)(comp, v) != 0) return "?";
                ushort vt = (ushort)Marshal.ReadInt16(v);
                return vt == 3 ? Marshal.ReadInt32(v, 8).ToString("X") : "vt" + vt;
            }
            finally
            {
                if (comp != IntPtr.Zero) Release(comp);
                if (mgr != IntPtr.Zero) Release(mgr);
                Marshal.FreeHGlobal(v);
            }
        }

        // ---------- environment ----------

        void Env()
        {
            uint tid = GetCurrentThreadId();
            int hr = CoGetApartmentType(out int apt, out int qual);
            Log($"env: thread {tid}, apartment hr={hr:X} type={apt} qualifier={qual} (0 STA, 1 MTA, 3 main STA), managed {System.Threading.Thread.CurrentThread.GetApartmentState()}");
            var sb = new StringBuilder("thread windows:");
            long best = -1;
            EnumThreadWindows(tid, (h, _) =>
            {
                GetWindowRect(h, out var r);
                bool vis = IsWindowVisible(h);
                sb.Append($" [{Hex(h)} {Class(h)} vis={vis} {r.Right - r.Left}x{r.Bottom - r.Top} ex={GetWindowLongPtrW(h, -20).ToInt64():X}]");
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (vis && area > best) { best = area; game = h; }
                return true;
            }, IntPtr.Zero);
            Log(sb.ToString());
            Log($"game window {Hex(game)} {Class(game)} owned by thread {GetWindowThreadProcessId(game, out _)}");
            originalHkl = GetKeyboardLayout(0);
            userHklBefore = UserHkl();
            var list = new IntPtr[16];
            int n = GetKeyboardLayoutList(16, list);
            var l = new StringBuilder();
            for (int i = 0; i < n; i++) l.Append(" " + Hex(list[i]));
            Log($"layouts:{l} thread {Hex(originalHkl)} user foreground {Hex(userHklBefore)}");
            if (TF_CreateInputProcessorProfiles(out var profiles) == 0 && profiles != IntPtr.Zero)
            {
                profileMgr = Qi(profiles, IID_ITfInputProcessorProfileMgr);
                Release(profiles);
            }
            Log($"tsf: profileMgr {(profileMgr != IntPtr.Zero ? "yes" : "no")}");
            EnumWindows((h, _) => { if (IsWindowVisible(h)) baseline.Add(h); return true; }, IntPtr.Zero);
        }

        void ActivatePinyin()
        {
            var hkl = LoadKeyboardLayoutW("00000804", 0);
            Log($"LoadKeyboardLayout(00000804) -> {Hex(hkl)}");
            var prev = ActivateKeyboardLayout(hkl, 0); // this thread only
            Log($"ActivateKeyboardLayout(thread) -> prev {Hex(prev)}, now {Hex(GetKeyboardLayout(0))}, user foreground {Hex(UserHkl())}");
            if (profileMgr != IntPtr.Zero)
            {
                var c = PinyinClsid; var p = PinyinProfile;
                int hr = Fn<ActivateProfileFn>(profileMgr, 3)(profileMgr, 1, 0x804, ref c, ref p, IntPtr.Zero, 0);
                Log($"ActivateProfile(Pinyin, this thread) -> hr {hr:X}, user foreground {Hex(UserHkl())}");
            }
        }

        // ---------- input ----------

        static IntPtr KeyLParam(char vk, bool up)
        {
            uint scan = MapVirtualKeyW(vk, 0);
            uint l = 1u | (scan << 16);
            if (up) l |= 0xC0000000u;
            return (IntPtr)(long)l;
        }

        IEnumerator Keys(string route, params char[] keys)
        {
            foreach (var k in keys)
            {
                string r = "";
                if (keystroke != IntPtr.Zero)
                {
                    int hr = Fn<KeyFn>(keystroke, 8)(keystroke, (IntPtr)k, KeyLParam(k, false), out int eaten);
                    int hr2 = Fn<KeyFn>(keystroke, 9)(keystroke, (IntPtr)k, KeyLParam(k, true), out int eaten2);
                    r = $"down hr{hr:X} eaten={eaten} up hr{hr2:X} eaten={eaten2}";
                }
                Log($"key {route} {(int)k:X2}: {r}");
                yield return Frames(3);
            }
            yield return Frames(10);
        }

        IEnumerator ProcessKey(string route, char vk, TMPro.TMP_InputField field)
        {
            string before = field != null ? field.text : null;
            var l = KeyLParam(vk, false);
            bool a = PostMessageW(game, 0x100, (IntPtr)0xE5, l);
            bool seen = false; int frames = 0;
            for (; frames < 10 && !seen; frames++)
            {
                yield return null;
                seen = vk == 'N' ? Input.GetKey(KeyCode.N) || Input.GetKeyDown(KeyCode.N) : Input.GetKey(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Backspace);
            }
            PostMessageW(game, 0x101, (IntPtr)vk, KeyLParam(vk, true));
            yield return Frames(5);
            Log($"processkey {route} scan of {(int)vk:X2}: posted {a}, key seen {seen} (frames {frames}){(field != null ? $", text \"{before}\" -> \"{field.text}\"" : "")}");
        }

        static void FieldGeometry(TMPro.TMP_InputField f)
        {
            var corners = new Vector3[4];
            f.textComponent.rectTransform.GetWorldCorners(corners);
            var info = f.textComponent.textInfo;
            var sb = new StringBuilder($"field text \"{f.text}\" label \"{f.textComponent.text}\" caret {f.caretPosition} screen {Screen.width}x{Screen.height} text corners");
            foreach (var c in corners) sb.Append($" ({c.x:F1},{c.y:F1})");
            sb.Append($" anchored {f.textComponent.rectTransform.anchoredPosition} chars {info.characterCount}");
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                sb.Append($" [{ch.character} o{ch.origin:F1} a{ch.xAdvance:F1} asc{ch.ascender:F1} desc{ch.descender:F1}]");
            }
            Log(sb.ToString());
        }

        void Post(char k)
        {
            bool a = PostMessageW(game, 0x100, (IntPtr)k, KeyLParam(k, false));
            bool b = PostMessageW(game, 0x101, (IntPtr)k, KeyLParam(k, true));
            Log($"posted {(int)k:X2}: {a} {b}");
        }

        void ImmSet(string s)
        {
            var himc = ImmGetContext(game);
            if (himc == IntPtr.Zero) { Log("immset: no context"); return; }
            var bytes = Encoding.Unicode.GetBytes(s);
            bool ok = ImmSetCompositionStringW(himc, 9 /*SCS_SETSTR*/, bytes, bytes.Length, null, 0);
            Log($"ImmSetCompositionString(\"{s}\") -> {ok}");
            ImmReleaseContext(game, himc);
        }

        void ImmCancel()
        {
            var himc = ImmGetContext(game);
            if (himc == IntPtr.Zero) return;
            Log($"ImmNotifyIME(cancel) -> {ImmNotifyIME(himc, 0x15, 4, 0)}");
            ImmReleaseContext(game, himc);
        }

        void Snapshot() { baseline.Clear(); EnumWindows((h, _) => { if (IsWindowVisible(h)) baseline.Add(h); return true; }, IntPtr.Zero); }

        void Candidates(string route)
        {
            var sb = new StringBuilder($"windows new since baseline ({route}):");
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || baseline.Contains(h)) return true;
                GetWindowRect(h, out var r);
                uint wt = GetWindowThreadProcessId(h, out uint pid);
                sb.Append($" [{Hex(h)} {Class(h)} thread={(wt == GetCurrentThreadId() ? "main" : wt.ToString())} ours={pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id} {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} ex={GetWindowLongPtrW(h, -20).ToInt64():X} aboveGame={Above(h, game)}]");
                return true;
            }, IntPtr.Zero);
            Log(sb.ToString());
        }

        static bool Above(IntPtr h, IntPtr game)
        {
            for (var w = GetWindow(game, 3 /*GW_HWNDPREV*/); w != IntPtr.Zero; w = GetWindow(w, 3)) if (w == h) return true;
            return false;
        }

        static bool IsOurs(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        static IntPtr UserHkl()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            return GetKeyboardLayout(GetWindowThreadProcessId(fg, out _));
        }

        static string Class(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            return sb.ToString();
        }

        static string Hex(IntPtr p) => p.ToInt64().ToString("X");

        // ---------- COM by vtable ----------

        static T Fn<T>(IntPtr obj, int slot) where T : class
            => Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;

        static IntPtr Qi(IntPtr obj, Guid iid)
        {
            Fn<QiFn>(obj, 0)(obj, ref iid, out var result);
            return result;
        }

        static void Release(IntPtr obj) => Fn<ReleaseFn>(obj, 2)(obj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QiFn(IntPtr self, ref Guid iid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PtrOutFn(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int KeyFn(IntPtr self, IntPtr w, IntPtr l, out int eaten);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ActivateProfileFn(IntPtr self, uint type, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetActiveProfileFn(IntPtr self, ref Guid catid, out TfProfile profile);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetCompartmentFn(IntPtr self, ref Guid g, out IntPtr comp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetValueFn(IntPtr self, IntPtr variant);

        [StructLayout(LayoutKind.Sequential)]
        struct TfProfile { public uint type; public ushort langid; public Guid clsid; public Guid profile; public Guid catid; public IntPtr hklSubstitute; public uint caps; public IntPtr hkl; public uint flags; }
        [StructLayout(LayoutKind.Sequential)]
        struct GUITHREADINFO { public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct COMPOSITIONFORM { public uint dwStyle; public int x, y; public RECT rc; }
        [StructLayout(LayoutKind.Sequential)]
        struct CANDIDATEFORM { public uint dwIndex, dwStyle; public int x, y; public RECT rc; }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint tid, EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);
        [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[] list);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayoutW(string id, uint flags);
        [DllImport("user32.dll")] static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("ole32.dll")] static extern int CoGetApartmentType(out int type, out int qualifier);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr h);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr h, IntPtr himc);
        [DllImport("imm32.dll")] static extern bool ImmGetOpenStatus(IntPtr himc);
        [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr himc, out uint conv, out uint sent);
        [DllImport("imm32.dll")] static extern bool ImmGetCompositionWindow(IntPtr himc, ref COMPOSITIONFORM f);
        [DllImport("imm32.dll")] static extern bool ImmGetCandidateWindow(IntPtr himc, uint index, ref CANDIDATEFORM f);
        [DllImport("imm32.dll")] static extern int ImmGetCompositionStringW(IntPtr himc, uint index, byte[] buf, int len);
        [DllImport("imm32.dll")] static extern bool ImmSetCompositionStringW(IntPtr himc, uint index, byte[] comp, int compLen, byte[] read, int readLen);
        [DllImport("imm32.dll")] static extern bool ImmNotifyIME(IntPtr himc, uint action, uint index, uint value);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
        [DllImport("msctf.dll")] static extern int TF_GetThreadMgr(out IntPtr mgr);
        [DllImport("msctf.dll")] static extern int TF_CreateInputProcessorProfiles(out IntPtr profiles);
    }
}
