// ProbeKind as the probe is built against: Flip is a struct and Flop a class. The mod folder gets
// v2's build instead (../v2), where Flip is a class and Flop a struct, as the game's
// LogicValueChanged (U57) and Crop.CropVal (U60) went from struct to class for mods built
// before them (KindChange.cs).
using System;
using System.Collections.Generic;

namespace ProbeKind
{
    public struct Flip
    {
        public int A;
        public string S;
        public Flip(int a) { A = a; S = "s" + a; }
        public int Twice() => A * 2;
        public override string ToString() => "Flip" + A;
    }

    public class FlipHost
    {
        public static Flip Field = new Flip(3);
        public Flip Inst = new Flip(4);
        public static Flip[] Arr = { new Flip(7) };
        public static List<Flip> List = new List<Flip> { new Flip(6) };
        public static Flip Make(int a) => new Flip(a);
        public static object Boxed(int a) => new Flip(a);
        public static int Read(Flip f) => f.A;
        public static int ReadList(List<Flip> l) => l.Count;
        public static void Out(out Flip f) { f = new Flip(11); }
        public static int Ref(ref Flip f) => f.A;
        public static void Raise(Action<object> handler, int a) => handler(new Flip(a));
    }

    public class Flop
    {
        public int A;
        public Flop(int a) { A = a; }
    }

    public static class FlopHost
    {
        public static Flop Make(int a) => new Flop(a);
        public static int Read(Flop f) => f.A;
    }
}
