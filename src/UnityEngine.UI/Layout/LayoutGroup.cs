using System.Collections;
using System.Collections.Generic;
using UnityEngine.EventSystems;

namespace UnityEngine.UI
{
    /// Lays out its active child rects (those not ignoring layout) inside its padding, and
    /// reports the sizes that needs as a layout element.
    public abstract partial class LayoutGroup
    {
        [System.NonSerialized] RectTransform cachedRect;
        protected DrivenRectTransformTracker m_Tracker;
        Vector2 totalMin, totalPreferred, totalFlexible;
        [System.NonSerialized] readonly List<RectTransform> children = new();
        static readonly List<ILayoutIgnorer> ignorers = new();

        protected LayoutGroup()
        {
            m_Padding ??= new RectOffset();
        }

        public RectOffset padding
        {
            get => m_Padding;
            set => SetProperty(ref m_Padding, value);
        }

        public TextAnchor childAlignment
        {
            get => m_ChildAlignment;
            set => SetProperty(ref m_ChildAlignment, value);
        }

        protected RectTransform rectTransform => cachedRect ??= GetComponent<RectTransform>();

        protected List<RectTransform> rectChildren => children;

        /// Collects the children to lay out: active, and not ignored by every ILayoutIgnorer on
        /// them.
        public virtual void CalculateLayoutInputHorizontal()
        {
            children.Clear();
            var rt = rectTransform;
            for (int i = 0; i < rt.childCount; i++)
            {
                if (rt.GetChild(i) is not RectTransform child || !child.gameObject.activeInHierarchy) continue;
                child.GetComponents(ignorers);
                bool included = ignorers.Count == 0;
                foreach (var c in ignorers)
                    if (!c.ignoreLayout)
                    {
                        included = true;
                        break;
                    }
                ignorers.Clear();
                if (included) children.Add(child);
            }
            m_Tracker.Clear();
        }

        public virtual float minWidth => GetTotalMinSize(0);
        public virtual float preferredWidth => GetTotalPreferredSize(0);
        public virtual float flexibleWidth => GetTotalFlexibleSize(0);
        public virtual float minHeight => GetTotalMinSize(1);
        public virtual float preferredHeight => GetTotalPreferredSize(1);
        public virtual float flexibleHeight => GetTotalFlexibleSize(1);
        public virtual int layoutPriority => 0;

        protected override void OnEnable()
        {
            base.OnEnable();
            SetDirty();
        }

        protected override void OnDisable()
        {
            m_Tracker.Clear();
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            base.OnDisable();
        }

        protected override void OnDidApplyAnimationProperties() => SetDirty();

        protected float GetTotalMinSize(int axis)
        {
            if (LayoutRebuilder.Verifying) LayoutRebuilder.VerifyRead(rectTransform);
            return totalMin[axis];
        }

        protected float GetTotalPreferredSize(int axis)
        {
            if (LayoutRebuilder.Verifying) LayoutRebuilder.VerifyRead(rectTransform);
            return totalPreferred[axis];
        }

        protected float GetTotalFlexibleSize(int axis)
        {
            if (LayoutRebuilder.Verifying) LayoutRebuilder.VerifyRead(rectTransform);
            return totalFlexible[axis];
        }

        /// Where content of the given size starts on the axis: after the leading padding, plus
        /// the alignment's share of the space left over.
        protected float GetStartOffset(int axis, float requiredSpaceWithoutPadding)
        {
            float required = requiredSpaceWithoutPadding + (axis == 0 ? padding.horizontal : padding.vertical);
            float surplus = rectTransform.rect.size[axis] - required;
            return (axis == 0 ? padding.left : padding.top) + surplus * GetAlignmentOnAxis(axis);
        }

        /// 0, 0.5 or 1: left/centre/right horizontally, upper/middle/lower vertically.
        protected float GetAlignmentOnAxis(int axis)
            => axis == 0 ? (int)childAlignment % 3 * 0.5f : (int)childAlignment / 3 * 0.5f;

        protected void SetLayoutInputForAxis(float totalMin, float totalPreferred, float totalFlexible, int axis)
        {
            this.totalMin[axis] = totalMin;
            this.totalPreferred[axis] = totalPreferred;
            this.totalFlexible[axis] = totalFlexible;
        }

