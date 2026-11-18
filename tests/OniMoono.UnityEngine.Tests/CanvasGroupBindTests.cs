using OniMoono.Assets.TypeTrees;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class CanvasGroupBindTests : EngineTest
    {
        /// A saved CanvasGroup keeps its settings. Left at the defaults, the game's port icons
        /// (a group that neither blocks raycasts nor is interactable) caught the press that starts
        /// a pipe or wire drag.
        [Fact]
        public void SavedSettingsAreKept()
        {
            var group = new GameObject("g").AddComponent<CanvasGroup>();
            var f = new Fields();
            f.Add("m_Alpha", 0.25f);
            f.Add("m_Interactable", false);
            f.Add("m_BlocksRaycasts", false);
            f.Add("m_IgnoreParentGroups", true);
            group.EngineStateOrThrow.Bind(f, null);
            Assert.Equal(0.25f, group.alpha);
            Assert.False(group.interactable);
            Assert.False(group.blocksRaycasts);
            Assert.True(group.ignoreParentGroups);
        }
    }
}
