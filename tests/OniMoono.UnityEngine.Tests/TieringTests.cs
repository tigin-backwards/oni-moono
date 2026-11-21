using System;
using System.Linq;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// Tiered compilation's call-counting delay in the log (src/OniMoono.Host/Tiering.cs,
    /// compiled in here).
    public class TieringTests
    {
        static Func<string, string> Env(params (string Name, string Value)[] variables)
            => name => variables.FirstOrDefault(v => v.Name == name).Value;

        [Fact]
        public void TheRuntimeconfigsValueIsLogged()
            => Assert.Equal("tiering: call-counting delay 0 ms (the runtimeconfig)", Tiering.LogLine(Env(), "0"));

        [Fact]
        public void WithoutTheKnobTheRuntimesDefaultApplies()
            => Assert.Equal("tiering: call-counting delay 100 ms (the runtime's default)", Tiering.LogLine(Env(), null));

        [Theory]
        // The runtime reads the variable as hexadecimal: 64 is 100 ms, 20 is 32 ms.
        [InlineData("DOTNET_TC_CallCountingDelayMs", "64", "100")]
        [InlineData("DOTNET_TC_CallCountingDelayMs", "0x14", "20")]
        [InlineData("COMPlus_TC_CallCountingDelayMs", "20", "32")]
        [InlineData("DOTNET_TC_CallCountingDelayMs", "fast", "unknown")]
        public void TheUsersVariableWinsAndIsReadAsHex(string name, string value, string ms)
            => Assert.Equal($"tiering: call-counting delay {ms} ms ({name}={value} in the environment, hexadecimal, wins over the runtimeconfig's 0)",
                Tiering.LogLine(Env((name, value)), "0"));
    }
}