        protected void SetChildAlongAxis(RectTransform rect, int axis, float pos) => SetChildAlongAxisWithScale(rect, axis, pos, 1f);

        protected void SetChildAlongAxisWithScale(RectTransform rect, int axis, float pos, float scaleFactor)
        {
            if (rect == null) return;
            Place(rect, axis, pos, rect.sizeDelta[axis], scaleFactor, false);
        }

        protected void SetChildAlongAxis(RectTransform rect, int axis, float pos, float size) => SetChildAlongAxisWithScale(rect, axis, pos, size, 1f);

        protected void SetChildAlongAxisWithScale(RectTransform rect, int axis, float pos, float size, float scaleFactor)
        {
            if (rect == null) return;
            Place(rect, axis, pos, size, scaleFactor, true);
        }

        /// Anchors the child at the group's top left corner and puts its near edge `pos` in from
        /// the left (horizontal) or top (vertical); the scaled size places the pivot.
        void Place(RectTransform rect, int axis, float pos, float size, float scale, bool sized)
        {
            m_Tracker.Add(this, rect, DrivenTransformProperties.Anchors | (axis == 0
                ? DrivenTransformProperties.AnchoredPositionX | (sized ? DrivenTransformProperties.SizeDeltaX : 0)
                : DrivenTransformProperties.AnchoredPositionY | (sized ? DrivenTransformProperties.SizeDeltaY : 0)));
            rect.anchorMin = Vector2.up;
            rect.anchorMax = Vector2.up;
            if (sized)
            {
                var sizeDelta = rect.sizeDelta;
                sizeDelta[axis] = size;
                rect.sizeDelta = sizeDelta;
            }
            var position = rect.anchoredPosition;
            position[axis] = axis == 0
                ? pos + size * rect.pivot[axis] * scale
                : -pos - size * (1f - rect.pivot[axis]) * scale;
            rect.anchoredPosition = position;
        }

        /// No layout group above.
        bool isRootLayoutGroup
        {
            get
            {
                var parent = transform.parent;
                return parent == null || parent.GetComponent(typeof(ILayoutGroup)) == null;
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (isRootLayoutGroup) SetDirty();
        }

        protected virtual void OnTransformChildrenChanged() => SetDirty();

        protected void SetProperty<T>(ref T currentValue, T newValue)
        {
            if (currentValue == null && newValue == null || currentValue != null && currentValue.Equals(newValue)) return;
            currentValue = newValue;
            SetDirty();
        }

        /// Marks the layout for the next canvas update; during a layout rebuild, for the one after.
        protected void SetDirty()
        {
            if (!IsActive()) return;
            if (!CanvasUpdateRegistry.IsRebuildingLayout()) LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
            else StartCoroutine(MarkNextFrame(rectTransform));
        }

        IEnumerator MarkNextFrame(RectTransform rect)
        {
            yield return null;
            LayoutRebuilder.MarkLayoutForRebuild(rect);
        }
    }

    /// Lays out children in a row (horizontal) or column (vertical).
    public abstract partial class HorizontalOrVerticalLayoutGroup
    {
        protected HorizontalOrVerticalLayoutGroup()
        {
            m_ChildForceExpandWidth = true;
            m_ChildForceExpandHeight = true;
            m_ChildControlWidth = true;
            m_ChildControlHeight = true;
        }

        public float spacing { get => m_Spacing; set => SetProperty(ref m_Spacing, value); }
        public bool childForceExpandWidth { get => m_ChildForceExpandWidth; set => SetProperty(ref m_ChildForceExpandWidth, value); }
        public bool childForceExpandHeight { get => m_ChildForceExpandHeight; set => SetProperty(ref m_ChildForceExpandHeight, value); }
        public bool childControlWidth { get => m_ChildControlWidth; set => SetProperty(ref m_ChildControlWidth, value); }
        public bool childControlHeight { get => m_ChildControlHeight; set => SetProperty(ref m_ChildControlHeight, value); }
        public bool childScaleWidth { get => m_ChildScaleWidth; set => SetProperty(ref m_ChildScaleWidth, value); }
        public bool childScaleHeight { get => m_ChildScaleHeight; set => SetProperty(ref m_ChildScaleHeight, value); }
        public bool reverseArrangement { get => m_ReverseArrangement; set => SetProperty(ref m_ReverseArrangement, value); }

