#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// fix-load-screen-clicks installs only where LoadScreen.AddColonyToList still adds its row
    /// button's click without clearing it first (LoadScreenClickBug): the game's U59 and U60
    /// have the bug; a method that clears each button before adding its click does not.
    public class LoadScreenClickBugTests
    {
        [Theory]
        [InlineData("oni-linux")] // U59 (744825)
        [InlineData("oni-linux-763023")] // U60
        public void TheGamesAddColonyToListHasTheBug(string build)
        {
            var managed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", build, "OxygenNotIncluded_Data", "Managed");
            if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll"))) Assert.Skip($"no game build at {managed}");
            var context = new GameContext(managed);
            try
            {
                var game = context.LoadFromAssemblyPath(Path.Combine(managed, "Assembly-CSharp.dll"));
                var add = game.GetType("LoadScreen", throwOnError: true)!.GetMethod("AddColonyToList", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var buttons = LoadScreenClickBug.Calls(add).Where(m => m.DeclaringType?.Name == "KButton").Select(m => m.Name).ToArray();
                // The cloud and local buttons are cleared before their handlers; the row's button is not.
                Assert.Equal(new[] { "ClearOnClick", "add_onClick", "ClearOnClick", "add_onClick", "add_onClick" }, buttons);
                Assert.Null(LoadScreenClickBug.Fixed(add));
            }
            finally
            {
                context.Unload();
            }
        }

        [Fact]
        public void AMethodThatClearsEachButtonFirstIsFixed()
        {
            Assert.Null(LoadScreenClickBug.Fixed(Method(nameof(Buggy))));
            Assert.Equal("clears each button before adding its click", LoadScreenClickBug.Fixed(Method(nameof(Cleared))));
            Assert.Equal("clears each button before adding its click", LoadScreenClickBug.Fixed(Method(nameof(Removed))));
            Assert.Equal("adds no click to a button", LoadScreenClickBug.Fixed(Method(nameof(NoClick))));
        }

        static MethodInfo Method(string name) => typeof(LoadScreenClickBugTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

        /// Stands in for the game's KButton (matched by name).
        sealed class KButton
        {
            public event Action? onClick;
            public void ClearOnClick() => onClick = null;
            public void SignalClick() => onClick?.Invoke();
        }

        static void Show() { }

        // The game's shape: two buttons cleared first, the row's not.
        static void Buggy(KButton cloud, KButton local, KButton row)
        {
            cloud.ClearOnClick();
            cloud.onClick += Show;
            local.ClearOnClick();
            local.onClick += Show;
            row.onClick += Show;
        }

        // As a Klei fix may do it.
        static void Cleared(KButton cloud, KButton local, KButton row)
        {
            cloud.ClearOnClick();
            cloud.onClick += Show;
            local.ClearOnClick();
            local.onClick += Show;
            row.ClearOnClick();
            row.onClick += Show;
        }

        static void Removed(KButton row)
        {
            row.onClick -= Show;
            row.onClick += Show;
        }

        static void NoClick(KButton row) => row.ClearOnClick();

        /// The game's assemblies from its Managed folder, apart from the test's own; the
        /// framework's (mscorlib, System.*) from the runtime.
        sealed class GameContext(string managed) : AssemblyLoadContext("game " + managed, isCollectible: true)
        {
            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name is null || name.Name is "mscorlib" or "netstandard" || name.Name.StartsWith("System", StringComparison.Ordinal)) return null;
                var path = Path.Combine(managed, name.Name + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
