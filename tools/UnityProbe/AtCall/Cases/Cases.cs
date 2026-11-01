// The atcall probe's cases (AtCall.cs): what a method gives when a reference in it, inside a try
// of its own or not, names an assembly the mod folder does not ship (ProbeAbsent, ../Absent: its
// types, constructor, fields, a local and a struct), a member its type no longer has (ProbeGone
// v1's Keeper.Removed, run with Gone/v2), and System.Drawing's Bitmap, which Artificial Natural
// Tiles' TryLoadSprite builds from a PNG inside a try. Built against the original's own class
// libraries (Managed/), so its System.Drawing references are Mono's. Each case is a method of its
// own, called twice: what it returned or the exception's type, message, inner exceptions and
// first stack frame, whether its catch saw it, and whether any of its body ran.
using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using ProbeAbsent;
using ProbeGone;

namespace ProbeAtCallCases
{
    public static class Cases
    {
        static Action<string> L;

        static string Esc(string s) => s == null ? "null" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        static int ran;
        static int sink;
        static object box;

        static string Describe(Exception e)
        {
            var text = e.GetType().FullName + ": " + Esc(e.Message);
            for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
                text += " | inner " + inner.GetType().FullName + ": " + Esc(inner.Message);
            if (e is FileNotFoundException f) text += " | FileName " + Esc(f.FileName);
            if (e is TypeLoadException t) text += " | TypeName " + Esc(t.TypeName);
            return text;
        }

        static string Caught(Exception e) => "caught inside " + Describe(e);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentCallInTry()
        {
            ran++;
            try { return "" + Absent.Value(); }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentNewInTry()
        {
            ran++;
            try { box = new Absent(); return "new"; }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentStaticFieldInTry()
        {
            ran++;
            try { return "" + Absent.StaticField; }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentLocalInTry()
        {
            ran++;
            try
            {
                Absent a = new Absent();
                return "" + a.Instance() + a.Field;
            }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentBehindBranch(bool flag)
        {
            ran++;
            if (flag) sink = Absent.Value();
            return "ran past";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentIsInst(object o)
        {
            ran++;
            return "" + (o is Absent);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string AbsentValueLocal()
        {
            ran++;
            try
            {
                var v = new AbsentValue();
                v.X = 2;
                return "" + v.X;
            }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Inner() { ran++; return Absent.Value(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CallerCatches()
        {
            ran++;
            try { return "" + Inner(); }
            catch (Exception e) { return "caller caught " + Describe(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string MissingMemberInTry()
        {
            ran++;
            try { Keeper.Removed(); return "called"; }
            catch (Exception e) { return Caught(e); }
        }

        // A 1×1 PNG.
        static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string BitmapInTry()
        {
            ran++;
            try
            {
                using (var stream = new MemoryStream(Png))
                using (var bitmap = new Bitmap(stream))
                    return bitmap.Width + "x" + bitmap.Height;
            }
            catch (Exception e) { return Caught(e); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string RectangleValue()
        {
            ran++;
            var r = new Rectangle(1, 2, 3, 4);
            return r.Right + "," + r.Bottom;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string BitmapType() { ran++; return typeof(Bitmap).AssemblyQualifiedName; }

        static void Case(string name, Func<string> call)
        {
            for (int i = 1; i <= 2; i++)
            {
                ran = 0;
                try
                {
                    L(name + " #" + i + " returned " + Esc(call()) + " ran=" + ran);
                }
                catch (Exception e)
                {
                    var frame = (e.StackTrace ?? "").Split('\n')[0].Trim();
                    L(name + " #" + i + " threw " + Describe(e) + " ran=" + ran + " frame0 " + Esc(frame));
                }
            }
        }

        public static void Run(Action<string> log)
        {
            L = log;
            Case("AbsentCallInTry", AbsentCallInTry);
            Case("AbsentNewInTry", AbsentNewInTry);
            Case("AbsentStaticFieldInTry", AbsentStaticFieldInTry);
            Case("AbsentLocalInTry", AbsentLocalInTry);
            Case("AbsentBehindBranchFalse", () => AbsentBehindBranch(false));
            Case("AbsentBehindBranchTrue", () => AbsentBehindBranch(true));
            Case("AbsentIsInst", () => AbsentIsInst("x"));
            Case("AbsentValueLocal", AbsentValueLocal);
            Case("CallerCatches", CallerCatches);
            Case("MissingMemberInTry", MissingMemberInTry);
            Case("BitmapInTry", BitmapInTry);
            Case("RectangleValue", RectangleValue);
            Case("BitmapType", BitmapType);
        }
    }
}