        /// A child's sizes on the axis: its layout sizes when the group controls them, else its
        /// current size (and no flexibility); force-expand makes it at least 1 flexible.
        void ChildSizes(RectTransform child, int axis, bool controlSize, bool forceExpand, out float min, out float preferred, out float flexible)
        {
            if (!controlSize)
            {
                min = child.sizeDelta[axis];
                preferred = min;
                flexible = 0f;
            }
            else
            {
                min = LayoutUtility.GetMinSize(child, axis);
                preferred = LayoutUtility.GetPreferredSize(child, axis);
                flexible = LayoutUtility.GetFlexibleSize(child, axis);
            }
            if (forceExpand) flexible = Mathf.Max(flexible, 1f);
        }

        bool Controls(int axis) => axis == 0 ? m_ChildControlWidth : m_ChildControlHeight;
        bool Scales(int axis) => axis == 0 ? m_ChildScaleWidth : m_ChildScaleHeight;
        bool Expands(int axis) => axis == 0 ? m_ChildForceExpandWidth : m_ChildForceExpandHeight;

        /// The group's sizes on the axis: along the layout direction the padding plus the
        /// children's sizes and the spacing between them; across it the padding plus the largest
        /// child.
        protected void CalcAlongAxis(int axis, bool isVertical)
        {
            float pad = axis == 0 ? padding.horizontal : padding.vertical;
            bool across = isVertical ^ (axis == 1);
            float min = pad, preferred = pad, flexible = 0f;
            var list = rectChildren;
            foreach (var child in list)
            {
                ChildSizes(child, axis, Controls(axis), Expands(axis), out float cMin, out float cPreferred, out float cFlexible);
                if (Scales(axis))
                {
                    float s = child.localScale[axis];
                    cMin *= s;
                    cPreferred *= s;
                    cFlexible *= s;
                }
                if (across)
                {
                    min = Mathf.Max(cMin + pad, min);
                    preferred = Mathf.Max(cPreferred + pad, preferred);
                    flexible = Mathf.Max(cFlexible, flexible);
                }
                else
                {
                    min += cMin + spacing;
                    preferred += cPreferred + spacing;
                    flexible += cFlexible;
                }
            }
            if (!across && list.Count > 0)
            {
                min -= spacing;
                preferred -= spacing;
            }
            preferred = Mathf.Max(min, preferred);
            SetLayoutInputForAxis(min, preferred, flexible, axis);
        }

        /// Sizes and places the children on the axis. Along the layout direction, space between
        /// the group's minimum and preferred sizes moves every child from its minimum towards its
        /// preferred size, and space beyond goes to the flexible children (or to the alignment
        /// when none is flexible). Across it, each child fills the content up to its preferred
        /// size (all of it when flexible) and is aligned.
        protected void SetChildrenAlongAxis(int axis, bool isVertical)
        {
            float size = rectTransform.rect.size[axis];
            bool control = Controls(axis), scale = Scales(axis), expand = Expands(axis);
            float alignment = GetAlignmentOnAxis(axis);
            bool across = isVertical ^ (axis == 1);
            var list = rectChildren;
            int count = list.Count;

            if (across)
            {
                float inner = size - (axis == 0 ? padding.horizontal : padding.vertical);
                for (int k = 0; k < count; k++)
                {
                    var child = list[m_ReverseArrangement ? count - 1 - k : k];
                    ChildSizes(child, axis, control, expand, out float cMin, out float cPreferred, out float cFlexible);
                    float s = scale ? child.localScale[axis] : 1f;
                    float space = Mathf.Clamp(inner, cMin, cFlexible > 0f ? size : cPreferred);
                    float start = GetStartOffset(axis, space * s);
                    if (control) SetChildAlongAxisWithScale(child, axis, start, space, s);
                    else SetChildAlongAxisWithScale(child, axis, start + (space - child.sizeDelta[axis]) * alignment, s);
                }
                return;
            }

            float pos = axis == 0 ? padding.left : padding.top;
            float perFlexible = 0f;
            float total = GetTotalPreferredSize(axis), totalMin = GetTotalMinSize(axis), totalFlex = GetTotalFlexibleSize(axis);
            float surplus = size - total;
            if (surplus > 0f)
            {
                if (totalFlex == 0f) pos = GetStartOffset(axis, total - (axis == 0 ? padding.horizontal : padding.vertical));
                else if (totalFlex > 0f) perFlexible = surplus / totalFlex;
            }
            float towardsPreferred = totalMin != total ? Mathf.Clamp01((size - totalMin) / (total - totalMin)) : 0f;
            for (int k = 0; k < count; k++)
            {
                var child = list[m_ReverseArrangement ? count - 1 - k : k];
                ChildSizes(child, axis, control, expand, out float cMin, out float cPreferred, out float cFlexible);
                float s = scale ? child.localScale[axis] : 1f;
                float childSize = Mathf.Lerp(cMin, cPreferred, towardsPreferred) + cFlexible * perFlexible;
                if (control) SetChildAlongAxisWithScale(child, axis, pos, childSize, s);
                else SetChildAlongAxisWithScale(child, axis, pos + (childSize - child.sizeDelta[axis]) * alignment, s);
                pos += childSize * s + spacing;
            }
        }
    }

