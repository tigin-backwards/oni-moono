using System;
using System.Linq;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Vector2, Vector3 and Mathf members evaluate as the original's Mono does with float32 off
    /// (D25): in double, rounded to float where a value is stored. Each case is one the original
    /// returned on UnityProbe's vecmath scenario where per-operation float rounding gives other
    /// bits (issue #66).
    public class MonoVectorMathTests
    {
        static float F(uint bits) => BitConverter.Int32BitsToSingle((int)bits);
        static string H(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
        static void Bits(string expected, float actual) => Assert.Equal(expected, H(actual));
        static void Bits(string expected, Vector2 actual) => Assert.Equal(expected, H(actual.x) + "," + H(actual.y));
        static void Bits(string expected, Vector3 actual) => Assert.Equal(expected, string.Join(",", new[] { actual.x, actual.y, actual.z }.Select(H)));

        [Fact]
        public void MembersMatchTheOriginalBitForBit()
        {
            { var a = new Vector2(F(0xc3321b33), F(0xc113308d)); var b = new Vector2(F(0xb8f9b54b), F(0xbdb08609)); Bits("433256d1", Vector2.Distance(a, b)); }
            { var a = new Vector2(F(0xbba383ca), F(0x4416d648)); var b = new Vector2(F(0x41e3ae52), F(0x4416d648)); Bits("48b1bf9b", Vector2.Dot(a, b)); }
            { var a = new Vector2(F(0xc17c7566), F(0x42304265)); Bits("423b37f9", a.magnitude); }
            { var a = new Vector2(F(0xc3321b33), F(0xc113308d)); var b = new Vector2(F(0xb8f9b54b), F(0xbdb08609)); float t = F(0x3f7375f4); Bits("c10b958d,bf08566d", Vector2.Lerp(a, b, t)); }
            { var a = new Vector2(F(0xc002e441), F(0x411f140c)); var b = new Vector2(F(0xba43f82e), F(0xbd6e8b59)); float d = F(0x411ecb5e); Bits("bd6b140a,3e60193e", Vector2.MoveTowards(a, b, d)); }
            { var a = new Vector2(F(0xc3321b33), F(0xc113308d)); float d = F(0x427e0345); Bits("c27dacb4,c051a3f7", Vector2.ClampMagnitude(a, d)); }
            { var a = new Vector2(F(0xc002e441), F(0x411f140c)); var b = new Vector2(F(0xba43f82e), F(0xbd6e8b59)); Bits("4327a40a", Vector2.Angle(a, b)); }
            { var a = new Vector2(F(0xbba383ca), F(0x4416d648)); var b = new Vector2(F(0x41e3ae52), F(0x4416d648)); Bits("cb9e15f0,cdd17607", Vector2.Reflect(a, b)); }
            { var a = new Vector3(F(0x40b44f03), F(0x3f5cb6b2), F(0xc3897e16)); var b = new Vector3(F(0x40b44f06), F(0x410164ea), F(0x423f2015)); Bits("43a16c72", Vector3.Distance(a, b)); }
            { var a = new Vector3(F(0x40c0fc85), F(0xb9576448), F(0x4440eb95)); var b = new Vector3(F(0x40c0fc71), F(0x3b3eaf2c), F(0x4440eb93)); Bits("c019d7f1,bbd906ea,3c99e572", Vector3.Cross(a, b)); }
            { var a = new Vector3(F(0xc0058aaf), F(0x44361222), F(0x3bbe08a8)); var b = new Vector3(F(0xc0058acd), F(0x3b83e3b1), F(0x3bbdbfb2)); Bits("c05f72ed,3bdcaef5,3c1ebf8b", Vector3.Project(a, b)); }
            { var a = new Vector3(F(0x3bec02b4), F(0x4438a06c), F(0xc4022717)); float d = F(0x425459fa); Bits("39dddd51,422d8f7c,c1f4b404", Vector3.ClampMagnitude(a, d)); }
            Bits("bfdac4c7", Mathf.Lerp(F(0x3a76eca2), F(0xc29d0de8), F(0x3cb264c3)));
            Bits("3f7beaed", Mathf.InverseLerp(F(0xc36586e3), F(0x40695092), F(0xbd94fe3d)));
        }
    }
}
