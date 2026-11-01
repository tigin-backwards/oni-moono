// ProbeAbsent: an assembly the atcall probe's cases (../Cases) are built against and that the
// mod folder never ships, as Artificial Natural Tiles names System.Drawing.Common, which our
// player cannot load.
namespace ProbeAbsent
{
    public class Absent
    {
        public static int StaticField = 4;
        public int Field = 5;

        public Absent() { }

        public static int Value() => 3;

        public int Instance() => 6;
    }

    public struct AbsentValue
    {
        public int X;
    }
}
