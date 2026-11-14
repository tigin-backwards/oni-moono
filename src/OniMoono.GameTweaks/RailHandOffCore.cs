namespace OniMoono.GameTweaks
{
    /// What direct-rail-bridges' hand-off needs of the rails (the game's SolidConduitFlow in the
    /// tweak, a fake in the unit tests). A handle is the rail's number for an item it carries.
    public interface IRailOps<TItem> where TItem : class
    {
        bool HasConduit(int cell);
        /// The handle of the item in `cell`, or -1 for none.
        int HandleAt(int cell);
        /// The item of a handle; null when it was destroyed.
        TItem ItemOf(int handle);
        /// Whether a direct move of this item out of `cell` leaves what the game's round trip would.
        bool Eligible(TItem item, int handle, int cell);
        /// The first half of the game's removal: the cell is emptied and the item's overlay look cleared.
        void Empty(int cell, int handle);
        /// The rest of the game's removal: the item is told it left storage and the handle is freed.
        void Unstore(TItem item, int handle);
        /// The item, still under `handle`, goes into `cell` with what the round trip would leave on it.
        void Place(int cell, TItem item, int handle);
        TItem GameRemove(int cell);
        void GameAdd(int cell, TItem item);
    }

    /// direct-rail-bridges' hand-off without game types (unit-tested). A rail bridge moving a whole
    /// item calls the rails' remove for its input cell and then, at once, their add of the same
    /// item for its output cell. Remove here takes the item off the input cell without telling
    /// it that it left storage, and remembers it; Add then puts the remembered item into the
    /// output cell under the handle it had. Anything else goes the game's way: an item that is
    /// not eligible, a destroyed one, an add of another item or of none remembered (the bridge's
    /// partial move), an output cell without a rail or with an item in it. A remembered item
    /// that is not placed is first unstored as the game's removal would have, so the game's add
    /// (or its drop into the world) finds it as it expects.
    public struct RailHandOff<TItem> where TItem : class
    {
        TItem pending;
        int pendingHandle;

        /// Moves made directly, and removals handed back to the game after being started.
        public long Direct, Undone;

        public bool Pending => pending != null;

        /// In place of the rails' remove of the bridge's input cell.
        public TItem Remove<TOps>(ref TOps ops, int cell) where TOps : struct, IRailOps<TItem>
        {
            Settle(ref ops);
            if (!ops.HasConduit(cell)) return ops.GameRemove(cell);
            int handle = ops.HandleAt(cell);
            if (handle < 0) return ops.GameRemove(cell);
            var item = ops.ItemOf(handle);
            if (item == null || !ops.Eligible(item, handle, cell)) return ops.GameRemove(cell);
            ops.Empty(cell, handle);
            pending = item;
            pendingHandle = handle;
            return item;
        }

        /// In place of the rails' add to the bridge's output cell.
        public void Add<TOps>(ref TOps ops, int cell, TItem item) where TOps : struct, IRailOps<TItem>
        {
            if (pending == null) { ops.GameAdd(cell, item); return; }
            if (!ReferenceEquals(pending, item) || !ops.HasConduit(cell) || ops.HandleAt(cell) >= 0)
            {
                Settle(ref ops);
                ops.GameAdd(cell, item);
                return;
            }
            int handle = pendingHandle;
            pending = null;
            ops.Place(cell, item, handle);
            Direct++;
        }

        /// A remembered item nothing placed is unstored as the game's removal would have.
        public void Settle<TOps>(ref TOps ops) where TOps : struct, IRailOps<TItem>
        {
            if (pending == null) return;
            var item = pending;
            pending = null;
            ops.Unstore(item, pendingHandle);
            Undone++;
        }
    }
}
