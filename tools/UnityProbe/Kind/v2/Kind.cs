// ProbeKind as the mod folder ships it: Flip is a class and Flop a struct, with the members v1
// has (KindChange.cs).
using System;
using System.Collections.Generic;

namespace ProbeKind
{
    public class Flip
    {
        public int A;
        public string S;
        public Flip() { }
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
        public static int Read(Flip f) => f == null ? -1 : f.A;
        public static int ReadList(List<Flip> l) => l.Count;
        public static void Out(out Flip f) { f = new Flip(11); }
        public static int Ref(ref Flip f) => f == null ? -1 : f.A;
        public static void Raise(Action<object> handler, int a) => handler(new Flip(a));
    }

    public struct Flop
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
