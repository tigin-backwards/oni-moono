#nullable enable
using System;
using System.Globalization;

namespace OniMoono.Host
{
    /// Tiered compilation's call-counting delay, for the log. OniMoono.Host.csproj sets the
    /// runtimeconfig knob System.Runtime.TieredCompilation.CallCountingDelayMs to 0 (decimal):
    /// methods tier up from their 30th call at once, not after the JIT has been idle for 100 ms,
    /// so a load's hot code is optimised before the colony's first big pass
    /// (docs/research/tiered-compilation.md). The runtime reads DOTNET_TC_CallCountingDelayMs
    /// (or COMPlus_TC_CallCountingDelayMs) first, as hexadecimal, so a user's variable wins
    /// (CoreCLR's Configuration::GetKnobDWORDValue).
    internal static class Tiering
    {
        public const string Knob = "System.Runtime.TieredCompilation.CallCountingDelayMs";
        public const int RuntimeDefault = 100;
        static readonly string[] Variables = { "DOTNET_TC_CallCountingDelayMs", "COMPlus_TC_CallCountingDelayMs" };

        /// The log line: the delay in effect and where it comes from. `knob` is the runtimeconfig
        /// property's value (AppContext.GetData), null when unset.
        public static string LogLine(Func<string, string?> getenv, object? knob)
        {
            string? fromConfig = knob == null ? null : Convert.ToString(knob, CultureInfo.InvariantCulture);
            foreach (var name in Variables)
            {
                if (getenv(name) is not { } value) continue;
                string ms = ParseHex(value) is uint v ? v.ToString(CultureInfo.InvariantCulture) : "unknown";
                return $"tiering: call-counting delay {ms} ms ({name}={value} in the environment, hexadecimal, wins over "
                       + (fromConfig != null ? $"the runtimeconfig's {fromConfig})" : "the runtime's default)");
            }
            return fromConfig != null ? $"tiering: call-counting delay {fromConfig} ms (the runtimeconfig)"
                : $"tiering: call-counting delay {RuntimeDefault} ms (the runtime's default)";
        }

        /// CLRConfig's reading of a DWORD variable: hexadecimal, with or without 0x.
        static uint? ParseHex(string value)
        {
            var s = value.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v) ? v : null;
        }
    }
}
