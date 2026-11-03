using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The GL work recorded as StateCommands (uploads, sampler changes, targets, viewports,
    /// clears) runs with the state each was recorded with, in the order posted, from a list per
    /// state type that the frame's arena hands out again once it is reset.
    public class StateCommandTests
    {
        [Fact]
        public void StateCommandsRunWithTheirOwnState()
        {
            var arena = new FrameArena();
            var calls = new List<string>();
            System.Action<Rect> viewport = r => calls.Add($"viewport {r.width}");
            System.Action<(bool Color, float Depth)> clear = c => calls.Add($"clear {c.Color} {c.Depth}");
            var posted = new List<System.Action>
            {
                arena.Command(viewport, new Rect(0f, 0f, 10f, 10f)),
                arena.Command(clear, (true, 1f)),
                arena.Command(viewport, new Rect(0f, 0f, 20f, 10f)),
                arena.Command(clear, (false, 0.5f)),
            };
            Assert.Equal(4, new HashSet<object>(posted.ConvertAll(p => p.Target!), ReferenceEqualityComparer.Instance).Count);
            foreach (var p in posted) p();
            Assert.Equal(new[] { "viewport 10", "clear True 1", "viewport 20", "clear False 0.5" }, calls);

            arena.Reset();
            calls.Clear();
            var again = arena.Command(clear, (true, 0.25f));
            Assert.Same(posted[1], again);
            again();
            Assert.Equal(new[] { "clear True 0.25" }, calls);
        }
    }
}