    public partial class HorizontalLayoutGroup
    {
        protected HorizontalLayoutGroup() { }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            CalcAlongAxis(0, false);
        }

        public override void CalculateLayoutInputVertical() => CalcAlongAxis(1, false);
        public override void SetLayoutHorizontal() => SetChildrenAlongAxis(0, false);
        public override void SetLayoutVertical() => SetChildrenAlongAxis(1, false);
    }

    public partial class VerticalLayoutGroup
    {
        protected VerticalLayoutGroup() { }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            CalcAlongAxis(0, true);
        }

        public override void CalculateLayoutInputVertical() => CalcAlongAxis(1, true);
        public override void SetLayoutHorizontal() => SetChildrenAlongAxis(0, true);
        public override void SetLayoutVertical() => SetChildrenAlongAxis(1, true);
    }

    /// Lays out children in a grid of equal cells.
    public partial class GridLayoutGroup
    {
        /// Slack for float error when counting the cells that fit.
        const float Slack = 0.001f;

        protected GridLayoutGroup()
        {
            m_StartCorner = Corner.UpperLeft;
            m_StartAxis = Axis.Horizontal;
            m_CellSize = new Vector2(100f, 100f);
            m_Spacing = Vector2.zero;
            m_Constraint = Constraint.Flexible;
            m_ConstraintCount = 2;
        }

        public Corner startCorner { get => m_StartCorner; set => SetProperty(ref m_StartCorner, value); }
        public Axis startAxis { get => m_StartAxis; set => SetProperty(ref m_StartAxis, value); }
        public Vector2 cellSize { get => m_CellSize; set => SetProperty(ref m_CellSize, value); }
        public Vector2 spacing { get => m_Spacing; set => SetProperty(ref m_Spacing, value); }
        public Constraint constraint { get => m_Constraint; set => SetProperty(ref m_Constraint, value); }

        public int constraintCount
        {
            get => m_ConstraintCount;
            set => SetProperty(ref m_ConstraintCount, Mathf.Max(1, value));
        }

        int Count => rectChildren.Count;

        /// Lines needed for the children with `perLine` cells per line.
        static int Lines(int count, int perLine) => Mathf.CeilToInt(count / (float)perLine - Slack);

        /// Widths: the constraint's columns; flexible grids need one column and prefer a square.
        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            int minColumns, preferredColumns;
            if (m_Constraint == Constraint.FixedColumnCount) minColumns = preferredColumns = m_ConstraintCount;
            else if (m_Constraint == Constraint.FixedRowCount) minColumns = preferredColumns = Lines(Count, m_ConstraintCount);
            else
            {
                minColumns = 1;
                preferredColumns = Mathf.CeilToInt(Mathf.Sqrt(Count));
            }
            SetLayoutInputForAxis(
                padding.horizontal + (cellSize.x + spacing.x) * minColumns - spacing.x,
                padding.horizontal + (cellSize.x + spacing.x) * preferredColumns - spacing.x,
                -1f, 0);
        }

        /// Heights: the rows the children need at the constraint, or at as many columns as the
        /// width holds.
        public override void CalculateLayoutInputVertical()
        {
            int rows;
            if (m_Constraint == Constraint.FixedColumnCount) rows = Lines(Count, m_ConstraintCount);
            else if (m_Constraint == Constraint.FixedRowCount) rows = m_ConstraintCount;
            else
            {
                float width = rectTransform.rect.width;
                int columns = Mathf.Max(1, Mathf.FloorToInt((width - padding.horizontal + spacing.x + Slack) / (cellSize.x + spacing.x)));
                rows = Mathf.CeilToInt(Count / (float)columns);
            }
            float height = padding.vertical + (cellSize.y + spacing.y) * rows - spacing.y;
            SetLayoutInputForAxis(height, height, -1f, 1);
        }

        public override void SetLayoutHorizontal() => SetCells(0);

        public override void SetLayoutVertical() => SetCells(1);

        /// The horizontal pass sizes the cells; the vertical pass places them on both axes (the
        /// positions need the final height).
        void SetCells(int axis)
        {
            var list = rectChildren;
            int count = list.Count;
            if (axis == 0)
            {
                foreach (var child in list)
                {
                    m_Tracker.Add(this, child, DrivenTransformProperties.Anchors | DrivenTransformProperties.AnchoredPosition | DrivenTransformProperties.SizeDelta);
                    child.anchorMin = Vector2.up;
                    child.anchorMax = Vector2.up;
                    child.sizeDelta = cellSize;
                }
                return;
            }

            var size = rectTransform.rect.size;
            int columns = 1, rows = 1;
            if (m_Constraint == Constraint.FixedColumnCount)
            {
                columns = m_ConstraintCount;
                if (count > columns) rows = (count + columns - 1) / columns;
            }
            else if (m_Constraint == Constraint.FixedRowCount)
            {
                rows = m_ConstraintCount;
                if (count > rows) columns = (count + rows - 1) / rows;
            }
            else
            {
                columns = cellSize.x + spacing.x <= 0f ? int.MaxValue : Mathf.Max(1, Mathf.FloorToInt((size.x - padding.horizontal + spacing.x + Slack) / (cellSize.x + spacing.x)));
                rows = cellSize.y + spacing.y <= 0f ? int.MaxValue : Mathf.Max(1, Mathf.FloorToInt((size.y - padding.vertical + spacing.y + Slack) / (cellSize.y + spacing.y)));
            }

            bool fromRight = (int)m_StartCorner % 2 == 1;
            bool fromBottom = (int)m_StartCorner / 2 == 1;
            int perLine, usedColumns, usedRows;
            if (m_StartAxis == Axis.Horizontal)
            {
                perLine = columns;
                usedColumns = Mathf.Clamp(columns, 1, count);
                usedRows = Mathf.Clamp(rows, 1, Mathf.CeilToInt(count / (float)perLine));
            }
            else
            {
                perLine = rows;
                usedRows = Mathf.Clamp(rows, 1, count);
                usedColumns = Mathf.Clamp(columns, 1, Mathf.CeilToInt(count / (float)perLine));
            }

            var required = new Vector2(usedColumns * cellSize.x + (usedColumns - 1) * spacing.x, usedRows * cellSize.y + (usedRows - 1) * spacing.y);
            var start = new Vector2(GetStartOffset(0, required.x), GetStartOffset(1, required.y));
            for (int i = 0; i < count; i++)
            {
                int x, y;
                if (m_StartAxis == Axis.Horizontal) { x = i % perLine; y = i / perLine; }
                else { x = i / perLine; y = i % perLine; }
                if (fromRight) x = usedColumns - 1 - x;
                if (fromBottom) y = usedRows - 1 - y;
                SetChildAlongAxis(list[i], 0, start.x + (cellSize.x + spacing.x) * x, cellSize.x);
                SetChildAlongAxis(list[i], 1, start.y + (cellSize.y + spacing.y) * y, cellSize.y);
            }
        }
    }
}
