# uGUI (UnityEngine.UI): functional specification

This project's replacement of Unity's uGUI package. It goes in the assembly `UnityEngine.UI`
(`src/UnityEngine.UI`), with the namespaces `UnityEngine.UI` and `UnityEngine.EventSystems`.
The game builds every menu, panel and HUD element with it. The rules are in
`docs/cleanroom/README.md`, and PLAN M9 describes the stage.

## Interface

**Generated shapes:** `src/UnityEngine.UI/Generated/` holds everything that is fixed:
- the type declarations, with their bases and interfaces;
- the enums, interfaces and delegates;
- the serialized fields of every class and struct.

`scripts/regen-packages.sh` regenerates these from the shipped package's metadata. Do not edit
them. Write the rest in partial declarations next to them.

**Serialized fields:** the game's data serializes components by field order and type, through
the engine's serializer. The generated fields must therefore stay as they are. Field
initializers are not part of the metadata. A component added at run time (`AddComponent`)
starts with Unity's documented defaults, so give each field the default the documentation
states. Where the documentation is silent, measure it: add the component in a run that uses
the shipped package and read its fields through public members.

**Required members:** `out/surface/package-members.tsv` (from `scripts/regen-packages.sh`) lists
every member the game's assemblies use:
- `call`: the game calls the member;
- `override`: the game's classes override the member, so the replacement must call it at the
  moment Unity does.

`scripts/build-packages.sh` runs ApiCheck against the game's references. It must report
nothing missing.

**The game's subclasses:**
- `UIBehaviour`: two classes.
- `PointerInputModule`: Klei's input module overrides `Process`, `UpdateModule`,
  `ShouldActivateModule`, `IsModuleSupported`, `ActivateModule`, `DeactivateModule` and
  `GetMousePointerEventData`, and it calls the protected helpers of `PointerInputModule` and
  `BaseInputModule`.
- Graphics: `Graphic`, `MaskableGraphic` and `Image` (KImage). They override `OnPopulateMesh`,
  `mainTexture`, `SetVerticesDirty` and `SetMaterialDirty`.
- `LayoutElement` and `AspectRatioFitter`: two classes each.
- `ScrollRect`: overrides `LateUpdate`.
- `Slider`.
- `Toggle`: two classes.

These classes rely on the protected and virtual members of their bases, and on the order of the
UIBehaviour messages described below.

**The engine:** the engine's API is this project's own code (`src/OniMoono.UnityEngine`). The
engine grants `UnityEngine.UI` access to its internals. The parts uGUI builds on:
- `Canvas`, `CanvasRenderer`, `CanvasGroup` and `RectTransform`;
- `RectTransformUtility`;
- `Canvas.willRenderCanvases` and `Canvas.ForceUpdateCanvases`;
- the `ICanvasRaycastFilter` and `ICanvasElement` contracts;
- `Input`, `Event` and `Physics`/`Physics2D` raycasts;
- `Sprite`, `DataUtility` and `Font`;
- the legacy `TextGenerator`.

**UI Toolkit:** the shipped package also has a bridge to UI Toolkit (`UIToolkitInteroperabilityBridge`,
`PanelEventHandler`, `PanelRaycaster`). The game has no UI Toolkit content. Keep only what the
generated shapes require: the bridge's serialized field on `EventSystem`. It does nothing.

## Behaviour

These follow Unity's uGUI documentation, the 1.0 and 2.0 manuals and the scripting reference.
Each area below states what the game relies on and what is easy to get wrong.

### UIBehaviour messages

`UIBehaviour` is a `MonoBehaviour` with protected virtual `Awake`, `OnEnable`, `Start`,
`OnDisable`, `OnDestroy`, `OnValidate` and `Reset`, plus these:
- `OnRectTransformDimensionsChange`, `OnBeforeTransformParentChanged`,
  `OnTransformParentChanged`, `OnDidApplyAnimationProperties`,
  `OnCanvasGroupChanged` and `OnCanvasHierarchyChanged`. The engine sends these messages
  (`Engine/`; search for the message names).
- `IsActive()`, which is `isActiveAndEnabled`.
- `IsDestroyed()`.

Subclasses call the base implementations, and the replacement's own subclasses must do the
same.

### Canvas update cycle (`CanvasUpdateRegistry`, `ICanvasElement`, `CanvasUpdate`)

**When it runs:** the registry subscribes to `Canvas.willRenderCanvases`, which the engine
raises once per frame before canvases render, and on `Canvas.ForceUpdateCanvases`.

**One pass:**
1. Elements that are destroyed or inactive are dropped from both queues.
2. **Layout rebuild:** the layout queue is sorted by hierarchy depth, parents first: depth is the
   number of ancestors. Each element's `Rebuild` is then called for `Prelayout`, `Layout` and
   `PostLayout` in turn; each phase runs over the whole queue before the next phase starts.
   `LayoutComplete` follows for each element.
3. **Clipping:** `ClipperRegistry.Cull()` runs, so every registered `IClipper` performs its
   clipping.
4. **Graphic rebuild:** the graphic queue's elements get `Rebuild` for `PreRender` and then
   `LatePreRender`, followed by `GraphicUpdateComplete`.
5. Both queues are cleared.

**Registering:**
- `RegisterCanvasElementForLayoutRebuild` and `…ForGraphicRebuild` add an element once.
- `TryRegister…` returns false when the registry is in the middle of that kind of rebuild; the
  plain forms log an error instead.
- Registering during the layout phase for a graphic rebuild is allowed and takes effect in the
  same pass.
- `UnRegisterCanvasElementForRebuild` and `DisableCanvasElementForRebuild` behave alike
  (measured on the shipped package): remove the element from the layout queue and call its
  `LayoutComplete`, then remove it from the graphic queue and call its `GraphicUpdateComplete`,
  whichever queues held it (also for an element that was never registered).
- During a rebuild, the half for the queue being rebuilt is skipped with the error "Trying to
  remove X from rebuild list while we are already inside a rebuild loop. This is not
  supported."; the element is still rebuilt in that pass. The other half still happens.
- `IsRebuildingLayout()` and `IsRebuildingGraphics()` report the current phase.

### Graphic

`Graphic` is the base of everything drawn. It requires `CanvasRenderer` and `RectTransform`,
and declares `ICanvasElement`.

**Dirtying:**
- `SetAllDirty` sets layout, vertices and material dirty.
- `SetLayoutDirty` marks the layout for rebuild, through `LayoutRebuilder.MarkLayoutForRebuild`
  on the rect transform, and invokes the layout-dirty callbacks.
- `SetVerticesDirty` and `SetMaterialDirty` register the graphic for a graphic rebuild and
  invoke their callbacks.
- The `Register`/`UnregisterDirty…Callback` methods manage the callbacks.

**Rebuild(PreRender):**
- With vertices dirty, `UpdateGeometry` runs: a `VertexHelper` is cleared and filled by
  `OnPopulateMesh(VertexHelper)`. The default implementation fills one quad covering the
  rect's pixel-adjusted rectangle, in `color`. Then each `IMeshModifier` component on the
  GameObject, in component order, gets `ModifyMesh`. The result is set on the
  `CanvasRenderer` (`SetMesh`), from a shared work mesh.
- With material dirty, `UpdateMaterial` sets the canvas renderer's material count to 1. It sets
  `materialForRendering` (the `IMaterialModifier` components on the GameObject applied in
  order to `material`) and the texture `mainTexture`.

**Lifecycle:**
- `OnEnable` registers the graphic with its canvas (`GraphicRegistry`), cuts off the canvas
  renderer's culling, and dirties everything.
- `OnDisable` unregisters it, clears the canvas renderer (`Clear`), and notifies the layout
  (`LayoutRebuilder.MarkLayoutForRebuild`).
- `OnRectTransformDimensionsChange` dirties the vertices, and with a pending layout rebuild
  the layout as well.
- Parent and canvas changes re-register the graphic and dirty everything.

**Properties:**
- `color`: changing it dirties the vertices.
- `raycastTarget` and `raycastPadding`.
- `material`: the default is `defaultGraphicMaterial`, the engine's default UI material.
- `canvas`: the nearest enabled `Canvas` up the hierarchy.
- `depth`: `canvasRenderer.absoluteDepth`, unchanged. The numbering is the engine's canvas
  batching (`Engine/UI/CanvasSystem.cs`), not this package's; it follows the original's as
  UnityProbe measured it (`UIS depth`), with either package.
- `defaultMaterial`, `mainTexture`, `rectTransform`, `canvasRenderer`.

**Methods:**
- `GetPixelAdjustedRect()`: the rect snapped to pixels when the canvas is pixel-perfect,
  through `RectTransformUtility.PixelAdjustRect`; otherwise the rect.
- `PixelAdjustPoint`.
- `Raycast(Vector2 sp, Camera eventCamera)`: walks up the hierarchy, from this object to the
  root. Every enabled `ICanvasRaycastFilter` component found on the way must accept the point.
  Above a `CanvasGroup` with `ignoreParentGroups`, further `CanvasGroup`s are skipped (other
  filters still count), and a `CanvasGroup` that does not block raycasts fails it.
- `CrossFadeColor(target, duration, ignoreTimeScale, useAlpha[, useRGB])` tweens the canvas
  renderer's colour, not `color`. A duration of 0 applies the colour at once. A tween advances
  every frame while the graphic is active and stops when it is disabled. Unity runs it as a
  coroutine on the graphic, and a replacement may do the same. A new tween replaces a running
  one.
- `CrossFadeAlpha(alpha, duration, ignoreTimeScale)` likewise.
- `SetNativeSize` is virtual.
- `OnCullingChanged` re-dirties the vertices when the element becomes visible again.

**`MaskableGraphic`** adds `maskable` and `isMaskingGraphic`, and `onCullStateChanged` (a
`UnityEvent<bool>`). It implements `IClippable` and `IMaterialModifier`:
- **Stencil:** `GetModifiedMaterial` wraps the material in a stencil material
  (`StencilMaterial.Add`). The stencil value is the number of enabled `Mask` components above
  the graphic, up to its canvas. The graphic's own mask, when it is the masking graphic,
  counts separately.
- **Clipping:** `RecalculateClipping` registers the graphic with the nearest `RectMask2D`
  parent.
- **Culling:** `Cull(clipRect, validRect)` sets the canvas renderer's `cull` when the
  graphic's canvas-space rect does not overlap the clip rect.
- **`SetClipRect`** and **`SetClipSoftness`** pass the clip to the canvas renderer.

### VertexHelper

A builder of UI meshes:
- **Building:** `AddVert` (as a `UIVertex` or as its parts), `AddTriangle`, `AddUIVertexQuad`,
  `AddUIVertexStream` and `AddUIVertexTriangleStream`.
- **Access:** `PopulateUIVertex`, `SetUIVertex`, `GetUIVertexStream` (as a triangle list) and
  `FillMesh(Mesh)`.
- **Counts and housekeeping:** `currentVertCount`, `currentIndexCount`, `Clear` and `Dispose`.

The channels are position, colour, uv0 to uv3, normal and tangent. A vertex added without
them gets `UIVertex.simpleVert`'s defaults: normal (0, 0, −1), tangent (1, 0, 0, −1).

### Image

**Sprite and material:**
- `sprite` and `overrideSprite`: the override wins when it is set. `activeSprite` is the one in
  use.
- With no sprite, the image draws the engine's white texture.
- `mainTexture` is the active sprite's texture, or its alpha texture through `material` as
  Unity does.

**Types:**
- **Simple:** one quad, over the pixel-adjusted rect. With `preserveAspect`, it is fitted
  inside the rect at the sprite's aspect, centred by the pivot. `useSpriteMesh` uses the
  sprite's own mesh.
- **Sliced:** nine quads, split by the sprite's border scaled by `pixelsPerUnit`. When the rect
  is smaller than the borders, the borders shrink proportionally. With `fillCenter` false
  there is no centre quad. A sprite without a border draws as Simple.
- **Tiled:** the centre repeats at the sprite's size divided by `pixelsPerUnit`. A sprite whose
  texture wraps, without a border and not packed, uses one quad with repeating UVs; otherwise
  one quad per tile, clipped at the edges. Borders behave as in Sliced. The engine limits the
  quad count, which Unity documents as 65000 vertices.
- **Filled:** `fillMethod` (Horizontal, Vertical, Radial90, Radial180, Radial360),
  `fillOrigin`, `fillAmount` (0 to 1) and `fillClockwise`. The fill cuts the quad, or for
  radial fills sweeps it, as documented. With `fillAmount` 0, nothing is drawn.

**Other members:**
- `pixelsPerUnitMultiplier` scales the sprite's pixels per unit relative to the canvas's
  `referencePixelsPerUnit`.
- `alphaHitTestMinimumThreshold` (0 by default): above 0, `IsRaycastLocationValid` samples the
  sprite's texture alpha at the point, which needs a readable texture, and the raycast passes
  only at or above the threshold.
- `SetNativeSize` sizes the rect to the sprite's pixel size divided by `pixelsPerUnit`.
- `ILayoutElement`: the preferred size is the sprite's size, or for Sliced and Tiled its border
  size; the minimum size is 0; flexible is -1.

### RawImage, Text, Shadow and Outline

- **RawImage:**
  - `texture`, defaulting to white, and `uvRect`.
  - One quad, with UVs from `uvRect`.
  - `SetNativeSize` uses the texture's size times `uvRect`'s size.
- **Text (legacy):**
  - It lays out its `text` with the engine's `TextGenerator`, using `GetGenerationSettings`
    for the rect.
  - `cachedTextGenerator` is used for the mesh, and `cachedTextGeneratorForLayout` for the
    `ILayoutElement` preferred sizes.
  - The vertices are offset by the rounding offset of the first vertex when the canvas is
    pixel-perfect.
  - A font texture rebuild re-dirties every Text that uses the font
    (`FontUpdateTracker` on `Font.textureRebuilt`).
  - `fontData` (a `FontData`) holds the settings.
  - The face, glyph metrics, kerning, pixel size, line metrics and alignment rounding of the
    engine's generator: "Legacy Text glyph placement" below.
- **Shadow and Outline** (`BaseMeshEffect`): `Shadow` adds a copy of the vertices at
  `effectDistance` in `effectColor`, with `useGraphicAlpha`. `Outline` adds four copies, at
  (±x, ±y). The copies are drawn first, behind the original.

### Masking

- **`Mask`** (stencil): with `showMaskGraphic`, the mask's own graphic is drawn. Its graphic
  writes the stencil, and children test it. It works through `GetModifiedMaterial`, the
  stencil material, and the canvas renderer's pop material for unwinding the stencil. It
  needs the GameObject's Graphic.
- **`RectMask2D`** (clipping without stencil):
  - **Clip rect:** its clippables (MaskableGraphics) below it are clipped to the intersection
    of its rect, shrunk by `padding`, with all enclosing RectMask2Ds' rects, all in canvas
    space.
  - **Softness:** `softness` gives the clip a soft edge.
  - **Culling:** elements wholly outside the clip are culled: an `Image` by its rect, a
    TextMeshPro text by its mesh's bounds, with touching edges counting as outside ("RectMask2D
    culling (measured)" below).
  - **When:** it registers with `ClipperRegistry` and recomputes in `PerformClipping` each
    pass. When an element moves or the rect changes, the clip and cull state is updated in that
    pass.

### Layout

Layout is controlled by the layout controllers `ILayoutController`, `ILayoutSelfController`
and `ILayoutGroup`, and by the layout elements (`ILayoutElement`), as the Auto Layout section of
Unity's manual describes.

**`LayoutRebuilder`:**
- `MarkLayoutForRebuild(rect)` finds the root of the layout: the topmost consecutive ancestor
  whose GameObject has an enabled `ILayoutGroup`, stopping at the first one without. That
  root is registered as a rebuild for the next pass.
- **The rebuild:**
  - Widths: `CalculateLayoutInputHorizontal` on every `ILayoutElement` of the subtree,
    children before parents. Then `SetLayoutHorizontal` on the controllers, parents before
    children; self controllers (`ILayoutSelfController`) before the group on the same
    GameObject.
  - Heights: the same, vertically.
- `ForceRebuildLayoutImmediate(rect)` does a rebuild now.

**`LayoutUtility`:**
- `GetMinSize`, `GetPreferredSize`, `GetFlexibleSize` and their per-axis forms, with
  `GetLayoutProperty`.
- Over all enabled `ILayoutElement` components on the GameObject: the one with the highest
  `layoutPriority` wins, and a value below 0 means "not set". Equal priorities take the
  largest value.
- The preferred size is at least the minimum size.

**`LayoutElement`:**
- `ignoreLayout`, `minWidth`/`minHeight`, `preferredWidth`/`preferredHeight` and
  `flexibleWidth`/`flexibleHeight`, all -1 when not set.
- `layoutPriority` is 1 by default.

**Layout groups** (`HorizontalLayoutGroup`, `VerticalLayoutGroup`, `GridLayoutGroup`):

| Setting | Meaning |
|---|---|
| `padding` (RectOffset) | inset of the content |
| `childAlignment` (TextAnchor) | where the children sit when they do not fill the group |
| `spacing` | gap between children |
| `childControlWidth`/`childControlHeight` | the group sets the children's sizes |
| `childForceExpandWidth`/`childForceExpandHeight` | children share the remaining space even without flexible sizes |
| `childScaleWidth`/`childScaleHeight` | children's scale counts in their size |
| `reverseArrangement` | children are placed in reverse order |

- **Children:** the active children whose rect transforms have no `LayoutElement` with
  `ignoreLayout`.
- **Along the axis:**
  - The group's own sizes:
    - the minimum is the padding plus the sum of the children's minimums plus the spacing;
    - the preferred size is the same, with the children's preferred sizes;
    - the flexible size is the sum of the children's flexible sizes.
  - Space between the group's minimum and preferred sizes interpolates every child between
    its minimum and preferred size.
  - Space beyond the preferred size goes to children in proportion to their flexible sizes,
    with force-expand counting each child's flexible size as at least 1.
  - Children that the group does not control keep their size and are only positioned.
- **Across the axis:** each child gets its size clamped to the group's content size: its
  flexible size takes the rest. Its position follows the alignment.
- **Positions:**
  - The space left over goes to the alignment: left, centre or right along the axis,
    `(1 − alignment) × 0` and so on, as documented.
  - Children are placed at their pivot through `SetInsetAndSizeFromParentEdge`. Unity drives
    their rect transforms (`DrivenRectTransformTracker`).
- **`GridLayoutGroup`:**
  - `cellSize`, `spacing`, `startCorner`, `startAxis`, and `constraint` with `constraintCount`
    (Flexible, FixedColumnCount, FixedRowCount).
  - Cells are laid out from the start corner along the start axis.
  - The cell count per row or column comes from the constraint, or from the width or height
    available.
  - The children's sizes are set to the cell size.
- **Re-layout:** `LayoutGroup` re-lays out when its children, their active state, its own rect
  or its settings change, in that frame's pass. `SetDirty()` re-lays out in the next pass when
  called during a rebuild. Moving a child or the group (position or scale only) re-lays out
  nothing; the child keeps its place until the next re-layout ("Moved children of layout groups
  (measured)" below).

**`ContentSizeFitter`:** `horizontalFit` and `verticalFit`, each Unconstrained, MinSize or
PreferredSize. It sets the rect's size along the axis to the GameObject's layout minimum or
preferred size, keeping the pivot.

**`AspectRatioFitter`:** `aspectMode` and `aspectRatio`.

| Mode | Behaviour |
|---|---|
| None | does nothing |
| WidthControlsHeight | height = width / ratio |
| HeightControlsWidth | width = height × ratio |
| FitInParent | as large as fits in the parent at that ratio, anchors stretched, centred |
| EnvelopeParent | as small as covers the parent at that ratio, anchors stretched, centred |

It updates when its rect or its parent's rect changes, and in the update after it is enabled.

**`CanvasScaler`:**
- `uiScaleMode`:
  - ConstantPixelSize: `scaleFactor`.
  - ScaleWithScreenSize: `referenceResolution`, `screenMatchMode` (MatchWidthOrHeight with
    `matchWidthOrHeight`, which blends the log₂ scales of width and height, Expand, Shrink).
  - ConstantPhysicalSize: `physicalUnit`, `fallbackScreenDPI`, `defaultSpriteDPI`.
- `referencePixelsPerUnit`.
- It sets `Canvas.scaleFactor` and `referencePixelsPerUnit` every update.
- It only acts on a root canvas in screen space; world-space canvases use
  `dynamicPixelsPerUnit`.

### Event system

**`EventSystem`:**
- **Current system:** `current` is the most recently enabled EventSystem; the others stay
  registered.
- **Each Update:**
  - Every `BaseInputModule` on the GameObject gets `UpdateModule`.
  - The first module, in component order, whose `ShouldActivateModule()` is true becomes
    current. Switching modules calls `DeactivateModule` on the old one and `ActivateModule` on
    the new one.
  - The current module's `Process()` then runs. Only the current system processes.
- **Selection:**
  - `firstSelectedGameObject` is selected when the system starts.
  - `SetSelectedGameObject(go[, data])`: selecting the object already selected does nothing.
    Otherwise `IDeselectHandler` is sent to the old object and `ISelectHandler` to the new
    one.
  - `currentSelectedGameObject`.
  - `alreadySelecting` guards against re-entry: a call made while a selection is in progress
    logs an error and returns.
- `pixelDragThreshold`: 10 by default.
- `sendNavigationEvents`: true by default.
- **`isFocused`:** `Application.isFocused` while the application has focus.
- `IsPointerOverGameObject([pointerId])` asks the current module.
- **`RaycastAll(eventData, results)`:**
  - Every active `BaseRaycaster` (`RaycasterManager`) is asked for hits.
  - The hits are sorted:
    1. different raycasters with different event cameras: higher camera depth first;
    2. then a higher `sortOrderPriority`, then a higher `renderOrderPriority`, when the
       raycasters differ;
    3. then a higher sorting layer value and a higher sorting order;
    4. then a higher `depth`;
    5. then a smaller `distance`;
    6. then the earlier index.

**Raycasters:**
- **`GraphicRaycaster`:**
  - **Settings:** `ignoreReversedGraphics` (true by default), `blockingObjects` (None,
    TwoD, ThreeD, All) and `blockingMask`.
  - **Display:** only graphics of the raycaster's canvas, on the pointer's display.
  - **Candidates:** for each graphic of its canvas (`GraphicRegistry`) that is a raycast
    target, has a depth other than −1 (it has been drawn; depths start at 0) and is not
    culled, the pointer must be
    inside the graphic's rect, as `RectTransformUtility.RectangleContainsScreenPoint(rect,
    point, eventCamera, raycastPadding)` tests it (the padding as its offset). Use the
    canvas's event camera for non-overlay canvases. The graphic's `Raycast` must pass.
    - This test is on the rect as drawn on the screen. As measured, a graphic collapsed by
      a zero scale (a menu popping in from scale 0) is hit only on the point or line it
      collapsed to, not everywhere. Mapping the pointer into the rect's local space does
      not give this: a zero scale maps every point onto the pivot.
  - **Order:** the hits are sorted by depth, the highest first. With
    `ignoreReversedGraphics`, graphics facing away from the camera are skipped.
  - **Blocking:** with blocking objects, a 2D or 3D physics hit nearer than the canvas cuts
    off the graphics behind it.
  - **Result fields:**
    - `sortOrderPriority`: the canvas's sorting order for an overlay canvas;
    - `renderOrderPriority`: the canvas's `rootCanvas.renderOrder` for an overlay;
    - `eventCamera`: null for an overlay;
    - each result also has `module`, `distance`, `screenPosition`, `index`, `depth`,
      `sortingLayer`, `sortingOrder`, `worldPosition` and `worldNormal`.
- **`PhysicsRaycaster`** and **`Physics2DRaycaster`** exist if the shapes require them. They
  cast from the event camera.

**`ExecuteEvents`:**
- The static event functions: `pointerEnterHandler`, `pointerExitHandler`,
  `pointerDownHandler`, `pointerUpHandler`, `pointerClickHandler`,
  `initializePotentialDrag`, `beginDragHandler`, `dragHandler`, `endDragHandler`,
  `dropHandler`, `scrollHandler`, `updateSelectedHandler`, `selectHandler`,
  `deselectHandler`, `moveHandler`, `submitHandler` and `cancelHandler`, and
  `pointerMoveHandler` (see "Pointer move events").
- **`Execute<T>(go, data, functor)`:** calls the functor on every component of `go` that
  implements `T` and is an active, enabled Behaviour, or a non-Behaviour. They are called in
  component order. An exception in one is logged, and the rest still run. It returns whether
  any were called.
- **`ExecuteHierarchy`:** from `go` upwards, the first object that handles `T`.
- **`GetEventHandler<T>(go)`:** that object, without executing.
- **`CanHandleEvent<T>`** and **`ValidateEventData<T>`**.

**Event data:**
- **`BaseEventData`:** `currentInputModule`, `selectedObject`, `Use()`, `used` and `Reset()`.
- **`AxisEventData`:** `moveVector` and `moveDir`.
- **`PointerEventData`:**
  - Pointer: `pointerId`, `position`, `delta`, `pressPosition`, `scrollDelta` and `button`.
  - Objects: `pointerEnter`, `pointerPress`, `rawPointerPress`, `lastPress`, `pointerDrag`,
    `pointerClick` and `hovered`.
  - Raycasts: `pointerCurrentRaycast` and `pointerPressRaycast`.
  - Clicks and drag: `eligibleForClick`, `clickCount`, `clickTime`, `useDragThreshold`,
    `dragging`.
  - Derived: `enterEventCamera`, `pressEventCamera`, `IsPointerMoving()`, `IsScrolling()`.
  - The `ToString` is as documented.

### Input modules

**`BaseInputModule`:**
- **`input`:** a `BaseInput` (Unity's `Input` wrapper). It is created on the GameObject, or set
  through `inputOverride`.
- **Protected helpers:**
  - `eventSystem`;
  - `FindFirstRaycast` (the first result with a GameObject), `DetermineMoveDirection`,
    `FindCommonRoot` and `GetBaseEventData`/`GetAxisEventData`.
- **`HandlePointerExitAndEnter(eventData, newEnter)`** (with pointer moves, see "Pointer move
  events (measured 2026-10-23)", which corrects step 1):
  1. When the new target is null or the pointer data's enter is unchanged, exit is sent to the
     whole hovered list. When the new target is null, the list is then cleared.
  2. Otherwise exit is sent to old hovered objects up to the common root with the new target,
     and enter to new ones from the new target up to that root.
  3. `hovered` is updated, and `pointerEnter` is set.
- **Enabling:** `OnEnable` and `OnDisable` notify the event system so it updates its module
  list.

**`PointerInputModule`:**
- **Pointer data:** `GetPointerData(id, out data, create)`, `RemovePointerData`,
  `CopyFromTo`, `StateForMouseButton`, `GetLastPointerEventData` and `ClearSelection`.
- **`GetMousePointerEventData(id)`:**
  - The mouse pointer data is updated: position from `input.mousePosition`, the delta since
    last frame, scroll from `input.mouseScrollDelta`, and the button state per mouse button.
  - Left is the pointer id -1, right -2 and middle -3. Right and middle data are copies of the
    left with their own buttons.
  - The raycast is redone: `RaycastAll`, then `FindFirstRaycast`.
- **`ProcessMove`:** enter and exit for the current raycast target, unless the cursor is
  locked.
- **`ProcessDrag`:**
  - It needs a moving pointer with a drag target, and the cursor not locked.
  - Dragging starts when the pointer moves beyond `pixelDragThreshold` from the press position
    (squared distance), or always without `useDragThreshold`. `beginDrag` is sent, then
    `drag` each move.
  - A press that becomes a drag is no longer eligible for click: `pointerUp` is sent to the
    pressed object then.
- **`IsPointerOverGameObject(id)`**, `kMouseLeftId`, `kFakeTouchesId` and the
  `MouseButtonEventData`/`ButtonState`/`MouseState` helper classes are part of the contract.

**`StandaloneInputModule`:**
- **Settings:** `horizontalAxis`, `verticalAxis`, `submitButton`, `cancelButton`,
  `inputActionsPerSecond` (10) and `repeatDelay` (0.5).
- **`ShouldActivateModule`** is true with the base condition and any of:
  - submit, cancel or a navigation axis pressed or non-zero;
  - the mouse moved;
  - a mouse button pressed;
  - touches present.
- **`Process`:**
  1. Unless the application is unfocused (with `isFocused`), the selected object gets
     `updateSelected`.
  2. Then touch events, when touches are supported and used, or else the mouse.
  3. Then, with navigation events enabled, move and submit/cancel.
- **Mouse:**
  - For left, right and middle in turn, pressed and released are processed. The left button
    also gets move and drag; the others get drag only.
  - A non-zero scroll delta sends `scroll` up the hierarchy from the current target.
- **Press:**
  1. `pressPosition`, `pointerPressRaycast` and `eligibleForClick` are set, and the delta is
     zeroed.
  2. The selection is cleared when the new press target differs from the selected object.
  3. `pointerDown` is sent up the hierarchy. When nothing handles it, the target is the
     object's click handler.
  4. A click within 0.3 s on the same object increments `clickCount`; otherwise it resets to
     1.
  5. `pointerPress`, `rawPointerPress`, `pointerClick` and `clickTime` are set.
  6. The drag handler gets `initializePotentialDrag`.
- **Release:**
  1. `pointerUp` goes to the press target.
  2. `pointerClick` goes to the press target when it equals the click handler under the
     pointer and the press is still eligible.
  3. Otherwise `drop` goes to the drop handler under the pointer, when dragging.
  4. `endDrag` is sent when dragging.
  5. The press fields are cleared.
  6. Enter and exit are processed for the current target. When the pointer left the press
     target, exit and enter are sent again, so hover follows the pointer.
- **Navigation:** move events repeat after `repeatDelay` at `inputActionsPerSecond`, reading
  the raw axes. Submit and cancel go to the selected object.

### Selectables

**`Selectable`:**
- **Settings:** `interactable` (true), `navigation` (Automatic), `transition` (ColorTint),
  `colors`, `spriteState`, `animationTriggers` and `targetGraphic`.
- **`allSelectablesArray`, `allSelectableCount`, `AllSelectablesNoAlloc`:** every enabled
  Selectable.
- **`IsInteractable()`** is `interactable` and every enclosing `CanvasGroup` up to one that
  ignores its parents allowing interaction.
- **State:** Normal, Highlighted, Pressed, Selected or Disabled.
  - Disabled when not interactable.
  - Pressed while the pointer that pressed it is down, inside or not.
  - Selected when it is the event system's selection.
  - Highlighted while the pointer is over it.
  - Otherwise Normal.
  - It responds to pointer down, up, enter and exit, select and deselect, and moves.
- **Transitions:**
  - ColorTint: `targetGraphic.CrossFadeColor(color × colorMultiplier, fadeDuration, true, true)`.
    It applies instantly when the transition happens during `OnEnable`, or when the object is
    inactive.
  - SpriteSwap: sets the target image's `overrideSprite` from `spriteState`.
  - Animation: sets the Animator trigger named in `animationTriggers` and resets the others.
- **Pointer down:** a left-button press on an interactable selectable with navigation selects
  it, unless it is already selected.
- **Navigation:** `FindSelectableOnLeft`, `…Right`, `…Up` and `…Down`.
  - Explicit: uses the navigation's targets.
  - Automatic, Horizontal and Vertical: the nearest selectable in that direction, scored by
    distance and angle, as documented.
  - None: there is no navigation.

**`Button`:** `onClick` (`ButtonClickedEvent`, a UnityEvent):
- It is invoked on `OnPointerClick` with the left button when interactable and active.
- It is invoked on `OnSubmit`, which also shows Pressed and then fades back.

**`Toggle`:**
- **Settings:** `isOn`, `group`, `graphic` and `toggleTransition` (None, or Fade over
  0.1 s).
- **Events:** `onValueChanged` (`ToggleEvent`, `UnityEvent<bool>`).
- **Value:**
  - A click with the left button or a submit flips it.
  - `SetIsOnWithoutNotify` sets it without the event.
  - The `graphic`'s alpha shows the state, through `CrossFadeAlpha`.
- **Groups:** joining a group with `isOn` true makes it the group's active toggle.
  - **`ToggleGroup`:** `allowSwitchOff`.
  - `NotifyToggleOn(toggle[, sendCallback])` turns the others off.
  - `RegisterToggle` and `UnregisterToggle`.
  - `AnyTogglesOn`, `ActiveToggles`, `GetFirstActiveToggle` and `SetAllTogglesOff`.
  - Without `allowSwitchOff`, turning off the only toggle that is on is refused.

**`Slider`:**
- **Settings:** `fillRect`, `handleRect`, `direction` (LeftToRight, RightToLeft, BottomToTop,
  TopToBottom), `minValue`, `maxValue`, `wholeNumbers`, `value` and `normalizedValue`.
- **Events:** `onValueChanged` (`SliderEvent`).
- **`SetValueWithoutNotify`** sets the value without the event.
- **Visuals:**
  - The fill rect's anchors span 0 to the value along the direction; an Image fill of type
    Filled instead gets `fillAmount`.
  - The handle's anchors sit at the value.
- **Input:**
  - Dragging the handle moves the value, keeping the offset where the handle was grabbed.
  - Clicking the track jumps to the point.
  - The arrow keys step by 1 for whole numbers, otherwise by a tenth of the range.

**`Scrollbar`:**
- **Settings:** `handleRect`, `direction`, `value` (0 to 1), `size` (0 to 1) and
  `numberOfSteps`.
- **Events:** `onValueChanged`.
- **Visuals:** the handle's anchors span `size` at `value`.
- **Input:** dragging the handle moves the value. A press on the track outside the handle
  centres the handle on the press point at once, and the handle then follows the pointer while
  the button is held; until it moves, the press point is re-applied at the end of each frame.
  The protected `ClickRepeat` overloads and `Update` are part of the surface ("Scrollbar
  presses on the track", measured, below).

**`ScrollRect`:**
- **Settings:**
  - `content`, `viewport` (or its own rect), `horizontal`, `vertical`;
  - `movementType` (Unrestricted, Elastic, Clamped) and `elasticity` (0.1);
  - `inertia` (true), `decelerationRate` (0.135) and `scrollSensitivity` (1);
  - the scrollbars, with their visibility (Permanent, AutoHide, AutoHideAndExpandViewport) and
    spacing;
  - `onValueChanged` (`ScrollRectEvent`, `UnityEvent<Vector2>`).
- **Values:** `normalizedPosition`, `horizontalNormalizedPosition`,
  `verticalNormalizedPosition`, `velocity` and `StopMovement()`.
- **Dragging** with the left button moves the content.
  - When elastic, overshoot beyond the content's bounds is rubber-banded; the standard
    rubber-band function is used.
  - **On release, with inertia:** the velocity decays by `decelerationRate^dt`.
  - **Elastic:** the content returns to its bounds with `Mathf.SmoothDamp` over
    `elasticity`.
  - **Clamped:** the content stays within the bounds.
- **Scroll wheel:** it moves the content by `scrollSensitivity` × the delta. Vertical scrolling
  uses the y delta, and a horizontal-only rect uses whichever delta is larger.
- **When it updates:**
  - Positions update in `LateUpdate`, using unscaled delta time for the movement. The
    scrollbars are updated, and `onValueChanged` fires when the normalized position changes.
  - It also updates during `Rebuild(PostLayout)`.

  KScrollRect overrides `LateUpdate`.
- **Layout:** it is an `ILayoutElement` (all -1) and an `ILayoutGroup`, so it lays out its
  scrollbars and viewport.

**ScrollRect: normalized position and scrollbars, measured** (2026-10-22, `tools/UnityProbe`,
the `UIS scroll` lines, in the original player and in ours; a 200 × 100 viewport unless
stated). This implementation already does all of this, on these measurements; keep it.
- **Bounds.** Both are boxes in the viewport's local space (the viewport, or the scroll rect's
  own rect when there is none), kept as a centre and extents, with min = centre − extents:
  - the view: the viewport's rect;
  - the content: the box around the content's four world corners
    (`RectTransform.GetWorldCorners`), each taken into the viewport's space with its
    `worldToLocalMatrix`. Scale and rotation count: content of 400 × 400 at scale 0.25 is
    100 × 100.
- **Content smaller than the view on an axis** is grown to the view's size about its pivot:
  the centre moves by −(view size − content size) × (pivot − 0.5) and the size becomes the
  view's. Content exactly the view's size is not changed.
- **The normalized position on an axis** (0 at the left or bottom of the scrollable range, 1
  at the right or top):
  - when the (grown) content is no larger than the view, or its size equals the view's as
    `Mathf.Approximately` compares them: 1 when the view's min is greater than the content's
    min, else 0. That comparison of the mins is exact, with no tolerance. Without the
    approximate test, content an ulp larger than the view gives fractions such as −0.5 or
    0.25, which the original never reported;
  - otherwise (view min − content min) / (content size − view size), not clamped. Measured
    on 400 × 300 content, offset −250 … 250: −1.25 to 2.25, both axes.
  - The getter computes the bounds afresh from the current transforms, so it is valid before
    the first `LateUpdate`, and whichever of `horizontal`, `vertical` and `movementType`.
  - This formula reproduces all 172 axis values the probe recorded in the original, from the
    logged inputs, bit for bit (`tools/UnityProbe/uis-check.py fit`), the fractional ones
    included.
- **What the exactness means.** Where the content's size equals the viewport's (the content
  stretched to it, or laid out to its width, as Sweep By Type's list is), 0 or 1 is decided
  by the rounding of the transformed corners:
  - Content as wide as the view and offset by −0.1, −0.01, −0.001 and −0.0001 reports 1; by
    0, +0.00001 and +0.001 it reports 0; by −0.00001 either, by the layout.
  - In 48 random exactly-fitting layouts (sizes 80–600, fractional positions, half of them
    under a parent at scale 0.86) the original reported 1 on about 40 % of the axes.
  - Both players apply this formula. Their difference on such axes came from the engine's
    corners and matrices, 1 to 3 ulps apart in 57 of 86 cases; it went with the engine's
    worldToLocalMatrix following the original's ("Transform hierarchy to the bit" below,
    2026-10-29). That belongs to the engine's transform arithmetic, not to this package; do not
    add a tolerance here.
- **Scrollbars.** On an update that finds the view bounds, the content bounds or the content's
  position changed since the previous one (always on the first):
  - each assigned scrollbar's `size` becomes clamp01((view size − |overshoot|) / content
    size), with the (grown) content size and the overshoot as found at the start of that
    update, before the content is moved back. Example: Clamped, 100 × 50 content offset by 30: size 0.85 though the content
    is clamped in the same update; it stays 0.85, since nothing changes afterwards;
  - its `value` becomes the normalized position: 0 or 1 when the content fits, as above.
  - Before that update the scrollbar keeps its own values (a new one: value 0, size 0.2).
  - Setting `value` invokes the scrollbar's `onValueChanged`, to which the scroll rect listens,
    so it sets the normalized position on that axis. When the content fits, setting 1 moves
    the grown content's min onto the view's min. Measured: 100 × 50 content offset (−20,
    −10), Unrestricted: the first update sets both scrollbars to 1 and the content snaps to
    (0, 0). With offsets ≥ 0 the value is 0, which the new scrollbar already holds, so
    nothing is invoked and the content stays where it is.
- **Movement types** with content that fits (100 × 50 offset by (30, −20), pivots (0,1),
  (0.5,0.5), (1,0)):
  - Clamped puts it back at once, in the first update (anchoredPosition (0, 0));
  - Elastic springs back over time (`SmoothDamp` with `elasticity` and unscaled time; after
    0.21 s, 2.4 of the 30 remained);
  - Unrestricted leaves it, except through the scrollbar feedback above.
- **Content resized at run time** (400 × 300 scrolled to (0.5, 0.5), then shrunk to 100 × 50,
  then grown back): the normalized position follows the new bounds in the next update. While
  the content fits, setting `normalizedPosition`, or either axis, does not move it.
- **Not measured:** dragging and the scroll wheel with content that fits.

**ScrollRect: scrollbar visibility, measured** (2026-11-01, `tools/UnityProbe`,
`PROBE_SCENARIO=scrollvis`, `ScrollVis.cs`). Every frame's update sets the scrollbar's
activity, whatever set it earlier in the frame: a Permanent scrollbar is active while its axis
is enabled and inactive while it is not; an AutoHide or AutoHideAndExpandViewport one is active
while the content is larger than the view on its axis, whether the axis is enabled or not. A
scrollbar the game's code deactivates in an Update is active again at the frame's end. This
holds with the scrollbar a child of the scroll rect, beside it on the canvas, and under the
viewport with the scroll rect on the content (as the game's build menu has it). Found with
Better Build Menu (CN) (Workshop 3825795220), which deactivates the build menu's Permanent
scrollbar from PlanScreen's update at most every 0.2 s: the original, drawing under five frames
a second there, reads it inactive in every Update and draws it every frame, as this
implementation does.

**ScrollRect: bounds to the bit** (measured 2026-10-24, `tools/UnityProbe`: `PROBE_SCENARIO=
zeroscale`, `vitalsopen`, `matmul`). Found with Resize Dup UI (Workshop 2549295369), whose Vitals
screen opened with a scrollbar of size 0.94 to 0.98 in about half of our runs, 1 in the original's.
- **The screen's first frames, in both players.** `TableScreen.OnActivate` sets the screen's
  scale to 0; the next frame's `ScreenUpdate` sets it back to 1. The scroll rect's first
  `LateUpdate` runs in between. Every corner of its content maps to one point; that point,
  taken into the viewport's space, lies 43 above the view's top, so the content (16 px, grown to
  the view) overshoots by 43 and springs down by an amount that depends on that frame's
  `unscaledDeltaTime` (0.7 to 1.6 px at 20 to 30 ms; 28 px at 250 ms). In the next update the
  scale is 1, the overshoot is the distance moved, and the scrollbar is set to that size and
  to value 1, which snaps the content back. The probe's copy of this hierarchy gave the same
  numbers in both players (`zeroscale`, all six cases), and so did the game (`vitalsopen`).
- **What clears it.** The next frame the rows lay the content out to 208 px. Both heights fit
  the view, so both are grown to its height, and the update runs again only if the grown
  bounds differ. In the original they usually do, by a few ulps: -249.499969 with 16 px,
  -249.499954 with 208 px. That update sets the scrollbar to size 1 and value 0 (9 of 12
  original runs). Ours computed both as the same value in every run logged, so the size of the
  overshoot stayed; after a slow frame that was 0.94, outside the dump's tolerance.
- **The arithmetic.** The original's bounds are reproduced from its logged corners and
  `worldToLocalMatrix` when `MultiplyPoint3x4` rounds each component once (the sum of products
  in double), and not when every multiply and add rounds. The bounds' own centre and extents
  arithmetic does not change the result either way. Unity's `Matrix4x4` products all round that
  way (400 random cases of each, `mm-check.py`); that is the engine's `Matrix4x4`
  (ARCHITECTURE D25), which now follows it. This package needs nothing beyond mapping the
  corners with the engine's `MultiplyPoint3x4`.
- **No longer apart.** `localToWorldMatrix` (and with it `GetWorldCorners`), `rotation`,
  `lossyScale` and, since 2026-10-29, `worldToLocalMatrix` follow the original bit for bit
  ("Transform hierarchy to the bit" below), so the grown bounds move by an ulp in ours exactly
  when they do in the original. Both remain timing-dependent: the stale size shows in the
  original too (3 runs of 12, sizes 0.995 to 0.9985), and in either player after a slow frame.
  Of the 73 specs that cited `scroll-normalized-position`, 65 played without it after that
  change; the rest are table screens (this timing) and the details screen's side screens, where
  an input already differs: Better Checkpoints' AccessControlSideScreen (`scrollgeo`) has its
  SideScreen at anchoredPosition x −6.899994 in ours and −6.8999634 in the original, so the
  content's corners, the view's matrices and the normalized position follow from it.
- **Schedule & Priority Screen Expander** (3774780988, measured 2026-10-26 with `vitalsopen` in
  its spec, `PROBE_VO_SCREEN=VitalsTableScreen`) opens the same Vitals screen, and its dump
  differs from one original run to the next. The screen's first update is under scale 0 in both
  players, and the overshoot then depends on that frame's `unscaledDeltaTime` (73 and 92 ms in
  two original runs: content moved down 7.27 and 10.05, scrollbar sizes 0.976 and 0.966; 38 and
  22 ms in ours: 2.45 and 0.91, sizes 0.992 and 0.997). Whether a later update clears it depends
  on the grown bounds moving by an ulp, as above. In five original runs the dumped scrollbar was
  value 0, size 1 once and value 1 with sizes 0.97 to 0.99 four times; in eight of ours, value 0,
  size 1 once and value 1 with sizes 0.98 to 1 seven times. The spec's failure ("0.98 against 1")
  compares with the one original run that cleared it. Nothing here is for this package to
  change: the spec needs to leave that scrollbar's size and value out of its comparison, or
  match them with a tolerance and either value, as the known `scroll-normalized-position`
  finding does for the normalized position.

**Transform hierarchy to the bit** (measured 2026-10-24, `tools/UnityProbe`
`PROBE_SCENARIO=transform`, lines `TN`, `TR`, `TQ`, `TQS`, `TD`, `TQN`; the U57 player on this
machine). 1,200 chains of 1 to 6 transforms (translation only, uniform, non-uniform, negative and
zero scales, rotations, z rotations with (s, s, 1) scales as UI uses, mixes) and 400 chains of
RectTransforms, every level logged. Each operation below rounds to float; no fused multiply-add.
- **Local matrix.** Each rotation column is the quaternion turning that basis vector:
  t = 2 (q.xyz × e), then component i is ((−q_b t_a + q_a t_b) + w t_i) + e_i with
  a = i+1, b = i+2 (mod 3); the column is then multiplied by that axis's scale, and the
  translation is the local position. Its values equal the usual 1 − (yy + zz), xy − wz, …
  matrix (x2 = x + x, xx = x·x2, …); the summation decides the sign of a zero element.
- **World matrix: from the leaf up.** P0 (P1 (… (Pn L))), not (parent world) × local. In each
  product an element is a0·b0 + (a1·b1 + a2·b2), and the translation's own column is added
  last. 4,200 of 4,200 transform levels and 2,792 of 2,792 RectTransform levels (before and
  after a root resize), signed zeros included; composing from the root down matches 103 of
  800 two-level products. `GetWorldCorners` is that matrix's `MultiplyPoint` of the local
  corners (2,792 of 2,792).
- **World rotation: from the leaf up too.** r = q0 ⊗ m0(q1 ⊗ m1(… qn)), where m_k flips a
  child rotation's components by that ancestor's scale signs (x by sy·sz, y by sx·sz, z by
  sx·sy). The product's lanes are ordered x = −(((az·by − ay·bz) − aw·bx) − bw·ax),
  y = −(((ax·bz − az·bx) − aw·by) − bw·ay), z = −(((ay·bx − aw·bz) − az·bw) − ax·by),
  w = ((aw·bw − ax·bx) − az·bz) − ay·by: 4,200 of 4,200 with signs of zeros.
- **lossyScale:** the diagonal of R(rotation⁻¹) · M, with R the usual quaternion matrix of the
  conjugate and each diagonal element r0·m0 + (r1·m1 + r2·m2): 4,200 of 4,200.
- **localRotation's setter** always normalizes, unit-length input included: x and z over
  √((xx + yy) + (zz + ww)), y and w over √((xx + ww) + (yy + zz)) (5,480 sets of 5,480).
- **position** is the world matrix's translation for rotation-free chains (all 1,149); with
  rotations it is not (the native rotates each level's position by the quaternion with a
  formula not found).
- **Not reproduced** (searched, no formula matches every case):
  - `TransformDirection`, `TransformVector`, `TransformPoint` and `position` under rotation.
    The result for a basis vector is the local matrix's column, but for a general vector it is
    neither a matrix–vector product in any order (44% of vectors), nor t = 2 q×v,
    v + w t + q×t in any of 120 term orders per lane (≤ 49% per lane), nor q v q* with this
    product or any lane orders, nor the forms with q(q·v). With q = (−½, −½, −½, ½) exactly it
    turns (0, 68.15, −0.0709) into (68.15, −9291 / 2^17, 0): one lane cancels at the vector's
    magnitude, another exactly, so the lanes are grouped differently. Searched again with
    Inverse3DAffine's and Rotate's findings in hand: the managed `Quaternion * Vector3` matrix
    form in float gives 18% of vectors (66% of x lanes), in double 12%, the basis formula in
    double 10%.
  - `InverseTransformPoint` and the other `InverseTransform*`: not the worldToLocalMatrix
    product (IS1 `itp`: 1,780 of 6,000 even without rotation), not reproduced.
  - The formula of the per-axis inverse scale f(s) (`worldToLocalMatrix` below takes it from a
    measured table instead). It is a function of the mantissa alone (all 2^23 mantissas of
    [1, 2) measured, `PROBE_IS_PART=full`; 26,414 drawn scales of exponents −8 to 8 and both
    signs agree with them), within 1.4 ulp of 1/s and equal to 1/s rounded for 65% of
    mantissas (−1 ulp 16%, +1 ulp 18%). Its error has a part linear in the distance of 1/s
    from the nearest multiple of 1/8, about 0.75 ulp × (8/s − round(8/s)), which jumps at
    1/s = 9/16, 11/16, 13/16 and 15/16, plus a part that correlates with the rounding of
    s × rcpps(s). At the edges: 0 for |s| below 1e-9 (bits 0x3089705f; −0 and subnormals
    included), 0 with the scale's sign from 2^126 up, where rcpps(2^126) = 0.99976 × 2^-126 is
    subnormal (1/2^126 itself is not), and NaN for NaN. Not reproduced by 1/s, rcpps or
    vrcp14ss (this CPU's tables) with one or two Newton steps in about 40 evaluation orders
    with float, double or fused intermediates, Halley and series steps, Goldschmidt from
    rcpps or from round(8/s)/8, rsqrt forms, nor the cofactor forms (s²·(1/s³), s/s²).
  - RectTransform's rect from its anchors: (w·aMax − w·aMin) + sizeDelta matches 98% of sizes;
    w·(aMax − aMin) + sizeDelta 94%, the sums in double 91%, the size as the difference of the
    corners in the parent (anchor points plus offsets) at most 71%.
- **`Matrix4x4.inverse`** and `Matrix4x4.Inverse` (managed API, native) are Mesa's
  Gauss–Jordan inversion with partial pivoting in float (`invert_matrix_general`, MIT), and the
  engine ports it (`MesaInverse`, from Mesa 20.3.5; `docs/LICENSING.md`). Every operation
  rounds; a zero pivot at any of the four steps gives `Matrix4x4.zero` (Mesa's caller would
  load the identity). `matmul` scenario, `mm-check.py rule`: 400 of 400 random matrices (`MM`)
  and 35 of 35 singular and near-singular ones (`MMS`): rank-deficient at each step, a zero TRS
  scale, a 1e-20 scale whose float determinant is 0 (inverted all the same), subnormal scales
  (inf and NaN), rows dependent but for an ulp, negative zeros, NaN, infinity, projections.
  Our player matches both sets bit for bit (`mm-check.py diff`). `worldToLocalMatrix` does not
  use it.
- **`worldToLocalMatrix`** (measured 2026-10-29, `PROBE_SCENARIO=invscale`, lines `IS1` to
  `IS6`; 3,000 chains of 1 to 6 transforms, UI-like ones with a scaled root and z rotations,
  and general rotations with non-uniform and negative scales) is each level's inverse,
  multiplied from the leaf up: ((L⁻¹ · P_{n−1}⁻¹) · …) · P_0⁻¹, each product as the world
  matrix's (a0·b0 + (a1·b1 + a2·b2), the translation column added last). A level's inverse has
  row i = f(s_i) × column i of its rotation matrix (the transposed rotation, its columns as the
  local matrix's), and the translation −(row · p), summed r0·p.x + (r1·p.y + r2·p.z). Every
  element of all 2,779 chains whose local values our player reads back alike (the rest differ
  in `Quaternion.Euler`) matches; inverting the world matrix (Mesa or cofactors) and composing
  from the root down do not. A zero scale axis inverts to 0 at that level, so singular chains
  need no other path. The engine keeps f(s) as 1/s corrected by the measured difference
  (`Math/InverseScale.bin`, 2 bits per mantissa, 465 KB deflated).

**Camera and projection math** (measured 2026-10-24, `tools/UnityProbe`
`PROBE_SCENARIO=projection`, `pj-check.py rule`; the U57 player on this machine). Does more of
Unity's native matrix and camera math follow Mesa (`m_matrix.c`, 20.3.5) or GLU (`project.c`,
9.0.3), as the inverse does? Each function got 400 to 600 random cases and its degenerate ones
(equal planes, zero and negative sizes, fields of view of 0, 180 and 360 degrees, NaN, infinity,
and for the look-at and axis builders the size at which they give up, found by bisection). Every
operation below rounds to float; no fused multiply-add. NaNs agree but for their sign in the
Python check; the unit tests compare the bits.
- **`Matrix4x4.Ortho`** is Mesa's `_math_float_ortho`: 417 of 417. It is what the engine
  computed already.
- **`Matrix4x4.Frustum`** is Mesa's `_math_matrix_frustum`: 417 of 417, near == far giving
  −∞ in the depth row. The engine ports it (`MesaFrustum`, MIT); it had no `Frustum` before.
- **`Matrix4x4.Perspective`** is neither: GLU 9.0.3's `gluPerspective` (double, cos / sin)
  gives 56 of 440, the older GLUs' n·tan(fov·π/360) through Mesa's frustum 108. The original's,
  440 of 440: r = (fov / 360) · π, cot = cosf(r) / sinf(r) (the C library's, glibc here:
  correctly rounded sin and cos give 385, 1 / tanf 245), m00 = cot / aspect, m11 = cot,
  m22 = (near + far) / (near − far), m23 = 2·near·far / (near − far) (so near == far gives +∞
  where Mesa and GLU give −∞; 2·near·far overflows to −∞ at 1e20), m32 = −1. The engine's
  computed 1 / tanf(fov·Deg2Rad·0.5) and Mesa's signs and differed by an ulp in m00 and m11 at
  60 degrees; it now follows this (MathF's sinf and cosf are the same libm's).
- **`Matrix4x4.LookAt`** builds its basis with `gluLookAt`'s float arithmetic: forward
  normalized as GLU's `normalize` (√ of x² + y² + z² summed left to right, each component
  divided by it), right = up × forward normalized, up = forward × right, as columns with
  `from` as the translation, 473 of 473. GLU's own order (side = forward × up, negated for
  Unity's left-handed matrix) gives the same values but other signs of zero (309). Where GLU
  divides by a tiny length Unity gives the identity rotation: forward or right shorter than 1e-5
  (|dir| boundary 1e-5 along x, 1e-5/√3 along (1, 1, 1); |up × dir| boundary 1e-5 for up of
  sizes 1e-3 to 1e3), a forward overflowing float (2^64 along x), NaN passing through. The
  engine ports it (`GluLookAt`, SGI Free Software License B); it had no `LookAt` before.
- **`Quaternion.AngleAxis`**: not Mesa (`_math_matrix_rotate` builds a matrix from the angle
  and axis; through a quaternion, `Rotate(AngleAxis(a, axis))` matches it in 10 of 400). The
  original's, 420 of 420: half = (angle / 360) · π, the axis times sinf(half) / |axis|,
  w = cosf(half), the identity unless |axis| > 1e-6 (so also for a NaN axis; boundary 1e-6
  along x, 1e-6/√14 along (1, 2, 3)). The engine follows it; its angle · Deg2Rad · 0.5 matched
  235.
- **`Matrix4x4.Inverse3DAffine`**, 629 of 629 (608 random affine matrices, 21 singular and
  degenerate ones, 9 at the threshold): not Mesa's `invert_matrix_3d_general`, which matches 365.
  The determinant is summed over column 0's lanes of col0 · (col1 × col2), each lane
  (e_i·a_i+1)·b_i+2 − (e_i·a_i+2)·b_i+1 with e, a, b columns 0, 1, 2, the lanes left to right
  (the SIMD shape; Mesa's positive/negative sum, every order of the six terms, the row and
  column expansions and the exact determinant all miss some). The 3 × 3 is Mesa's float
  cofactors (a·b − c·d) times k = 1 / det, the translation −((t.x r0 + t.y r1) + t.z r2) per row,
  the bottom row (0·k, 0·k, 0·k, 1), so (−0, −0, −0, 1) for a negative determinant and NaNs for a
  NaN one; the input's bottom row is ignored. It fails, giving false and the zero matrix (m33
  0 too), when det·det < 1e-25 in float: bisected on Scale(x, 1, 1), (x, x, x) and (−x, 1, 1),
  |det| = 3.1622776e-13 fails and the next float passes; NaN and infinite determinants pass.
  The engine follows it, with both overloads (by value and `in`); of a NaN input the NaNs'
  signs can differ (CoreCLR orders a product's operands its own way).
- **`Matrix4x4.Rotate`**, 400 of 400 (and 400 of `AngleAxis`'s): the usual quaternion matrix with
  each product (x·2x, w·2z, …) stored to float and the diagonal's 1 − (yy + zz) evaluated in
  double and rounded once: managed code under Unity's Mono (ARCHITECTURE D25). Every float form
  matched 176. `TRS` stays all float (400 of 400), so the engine's TRS no longer calls Rotate.
- **Camera rects** (240 random cameras and 80 rule cases at 1024 × 768, `PK` lines). `rect` keeps
  what was set, out of range, negative and NaN included. `pixelRect` clips it to the target
  (the target texture's size, else the screen's): per axis the start is 0 + x·W and the end
  start + w·W, each clamped to [0, W] with comparisons that pass a NaN through (std::min and
  max), and the size end − start, at least 0; the 0 + turns a −0 start positive. The
  `pixelRect` setter stores the rect times 1 / W (21.5 / 768 is an ulp under 21.5 · (1/768)).
  `pixelWidth`/`Height` are a whole-pixel viewport's size: the start rounded half up
  (floor(v + 0.5)), the end (start + size) rounded and clamped to [0, W], the difference in
  32 bits (a NaN start gives int.MinValue, a NaN size 0). `aspect` is pixelRect's width over its
  height, 1 for a zero height; a set aspect (any value, 0, negative, NaN, infinity) stays through
  rect, pixel rect, projection and field-of-view changes until `ResetAspect`. The engine
  clamped the rect when it was set, which changed the projection of 50 of 240 cameras.
- **`worldToCameraMatrix`**, 240 of 240 and the 8 poses (the origin, −0, 180° turns, a child of a
  rotated, mirrored, scaled parent): the world rotation's matrix as the hierarchy builds it
  (above, "Local matrix"), unscaled and transposed into rows; each row's translation
  0 − (r0·p.x + (r1·p.y + r2·p.z)) with p the world position; then the third row subtracted from
  0, so zeros stay positive. The scale is ignored. `projectionMatrix` is `Perspective(fieldOfView,
  aspect, near, far)` or `Ortho(−size·aspect, size·aspect, −size, size, near, far)` and
  `cameraToWorldMatrix` Mesa's inverse of `worldToCameraMatrix`, 240 of 240 each.
- **Conversions**, 1,440 points of the 240 cameras, all bit for bit but the rays' perspective
  direction. Every operation rounds to float; C is cameraToWorldMatrix, its translation the
  camera position, −column 2 the forward axis; the screen side uses the whole-pixel viewport
  (x0, y0, w, h) above, not pixelRect.
  - `WorldToScreenPoint`: c = (P·V)·(p, 1) with P·V's elements summed left to right in float;
    x = ((c.x · (1 / c.w) + 1) · 0.5) · w + x0, y likewise; z = −((p − position) · C's column 2).
    `gluProject` in double matched 47.
  - `WorldToViewportPoint` = (screen − pixelRect.x) / pixelRect.width, z kept;
    `ScreenToViewportPoint` the same of the screen point, `ViewportToScreenPoint`
    v · pixelRect.width + pixelRect.x.
  - `ScreenToWorldPoint`: ndc = (s − x0) · 2 / w − 1 per axis, the point q = inverse(P·V)·(ndc,
    0.95, 1) times 1 / its w (0.95 found by searching the depth in steps of 0.05; Mesa's inverse);
    d = q − position, dist = d · forward. Perspective: position + d · (z / dist); orthographic:
    q − forward · (dist − z). It gives (0, 0, 0) unless |dist| ≥ 1e-6 (bisected on the clip
    planes for both projections; a NaN dist, as from an empty viewport or a zero aspect, fails).
    `ViewportToWorldPoint` is `ScreenToWorldPoint` of `ViewportToScreenPoint`. `gluUnProject`
    matched at most 9 of 240.
  - `ScreenPointToRay`: the origin is `ScreenToWorldPoint` at the near plane (1,440 of 1,440); an
    orthographic camera's direction is the forward axis normalized (its √(x² + y² + z²) in float,
    each component divided), 720 of 720. A perspective camera's is within an ulp or two of the
    normalized offset from the camera to the screen point at distance 1000 (175 of 720; the
    point at the far plane, at other distances, the depth-0.95 point, differences of two depths,
    camera-space directions, double, reciprocal and rsqrt normalizations all matched fewer):
    not reproduced; the engine uses that form. Where the unprojection fails the ray starts at the
    camera position along (0, 0, 1). `ViewportPointToRay` is `ScreenPointToRay` of
    `ViewportToScreenPoint`.
- **Not measured on Windows.** `Perspective` and `AngleAxis` depend on the C library's sinf and
  cosf, and were measured on Linux only (glibc, which .NET's MathF also calls there).

### InputField and Dropdown (legacy)

**`InputField`:**
- **Settings:**
  - `text` and `textComponent` (a legacy `Text`), `placeholder`;
  - `characterLimit`, `contentType` and the line, input, keyboard and character validation
    types it sets;
  - `caretBlinkRate`, `caretWidth`, `selectionColor`, `readOnly`;
  - `onValueChanged`, `onEndEdit` and `onSubmit`.
- **Editing:** on select it starts editing: focus and caret. Keys come from `Event.PopEvent`
  in `OnUpdateSelected`, with the documented editing keys and the clipboard through
  `GUIUtility.systemCopyBuffer`.
- **Display:** the caret and selection are drawn as a child graphic.

  The game has one InputField in its data.

**`Dropdown`:**
- **Settings:** `options` (`OptionData`: text and image), `captionText`, `captionImage`,
  `itemText`, `itemImage`, `template` and `value`.
- **Events:** `onValueChanged`.
- **Showing the list:** `Show()` instantiates the template under a new canvas with a blocker
  behind it, one item per option, and selects the current value. A click on an item sets the
  value and hides the list. `Hide()` removes it after an alpha fade of 0.15 s.

  The game uses Dropdown in three places.

### DefaultControls (measured)

Mods build controls from code with `DefaultControls` (Ony's shared library: `CreateDropdown` and
`CreateScrollView`). `src/UnityEngine.UI/package-required-types.tsv` brings its shapes into the
generated files. Its public surface, from the package's metadata:
- `DefaultControls.Resources`, a struct of seven public `Sprite` fields: `standard`, `background`,
  `inputField`, `knob`, `checkmark`, `dropdown`, `mask`;
- `DefaultControls.IFactoryControls`, an interface with
  `GameObject CreateGameObject(string name, params Type[] components)`;
- `static IFactoryControls factory { get; }`: a factory that makes a new GameObject with that
  name and those components added in that order;
- `CreatePanel`, `CreateButton`, `CreateText`, `CreateImage`, `CreateRawImage`, `CreateSlider`,
  `CreateScrollbar`, `CreateToggle`, `CreateInputField`, `CreateDropdown`, `CreateScrollView`,
  each `static GameObject (Resources)`.

Each method returns a new root GameObject with no parent, on layer 0, all of its objects
active except where stated, every object made through `factory`. The hierarchies below were
measured in the original player (PerfProbe's measure mode), each sprite field named after
itself; with empty `Resources` the result is the same with every sprite null. Children are listed
in sibling order. Components are listed in order. Every object that draws has a `CanvasRenderer`
right after its `RectTransform`, with `cullTransparentMesh` true.

**Notation.** `rect` gives anchorMin–anchorMax, pivot, sizeDelta and anchoredPosition.
- Images: material default, raycast target on, fill centre on, preserve aspect off,
  pixels-per-unit multiplier 1, colour white unless given.
- Texts: the built-in font `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")`, size 14,
  style Normal, colour (50, 50, 50, 255), rich text on, raycast target on, horizontal overflow
  Wrap, vertical overflow Truncate, line spacing 1, best fit off, align by geometry off, unless
  given.
- Selectables: transition ColorTint, with colours normal (255, 255, 255, 255), highlighted
  (245, 245, 245, 255), pressed (200, 200, 200, 255), selected (245, 245, 245, 255), disabled
  (200, 200, 200, 128), colour multiplier 1, fade 0.1 s, navigation Automatic.

- **CreatePanel:** "Panel": rect (0,0)–(1,1), pivot (0.5,0.5), size (0,0), position (0,0).
  Image: sprite `background`, Sliced, colour (255, 255, 255, 100).
- **CreateButton:** "Button (Legacy)": rect centre anchors (0.5,0.5), size (160,30). Image:
  `standard`, Sliced. Button: target graphic its own Image.
  - "Text (Legacy)": stretched (0,0)–(1,1), size (0,0). Text "Button", aligned MiddleCenter.
- **CreateText:** "Text (Legacy)": centre anchors, size (160,30). Text "New Text", UpperLeft.
- **CreateImage:** "Image": centre anchors, size (100,100). Image: no sprite, Simple.
- **CreateRawImage:** "RawImage": centre anchors, size (100,100). RawImage: no texture, white,
  uvRect (0,0,1,1).
- **CreateSlider:** "Slider": centre anchors, size (160,20). It has no CanvasRenderer or Image.
  Slider: direction LeftToRight, min 0, max 1, value 0, not whole numbers; fill rect "Fill";
  handle rect "Handle"; target graphic the handle's Image.
  - "Background": anchors (0,0.25)–(1,0.75), size (0,0). Image `background`, Sliced.
  - "Fill Area": anchors (0,0.25)–(1,0.75), size (−20,0), position (−5,0). No graphic.
    - "Fill": anchors (0,0)–(0,1), size (10,0). Image `standard`, Sliced.
  - "Handle Slide Area": stretched (0,0)–(1,1), size (−20,0). No graphic.
    - "Handle": anchors (0,0)–(0,1), size (20,0). Image `knob`, Simple.
- **CreateScrollbar:** "Scrollbar": centre anchors, size (160,20). Image `background`, Sliced.
  Scrollbar: direction LeftToRight, size 0.2, value 0, steps 0; handle rect "Handle"; target
  graphic the handle's Image.
  - "Sliding Area": stretched, size (−20,−20).
    - "Handle": anchors (0,0)–(0.2,1), size (20,20). Image `standard`, Sliced.
- **CreateToggle:** "Toggle": centre anchors, size (160,20). No graphic. Toggle: on; graphic
  "Checkmark"; target graphic "Background"'s Image; no group; toggle transition Fade.
  - "Background": anchors (0,1)–(0,1), size (20,20), position (10,−10). Image `standard`,
    Sliced.
    - "Checkmark": centre anchors, size (20,20). Image `checkmark`, Simple.
  - "Label": stretched, size (−28,−3), position (9,−0.5). Text "Toggle", UpperLeft.
- **CreateInputField:** "InputField (Legacy)": centre anchors, size (160,30). Image
  `inputField`, Sliced. InputField:
  - text component "Text (Legacy)", placeholder "Placeholder", text empty;
  - content type Standard, line type SingleLine, character limit 0;
  - caret blink rate 0.85, caret width 1, selection colour (168, 206, 255, 192), not read-only;
  - target graphic its own Image.

  Children:
  - "Placeholder": stretched, size (−20,−13), position (0,−0.5). Text "Enter text...", style
    Italic, colour (50, 50, 50, 128), UpperLeft, **no font** (null).
  - "Text (Legacy)": same rect. Text empty, UpperLeft, rich text **off**, horizontal overflow
    **Overflow**.
- **CreateDropdown:** "Dropdown (Legacy)": centre anchors, size (160,30). Image `standard`,
  Sliced. Dropdown:
  - template "Template", caption text "Label", item text "Item Label", no caption or item
    image;
  - options "Option A", "Option B", "Option C", value 0;
  - target graphic its own Image, alpha fade speed 0.15.

  Children:
  - "Label": stretched, size (−35,−13), position (−7.5,−0.5). Text "Option A", MiddleLeft.
  - "Arrow": anchors (1,0.5)–(1,0.5), size (20,20), position (−15,0). Image `dropdown`,
    Simple.
  - "Template", **inactive**: anchors (0,0)–(1,0), pivot (0.5,1), size (0,150), position
    (0,2). Image `standard`, Sliced. ScrollRect:
    - content "Content", viewport "Viewport";
    - vertical only, vertical scrollbar "Scrollbar", horizontal visibility Permanent,
      vertical visibility AutoHideAndExpandViewport, vertical spacing −3, horizontal spacing 0;
    - movement Clamped, elasticity 0.1, inertia on, deceleration 0.135, sensitivity 1.

    Its children:
    - "Viewport": stretched, pivot (0,1), size (−18,0). Image `mask`, Sliced. Mask with the
      graphic hidden.
      - "Content": anchors (0,1)–(1,1), pivot (0.5,1), size (0,28). No graphic.
        - "Item": anchors (0,0.5)–(1,0.5), size (0,20). No graphic. Toggle: on; graphic
          "Item Checkmark"; target "Item Background"'s Image; toggle transition Fade.
          - "Item Background": stretched, size (0,0). Image, no sprite, Simple, colour
            (245, 245, 245, 255).
          - "Item Checkmark": anchors (0,0.5)–(0,0.5), size (20,20), position (10,0). Image
            `checkmark`, Simple.
          - "Item Label": stretched, size (−30,−3), position (5,−0.5). Text "Option A",
            MiddleLeft.
    - "Scrollbar": anchors (1,0)–(1,1), pivot (1,1), size (20,0). Image `background`,
      Sliced. Scrollbar: BottomToTop, size 0.2, value 0, handle "Handle", target its Image.
      - "Sliding Area": stretched, size (−20,−20).
        - "Handle": anchors (0,0)–(1,0.2), size (20,20). Image `standard`, Sliced.
- **CreateScrollView:** "Scroll View": centre anchors, size (200,200). Image `background`,
  Sliced, colour (255, 255, 255, 100). ScrollRect:
  - content "Content", viewport "Viewport";
  - horizontal and vertical, scrollbars "Scrollbar Horizontal" and "Scrollbar Vertical", both
    visibilities AutoHideAndExpandViewport, both spacings −3;
  - movement Elastic, elasticity 0.1, inertia on, deceleration 0.135, sensitivity 1.

  Children:
  - "Viewport": stretched, pivot (0,1), size (0,0). Image `mask`, Sliced. Mask with the graphic
    hidden.
    - "Content": anchors (0,1)–(1,1), pivot (0,1), size (0,300). No graphic.
  - "Scrollbar Horizontal": anchors (0,0)–(1,0), pivot (0,0), size (0,20). Image `background`,
    Sliced. Scrollbar LeftToRight, size 0.2, value 0, handle "Handle".
    - "Sliding Area": stretched, size (−20,−20).
      - "Handle": anchors (0,0)–(0.2,1), size (20,20). Image `standard`, Sliced.
  - "Scrollbar Vertical": anchors (1,0)–(1,1), pivot (1,1), size (20,0). Image `background`,
    Sliced. Scrollbar BottomToTop, size 0.2, value 0, handle "Handle".
    - "Sliding Area": stretched, size (−20,−20).
      - "Handle": anchors (0,0)–(1,0.2), size (20,20). Image `standard`, Sliced.

Every rect not stated otherwise has pivot (0.5,0.5) and position (0,0). Where a size is stated,
anchors not given are the centre (0.5,0.5).

### Pointer move events (measured 2026-10-23)

Found in a mod play-test (issue #2): Resource Dashboard (Workshop 3671379574) does not load.
Its graph view implements `IPointerMoveHandler`, which this package lacks, so loading the mod's
types fails with a `TypeLoadException`. The game's `Assets.OnPrefabInit` then fails on the same
types, and the player exits while loading. No game assembly names the interface. Add it to
`src/UnityEngine.UI/package-required-types.tsv` and regenerate (`scripts/regen-packages.sh`):

```
UnityEngine.UI	UnityEngine.EventSystems.IPointerMoveHandler	# mods (Resource Dashboard's graph view): OnPointerMove
```

**Interface, from the package's metadata:**
- `IPointerMoveHandler : IEventSystemHandler`, with `void OnPointerMove(PointerEventData
  eventData)`;
- `ExecuteEvents.pointerMoveHandler`, a static `EventFunction<IPointerMoveHandler>` property
  beside `moveHandler`. It is not null.

**When it is sent.** Measured with `PROBE_SCENARIO=pointermove` (tools/UnityProbe,
`PointerMove.cs`, lines `PM …`). It drives the game's own current module, a
`StandaloneInputModule` (Klei's `VirtualInputModule` is there but disabled), through
`inputOverride`, a frame at a time. The test hierarchy is on an overlay canvas: P (400 × 300,
with enter, exit and move handlers) holds A (enter, exit and move handlers, and a second move
handler on another component), A's child A1 (no handler), B (move handler only), C (no
handler), D (move handler, not a raycast target) and E (a disabled move handler).

Move events are sent from `HandlePointerExitAndEnter(eventData, newEnterTarget)`, together with
enter and exit, with `ExecuteEvents.Execute` on each object (not `ExecuteHierarchy`). The
mouse's left-button data passes through it each frame, as before. Three cases:

1. **The new target is null, or the data's `pointerEnter` is null.** Every object in `hovered`,
   in list order, gets move and then exit. Move is sent here even when the pointer has not
   moved. The list is cleared. When the new target is null, `pointerEnter` becomes null and
   nothing more happens. Otherwise go on with case 3's enter part.
   - Measured: from A (hovered [P, canvas, A]) to a point off the screen, P gets move then exit,
     then A gets move and its second handler's move, then exit. Each sees `hovered` still
     [P, canvas, A].
   - Measured: with no target and nothing hovered, nothing is sent, moving or not.
2. **The new target is the current `pointerEnter`.** When the pointer is moving
   (`IsPointerMoving()`: a non-zero `delta`), each object in `hovered`, in list order, gets
   move. Nothing else is sent. When it is still, nothing is sent.
   - Measured: on A, moving: A (both handlers), then P. On A1 (hovered [A, P, canvas, A1]):
     A, then P. A1 has no handler, and the event does not bubble from it.
   - Measured: after entering B from P, `hovered` is [P, canvas, B], and a move goes to P
     before B. Entered objects are appended to the list.
3. **The target changed.** Find the common root, as before.
   - **Exit:** walk from the old `pointerEnter` up to the common root, not including it. Each
     object gets move, then exit, and is then removed from `hovered`. Move is sent even with
     a zero delta: measured when A moved away from under a still pointer (A gets move, then
     exit, with `delta` (0,0) and `IsPointerMoving()` false).
   - **Enter:** set `pointerEnter` to the new target. Walk from the new target up to the
     common root, not including it. Each object gets enter, then move, and is then appended
     to `hovered`. Move is sent even with a zero delta: measured for a press without
     movement, and for A moving back under a still pointer.
   - **Objects at or above the common root get no move in that frame,** although the pointer
     moved. Measured from A1 to P's bare area: A1 and A get move then exit; P gets nothing.
     From A to its child A1: nothing is sent to A or P.
   - Measured: from B to A (common root P), B gets move then exit, then A gets enter, then
     move.
   - Measured: from A to another canvas's object (no common root), A gets move then exit, then
     P gets move then exit. Each is removed as it goes: P sees `hovered` [P, canvas].

The event data in each call: `position` and `delta` of this frame's pointer data;
`pointerEnter` is still the old target during the exit walk, and the new one during the enter
walk; `pointerCurrentRaycast` is this frame's hit. `used` is false. A disabled handler
component, or one on an inactive object, gets nothing, and the other components still run
(`Execute`'s rule). An object with `raycastTarget` false is never the target, so it gets
nothing (D).

**Pressed buttons change nothing:** with the left button held, the same events are sent as
without it (measured from A across P to B while held). Releasing the button without moving
sends nothing more. Not measured: a press on an object with a drag handler. The probe's objects
have none, so `dragging` stays false and `pointerDrag` null.

This replaces the description of `HandlePointerExitAndEnter` under Input modules. That said
that exit goes to the whole `hovered` list when the enter is unchanged. Instead, an unchanged
enter sends only moves, and only while the pointer moves.

### Moved children of layout groups (measured 2026-10-23)

Found in a mod play-test: DraggableUI (Workshop 1870540864) moves a window by Alt + drag. Its
`DraggablePanel.Update` sets the screen's own `RectTransform.anchoredPosition` (the component's
`GetComponentInParent<RectTransform>()`, which is the screen's) every frame of the drag, from the
pointer minus the offset it had at the press, and stops on release. Over the details panel the
drag moves two screens, `RootMenuRight` and its child `DetailsScreen`. In the original both stay
where they were put (`DetailsScreen` 302 → −58). In ours `DetailsScreen` is back at 302 right
after the release. The hierarchy, from the original's dump:

- `BottomPanelRight`: `VerticalLayoutGroup`, `ContentSizeFitter`;
- `RootMenuRight`: `RootMenu`, `LayoutElement`, `ToggleGroup`, `VerticalLayoutGroup`,
  `ContentSizeFitter`, `DraggablePanel`;
- `DetailsScreen`: `DetailsScreen`, `VerticalLayoutGroup`, `LayoutElement`, `Image`,
  `DraggablePanel`.

**Measured** with `PROBE_SCENARIO=layoutcull` (tools/UnityProbe, `LayoutCull.cs`, lines `LC lay
…`; `lc-check.py`). The setup is a `VerticalLayoutGroup` G (300 × 400, spacing 10, upper left, no
force-expand) on an overlay canvas, with children A, B and C. Each child is an `Image` with a
`LayoutElement` (preferred 120 × 50; its own size 80 × 40). G counts its `SetLayoutHorizontal`
calls, and B and G count their `OnRectTransformDimensionsChange`. The configurations: with and
without a `ContentSizeFitter` on G (vertical preferred), child size control on both axes, on
neither and on one, B with `ignoreLayout`, and B with a `ContentSizeFitter` of its own. In
each, one change is made, then two frames are watched phase by phase. Then A's preferred
height changes (a re-layout from elsewhere), and one more frame is watched.

1. **Moving does not re-lay out.** Setting a child's `anchoredPosition`, `localPosition`,
   `position` or `localScale`, or the group's `anchoredPosition`, sends no
   `OnRectTransformDimensionsChange` to the child or the group, and marks nothing for rebuild:
   the group's layout calls stay at their count, for every configuration. The child stays
   where it was put, frame after frame. A drag that sets B's `anchoredPosition` every frame
   for three frames leaves B at the last value in every configuration. That includes B with
   its own `ContentSizeFitter`. A hierarchy made in that frame has one deferred re-layout of
   its own, in its third frame, which a drag in progress overrides at once.
2. **Changing a size re-lays out in that frame.** Setting a child's `sizeDelta`, `pivot` or
   anchors, or the group's `sizeDelta`, sends `OnRectTransformDimensionsChange` from the setter
   (once; twice for anchors, one per property). The group then re-lays out in that frame's
   canvas update, before `Canvas.willRenderCanvases` returns. A size the group controls is set
   back, and every position the group drives is set again.
3. **The next re-layout puts a moved child back.** When the group re-lays out for any reason,
   B is placed at its layout position again, in the canvas update of the frame the rebuild was
   asked in. This holds with child size control on or off, with or without a fitter on the
   group, and the moved axis does not matter. A child whose `LayoutElement` has `ignoreLayout`
   keeps both its position and its size. Measured to put B back:
   - `LayoutRebuilder.MarkLayoutForRebuild` on B or on G;
   - `SetLayoutDirty()` on B's `Image`, or disabling that `Image`;
   - a changed `LayoutElement` value on B or A;
   - a changed group setting (`spacing`);
   - `SetActive(false)` on a sibling, a new child, `SetAsLastSibling()` on B, and disabling
     and enabling the group;
   - `LayoutRebuilder.ForceRebuildLayoutImmediate(G)`, at once.
   - Nested groups: G (`VerticalLayoutGroup` + fitter) > D (`VerticalLayoutGroup`) > T1
     (`LayoutElement`) and T2 (`TextMeshProUGUI`). Moving D, then changing T1's preferred
     height or T2's text, re-lays out from G and puts D back. With a `LayoutElement` in
     place of D's group, T1's change re-lays out nothing (the root search stops at D), and D
     stays moved.

   Not putting B back: `SetVerticesDirty()`, a colour change, setting a `LayoutElement` value
   or a group setting to the value it has, setting a text to the text it has, and
   `Canvas.ForceUpdateCanvases()` with nothing marked.
4. **`OnRectTransformDimensionsChange` is sent once, from the change, and only for the rect's
   size**, on a canvas that is not pixel perfect (the probe's canvas; on a pixel-perfect one
   a move is told after the canvas update, see "Moves on pixel-perfect canvases" below). This
   includes changes made during the canvas update:
   - A rect resized or re-pivoted from a `Canvas.willRenderCanvases` handler gets the message
     once, from the setter. It gets nothing after the update.
   - A rect moved there (`anchoredPosition`, `localPosition`, `localScale`) gets none.
   - A child resized by its group during the layout rebuild gets one message, during the
     rebuild (`CanvasUpdateRegistry.IsRebuildingLayout()` true).
   - A child moved back by its group gets none, during the rebuild or after it. Across the
     whole run, B's message never came after the canvas update: 0 lines in phase R, against
     419 during the layout rebuild.

**Where ours deviates.** `CanvasSystem.RunCanvasUpdate` (`src/OniMoono.UnityEngine/Engine/UI/
CanvasSystem.cs`) sends `OnRectTransformDimensionsChange` again, after the update, to every
`RectTransform` that `RectTransformNative.Resolve` saw moved or resized during it
(`NoteChangedInUpdate`). That breaks rule 4 twice:
- **Moves.** A child moved back by its group gets a message after the update (B's count rises by
  one on every re-layout that moves it). When the child has a component that marks the layout
  on that message, such as a `ContentSizeFitter`, the layout is rebuilt again the next frame.
  While something keeps moving the child, the loop never ends. Measured in ours with B carrying
  a `ContentSizeFitter`: during the drag G re-lays out every frame (`SetLayoutHorizontal` + 1 a
  frame), and B is back at 40 in each frame's canvas update, where the original leaves it at
  −260. In the game, `RootMenuRight` (with its `ContentSizeFitter`) is moved by
  `BottomPanelRight`'s group. So once any re-layout of `BottomPanelRight` happens during the
  drag, it repeats every frame, and the one after the release puts `RootMenuRight` and
  `DetailsScreen` back. In the original nothing re-lays out after the release, so the moved
  windows stay.
- **Resizes.** Every re-layout that resizes or moves a rect is followed by another one: G's layout
  calls are one higher than the original's after each size change and each poke. A rect resized
  from a `willRenderCanvases` handler gets two messages (the original, one). One moved there
  through `anchoredPosition` gets one (the original, none).

**What to do.**
- Send `OnRectTransformDimensionsChange` only when the rect's size or its pivot-relative rect
  changes, from the change itself, and never again after the canvas update.
- A position change (`anchoredPosition`, `localPosition`, `position`, scale) sends nothing,
  in the canvas update or out of it.
- `CanvasTests.ARectChangedDuringTheCanvasUpdateHearsOfItAgainAfterIt` asserts the re-send. The
  news-tile fix of 2026-10-20 (PLAN.md, from PerfProbe `layout.txt`) relied on it, and that
  trace attributed the tiles' second message to the re-send. This probe sees no second message
  for a rect resized in the update. Re-check the tiles before deciding: what sends their second
  message in the original, and whether it reaches only rects whose size changed. Moves must not
  be re-sent in any case.

| Case (`lay …`) | original: B after the change, after the re-layout | ours before |
|---|---|---|
| csf.cwh, B `anchoredPosition.x` −200 | −140 held, no layout call; back at 60 on A's change | the same |
| csf.cwh, B `localPosition`, `position`, `localScale`; G `anchoredPosition` | held, no layout call | the same |
| csf.cwh, B `sizeDelta.x` +30 | width back to 120 in that frame's update; 1 message to B | the same, 2 layout calls and 2 messages |
| csf.none, B `sizeDelta.x` +30 (size not controlled) | width 110 kept, x 55 in that frame's update | the same |
| csf.cwh.ignB, B moved −200 | held, also after A's change | the same |
| csf.none.Bcsf, drag (B set each frame, three frames) | −260 held, layout calls unchanged | back at 40 every frame, one layout call a frame |
| any, drag without a fitter on B | the last value held | the same |
| poke `SetVerticesDirty`, colour, same values, `ForceUpdateCanvases` | B stays moved | the same |
| poke `MarkLayoutForRebuild(B)`, `SetLayoutDirty`, sibling inactive, `SetAsLastSibling` | B put back that frame | the same, with one extra layout pass |
| nested, D moved, T2 text changed | D put back | the same, with one extra layout pass |
| solo rect, `anchoredPosition` set in `willRenderCanvases` | no message | 1 message after the update |
| solo rect, `sizeDelta` or `pivot` set in `willRenderCanvases` | 1 message | 2 messages |
| DraggableUI, `DetailsScreen.anchoredPosition.x` after the drag | −58 (from 302) | 302 |

### Moves on pixel-perfect canvases (measured 2026-10-24)

Found in QA's dogfood `intro`: after the Spaced Out intro video, the main menu's news tiles
(`MOTD`, a `HorizontalLayoutGroup` of three `MotdBox` tiles) sat stacked at the row's right
edge. Each tile's `ImageButton` has a `MotdBox_ImageButtonLayoutElement`, whose preferred width
is its height × a ratio, set on `OnRectTransformDimensionsChange`. The row first lays out with
the buttons' heights still unset, so with widths of 0. PerfProbe's `layout.txt` trace in the
original shows the buttons hearing `OnRectTransformDimensionsChange` again after each frame's
canvas update, from the engine (the managed stack holds only the handler), with
`IsRebuildingLayout()` false; each marks the layout, and the row lays out again in the next
frame, three frames in all, until nothing moves. The probe above saw no such message: its
canvas is not pixel perfect. The front end's canvas (`FrontEndManager`) is.

**Measured** with `PROBE_SCENARIO=childdims` (tools/UnityProbe, `ChildDimensions.cs`, lines
`CD …`; `PROBE_CD_PART=extra` for the second set). A rect P (200 × 100) holds C (50 × 50,
centre anchors), which holds GC; P also holds S (stretched). Each has a spy. One change to P
per case, made in a coroutine, in `Canvas.willRenderCanvases` or during the layout rebuild (an
`ICanvasElement` registered for it); on canvases at scale 1 and 0.86, pixel perfect or not;
with P an `Image`, C a `RawImage` and GC a `TextMeshProUGUI`, or with no graphics. Then P as a
child of a `HorizontalLayoutGroup`, resized or moved by it; then a copy of the news row.

- **Not pixel perfect:** no message beyond the size messages of rule 4, in any case.
- **Pixel perfect:** every active GameObject that has a `CanvasRenderer` and whose transform
  moved gets `OnRectTransformDimensionsChange` once, after the canvas update (after the
  `willRenderCanvases` handlers, before the end of the frame), whenever in the frame the move
  was made: before the update, in it, during the layout rebuild or in a `willRenderCanvases`
  handler. A move made after rendering (end of frame) is told in the next frame's.
  - A move is any change of the transform's place: `anchoredPosition`, `localPosition`,
    `position`, `localScale`, `localRotation`, a parent set (with or without keeping the world
    position), a sub-pixel move (0.3), and a move undone in the same frame. Two moves in a
    frame are told once. A drag is told every frame.
  - It reaches the moved rect and every descendant whose place moves with it: C and GC when
    P moves. A pivot change moves the children's rects but not P's transform: C and GC are
    told after the update, P only by the setter (rule 4).
  - A size change alone is not a move: P resized in place hears it from the setter only; C
    (centre anchors) does not move and is told nothing. P resized and moved by its group
    hears both: the setter's message during the rebuild and one after the update.
  - Only GameObjects with a `CanvasRenderer`: an `Image`, a `RawImage`, a text, a
    `CanvasRenderer` alone, a disabled `Image`, one with alpha 0 and a culled one all count. A
    rect without one (S, a `LayoutElement` alone) is told nothing. Inactive objects are told
    nothing.
  - Not a move: setting `anchoredPosition` to its value, `SetAsLastSibling`, activating an
    object, turning `pixelPerfect` on. An object made or cloned in the frame (a new object, an
    `Instantiate` into a parent) is not told of its placement. A move made after it has its
    `CanvasRenderer` is told, in the frame it was made too (below, "Objects made in the frame").
  - The canvas that decides is the nearest one, as `Canvas.pixelPerfect` reads: a nested
    canvas that overrides `pixelPerfect` off is not told under a pixel-perfect root; one that
    overrides it on is told under a root that is not. A nested canvas that inherits follows its
    root.
  - Changing the root canvas's `scaleFactor` moves everything under it: P is told.
- **The news row:** a copy (row > tiles with a `VerticalLayoutGroup` > a header and a button
  whose preferred width follows its height) stays at the row's first layout (preferred width
  56) on a canvas that is not pixel perfect, at scale 1 and 0.86, with a `Mask` and a
  `RawImage` child with an `AspectRatioFitter` too. On a pixel-perfect canvas the buttons are
  told after the update, and the row reaches 908.5 in the next frames, as in the game.

**Where ours deviated.** Nothing was sent for a move on any canvas, so the news row kept its
first layout whenever nothing else re-laid it out (the intro path; the `menu` scenario happened
to get another layout). The re-send withdrawn on 2026-10-23 had covered it, but for every rect
on every canvas, sizes included.

#### Objects made in the frame (measured 2026-10-24)

Found by a player at 2560 × 1440 on Windows: the news row stayed at its first full layout
(preferred width 8, the three tiles stacked at the row's right edge, one showing past the
screen's edge). In the original at 2560 × 1440 (PerfProbe `layout.txt`, MOTD images cached)
the news data arrives in the frame the main menu is made: `MainMenu_Motd.Setup` hides the tiles,
the response shows them again and the row lays out once in that frame's canvas update, with the
buttons' widths at 0. The buttons are told after that update, in that same frame, and the row
reaches 908.5 two frames later.

**Measured** with `PROBE_SCENARIO=childdims PROBE_CD_PART=born` (lines `CD born.…`), on the
pixel-perfect probe canvas. Each case makes N (a rect with a spy, then an `Image` and a
`LayoutElement`) in a coroutine and changes it in the same frame:

| Case | told after that frame's canvas update |
|---|---|
| made, nothing else (`control`) | no |
| `anchoredPosition` set after the `Image` was added (`moveNow`) | yes |
| moved in `Canvas.willRenderCanvases` (`moveInRender`) | yes |
| its parent moved (`parentMoved`) | yes, N and its sibling |
| made as a child of a `HorizontalLayoutGroup`, placed by it in the canvas update (`group`) | yes, after the setter's messages during the rebuild |
| `Instantiate` into a parent (`clone`) | no |
| `Instantiate`, then `anchoredPosition` set (`cloneMoved`) | yes |
| `Instantiate` into a layout group (`cloneInGroup`) | yes |
| deactivated, moved, activated, in that order (`inactiveActivatedMoved`) | no, nor later |
| moved before its `Image` was added (`imageAfterMove`) | no |
| made, moved in the next frame (`moveNextFrame`) | yes, in that frame |

So a move counts from the moment the object has a `CanvasRenderer` and is active: the
placement that comes with making, cloning or parenting it before then is not told, and neither
is a move made while it is inactive, but every move after that is, whenever in the frame the
object was made.

**Where ours deviated.** Ours told no move of an object whose `CanvasRenderer` was made since
the last pass, so the news row stuck at its first full layout whenever that layout came in the
frame the menu was made: in ours only when the data arrives that early (reproduced on Linux by
a test mod handing `MainMenu_Motd` its data inside `Setup`, which gives the player's picture
exactly; what brought the Windows run there was not traced). It also told a move made while the object was inactive once it was activated.

### RectMask2D culling (measured 2026-10-23)

Found in a mod play-test: Modify Difficulty Settings (Workshop 2962220957). The label
`…/CustomGameSettingsChanger/Content/ScrollRectContainer/TogglePrefab#10/Label` ("Care Packages",
a `LocText`, GRAYSTROKE REGULAR SDF 14 pt, alignment `Left`) has its `CanvasRenderer.cull` true
in the original and false in ours. Its rect runs from world y 78.26 to 109.22. The
`RectMask2D` on `Content` has its rect's bottom at 94.26 and a `padding` of (0, 8, 0, 8), so
the clip's bottom is 94.26 + 8 × 1.032 = 102.52 (canvas scale 1.032). The label's mesh
(local y −7.94 to 6.34, pivot at world y 93.74) ends at 100.29, below the clip. The rect's top
reaches above it. Its sibling `Background` (an `Image`, 80.84 to 106.64) and that one's
`Checkmark` (84.26 to 103.23) cross the edge and are not culled in either player. The pixels
match: what is culled lies under the clip anyway.

**Measured** with `PROBE_SCENARIO=layoutcull` (lines `LC cull …`), at canvas scale 1 and 1.032.
Each rect is 200 × 30, placed with its centre at the mask's bottom (or top) edge plus d, for d
from −70 to 70 in steps of 0.5 (and ±0.01, ±0.1, ±0.25). The mask is a `RectMask2D` of 600 ×
200 at the canvas centre, also with a bottom padding of 10, a top padding of 10, or a softness
of 20. The kinds:
- `Image`s at pivots (0, 0.5), (0.5, 0.5) and (0.5, 1);
- `TextMeshProUGUI`s in NotoSans-Regular 14 pt, with extra padding and kerning: "Care
  Packages" aligned left, top left, bottom left and centre, at pivots (0, 0.5), (0.5, 1) and
  (0.5, 0); "", " " and "AAA"; and a 100-high rect, aligned left and top left.

- **The clip rect** is the mask's rect in the root canvas's space, shrunk by `padding` (x left, y
  bottom, z right, w top), intersected with the enclosing masks' rects, as already described.
  `softness` does not change it: with softness 20 the cull offsets are those without it.
- **The test is `Rect.Overlaps` with exclusive edges:** a graphic is culled when its cull rect
  does not overlap the clip rect. Touching edges count as not overlapping. An overlap of any
  size, however partial, keeps the graphic. Measured: an `Image` whose top lies exactly on the
  clip's bottom (d = −15) is culled; at d = −14.5 it is not. At the top edge, d = 15 is culled
  and 14.5 is not. With a bottom padding of 10, the edge moves up by 10 (d = −5 culled). At
  scale 1.032 the exact-touch case falls either way, through float rounding.
- **An `Image` (the `MaskableGraphic` default) culls with its rect:** the four corners of its
  `RectTransform` rect in the root canvas's space. The pivot makes no difference.
- **A `TextMeshProUGUI` culls with its mesh's bounds,** not with its rect and not with
  `textBounds`. The bounds are the axis-aligned box of all the mesh's vertices in its local
  space, mapped to the root canvas's space. Vertices TMP leaves unused (a space's quad, the
  slack of the vertex buffer: "Care Packages" has 52 vertices, "AAA" 16) sit at the local
  origin. So the box always includes the pivot point. An empty text, or one of white space
  alone, is a point at the pivot (4 vertices).
  - Measured for every text kind: the offset at which it is culled is the one where that box
    stops overlapping, to the 0.5 step. "Care Packages" centred vertically has a box from
    −12.29 to 8.71 about the pivot and is culled from d = −9 at the bottom edge, where its rect
    would be from d = −15. Aligned to the top of a 100-high rect, its glyphs run from 28.2 to
    49.2 above the pivot, but the box starts at 0. So it is culled at the top edge from d = 0,
    not from d = −28.
  - In the game, the box explains the label above: it ends 2.2 below the padded clip.
- **When the cull is evaluated:** in the clipping step of the canvas update (after the layout
  rebuild, before the graphic rebuild), for each graphic under the mask, when:
  - the clip rect changed (the mask moved, measured: culled in that frame);
  - the graphic moved, through its own `anchoredPosition` or `localPosition` or a parent's;
  - its scale or its rect's size changed (an `Image` grown into the clip is shown in that
    frame, one shrunk out of it is culled in that frame);
  - a text's mesh changed. After a text or font-size change, the cull follows the new mesh in
    the same frame's update. After an alignment change, it follows one frame later. A text set
    to "" keeps its cull. Nothing else re-evaluates it: a culled text whose mesh stays put stays
    culled.
  - A text made in place is culled with its first mesh from its first canvas update. That
    mesh comes from the same update, not from the empty mesh before it: a text whose pivot
    lies 0.5 below the clip, with glyphs reaching into it, is not culled.

**Where ours deviates.**
- `TextMeshProUGUI` has no `Cull` of its own, so it culls with its rect
  (`MaskableGraphic.Cull`, `RootCanvasRect`). Every text kind is culled at the `Image`'s offsets
  (d = −15 at the bottom, d = 15 at the top; −50 and 50 for the 100-high rect). Give
  `TMP_Text`/`TextMeshProUGUI` a `Cull(clipRect, validRect)` that tests the bounds of the mesh
  its canvas renderer was given. Our TMP builds that mesh as the original does: the probe's
  `meshLocal` boxes match the original's, unused vertices at the origin included. Map the
  corners of the box with the text's transform into the root canvas's space, as
  `RootCanvasRect` maps the rect's. Then re-evaluate it when the mesh changes, as above.
- `RectMask2D.PerformClipping` (`src/UnityEngine.UI/Core/Mask.cs`) re-culls a graphic only when
  `canvasRenderer.hasMoved`, which follows the transform, not the rect. An `Image` resized
  across the edge keeps its old cull (`time img size in` / `size out`). A size change must
  count as a move here.
- Two values the probe logged on the way: our `TMP_Text.bounds` is the glyphs' box, but the
  original's equals the mesh's bounds (pivot included). Our empty text has 0 vertices, where
  the original has 4. The original's `textBounds` of an empty text is the sentinel box
  (±2147483648); ours is a point at the pivot. Neither is used by the cull above.

| Case (`cull …`) | original: culled from | ours before |
|---|---|---|
| `Image`, any pivot, bottom edge | d ≤ −15 (top on the edge) | the same |
| `Image`, top edge | d ≥ 15 | the same |
| `Image`, bottom padding 10 | d ≤ −5 | the same |
| `Image`, softness 20 | d ≤ −15 | the same |
| "Care Packages", left (box −12.29..8.71) | d ≤ −9 | d ≤ −15 |
| the same, top left (box −6.80..14.20) | d ≤ −14.5 | d ≤ −15 |
| the same, bottom left (box −17.77..3.23) | d ≤ −3.5 | d ≤ −15 |
| the same, top edge | d ≥ 12.5 | d ≥ 15 |
| "AAA" (box −8.93..8.09) | d ≤ −8.5 | d ≤ −15 |
| "" and " " (a point at the pivot) | d ≤ 0 (bottom), d ≥ 0 (top) | d ≤ −15, d ≥ 15 |
| 100-high rect, top left, top edge (glyphs 28.2..49.2, box 0..49.2) | d ≥ 0 | d ≥ 50 |
| "Care Packages", bottom padding 10 | d ≤ 1 | d ≤ −5 |
| `Image` grown across the edge (`sizeDelta.y` +100) | shown in that frame | stays culled |
| text whose box is out, rect in, nothing changes | culled | not culled |
| text whose box is out after an alignment change | culled one frame later | not culled |
| Modify Difficulty Settings, "Care Packages" label | culled; shown when scrolled 100 up, culled again back | not culled |

**An exception: TextMeshPro's caret graphic** (measured 2026-10-24). `TMP_SelectionCaret`, the
graphic a `TMP_InputField` draws its caret and selection with, is registered with the masks
and clipped like any `MaskableGraphic`, but its `Cull` ignores its rect and mesh: it is culled
only when the clip rect is invalid. `textmeshpro.md` ("The caret graphic and RectMask2D")
records the measurement. The same probe found that the original re-culls an `Image` a frame
after a direct `Cull` call un-culled it, with nothing moved, and the caret graphic under an
invalid clip likewise. This implementation's `RectMask2D` keeps the cull its pass left on each
graphic and culls one again in its next pass when its cull changed since (a direct call),
which reproduces both (`ic-check.py diff … cull`: no step differs).

### Scrollbar presses on the track (measured 2026-10-23)

Found through the vanilla Consumables screen (`tests/e2e/consumables-scroll.yaml`). Its header
row has a horizontal `Scrollbar` (`…/prefab_row_header/Border/HorizontalScrollbar`, 760 wide,
`size` 0.4496, `value` 0) driving the header's `KScrollRect`. A click on the track right of the
handle, at the track's centre, moved the value to 0.49994 in the original: the handle centred
on the click. Ours moved it by one `size`, to 0.4496, and kept stepping while the button was
held. (The rows of that table did not follow the header in ours either; that is the layout
re-send described under "Moved children of layout groups", not this.)

**Measured** with `PROBE_SCENARIO=scrollbar` (tools/UnityProbe, `ScrollbarPress.cs`, lines `SB
…`). Each case builds a fresh `Scrollbar` on an overlay canvas of scale 1: the track an `Image`
of 400 × 20 (20 × 400 vertical) at the screen centre, a "Sliding Area" child stretched over it,
and a "Handle" child `Image` in that as `handleRect`. The case sets `direction`, `size`,
`value` and `numberOfSteps`, waits two frames, then drives the game's `StandaloneInputModule`
through `inputOverride`: a press at a fraction p of the track's length (0 at the left or bottom
end), held for 1 to 60 frames (at about 250 frames a second, so up to about 0.25 s), sometimes
moving while held, then a release. Every frame logs `value` and the handle's anchors; every
`onValueChanged` logs its argument.

Write the track position of the press along the scrollbar's direction as q: q = p for
LeftToRight and BottomToTop, q = 1 − p for RightToLeft and TopToBottom.

1. **A press on the handle** changes nothing. Pressing it and moving drags it, keeping the
   offset at which it was grabbed: `size` 0.45, `value` 0, pressed at p = 0.1 and moved to
   p = 0.5 gives 0.72727 (the handle's start moved by 0.4 of the track; 0.4 / 0.55).
2. **A press on the track outside the handle** sets, in the frame of the press, the value that
   puts the handle's centre on the press point: `value` = clamp01((q − `size` / 2) / (1 −
   `size`)), then `numberOfSteps` rounding as for any value set. `onValueChanged` is invoked
   once, with that value (not when the value is unchanged).
3. **While the button is held,** the handle follows the pointer as a drag from its centre: the
   value is again (q − `size` / 2) / (1 − `size`) for the pointer's current q, clamped, each
   frame the pointer moves. This holds below the event system's drag threshold (20 px):
   a move of 10 px gave the followed value. A pointer that stays still changes nothing: there
   is no stepping repeat, however long the press is held (the press point is re-applied,
   rule 4, which leaves the value as it is). The release changes nothing.

| Case | original | ours before |
|---|---|---|
| size 0.4496, value 0, p 0.5, held 2 frames | 0.5 at the press | 0.4496 |
| size 0.45, value 0, p 0.5, held 1 or 30 frames | 0.5 | 0.45 |
| size 0.45, value 0, p 0.6 | 0.68182 | 0.45 |
| size 0.45, value 0, p 0.75 | 0.95455 | 0.45, then 0.9 on the 2nd held frame |
| size 0.45, value 0, p 0.9 or 0.99, held 2 or 60 | 1 | 0.45, 0.9, then 1 |
| size 0.45, value 0, p 0.46 (just right of the handle) | 0.42727 | 0.45 |
| size 0.45, value 0, p 0.2 (on the handle) | 0, nothing invoked | the same |
| size 0.2, value 0, p 0.3 | 0.25 | 0.2 |
| size 0.2, value 0, p 0.9, held 60 | 1 | 0.2, 0.4, 0.6, 0.8, 1 a frame apart |
| size 0.2, value 0.5, p 0.1 | 0 | 0.3, then 0.1 |
| size 0.2, value 0.5, p 0.9 | 1 | 0.7, then 0.9 |
| size 0.2, value 1, p 0.1, held 60 | 0 | 0.8 down to 0 |
| size 0.1, value 0, p 0.55, held 60 | 0.55556 | 0.1 up to 0.5 |
| size 0.45, value 0, steps 5, p 0.75 | 1 (0.95455 rounded) | 0.5, then 1 |
| size 0.2, value 0, steps 3, p 0.9, held 60 | 1 | 0, nothing invoked |
| RightToLeft, size 0.45, value 0, p 0.25 (q 0.75) | 0.95455 | 0.45, then 0.9 |
| BottomToTop, size 0.3, value 0, p 0.8 | 0.92857 | 0.3, 0.6, then 0.9 |
| TopToBottom, size 0.3, value 0, p 0.8 (q 0.2, on the handle) | 0, nothing invoked | the same |
| size 0.2, value 0, p 0.9, then moved to p 0.3 while held | 1, then 0.25 | 0.2, then 0.25 |
| size 0.2, value 0, p 0.9, then moved to p 2 (off the track) while held | 1, stays 1 | 0.2, then 1 |
| size 0.2, value 0, p 0.5, then moved 10 px (p 0.525) | 0.5, then 0.53125 | 0.2, then 0.53125 |
| size 0.2, value 0, p 0.5, then moved 30 px (p 0.575) | 0.5, then 0.59375 | 0.2, then 0.59375 |

What follows a value change (the handle's anchors, `onValueChanged`, a linked `ScrollRect`
setting its normalized position) is as described above; only the value a press on the track
produces, and the repeat, differ. Not measured: touch input, a press with a button other than
the left, and presses on a scrollbar whose `interactable` is false.

**The press while held, and the protected `ClickRepeat` and `Update` (measured 2026-10-23,**
`PROBE_SB_PART=clickrepeat` and `clickrepeat2`, a `Scrollbar` subclass of size 0.2 calling
both `ClickRepeat` overloads by hand, one `MoveNext` a frame, and through `StartCoroutine`).
The press of rule 2 is not applied once only:

4. **A press holds its point.** From a press (on the handle or not) until the release or the
   start of a drag, the press point is re-applied immediately and then at the end of every
   frame: when it lies outside the handle, the value becomes the one rule 2 gives for it. With
   nothing else moving the value this changes nothing (rule 3's still pointer). A value set
   while the button is held is put back at the end of that frame when the handle no longer
   covers the press point: pressed on the track at 0.75 (0.8125), then set to 0.2 from code,
   the value is 0.8125 again by the next frame, with `onValueChanged` for 0.2 and then
   0.8125. Pressed on the handle (value 0.5, at 0.5), a value of 0.45 stays (the handle still
   covers 0.5); a value of 0 is put back to 0.5.
5. **`ClickRepeat(Vector2 screenPosition, Camera camera)`** returns an enumerator. While a
   press of rule 4 holds, each `MoveNext` applies `screenPosition` as rule 4 applies the press
   point (outside the handle: the value of rule 2 for it, `onValueChanged` when it changes;
   on the handle: nothing), and returns true with `Current` a `WaitForEndOfFrame`. Once the
   press has ended (released, or a drag begun), `MoveNext` returns false and changes nothing.
   On a scrollbar that has never been pressed, the first `MoveNext` throws a
   `NullReferenceException` ("routine is null") and changes nothing, wherever the point is.
   Both repeats run at the end of each frame, the press's first: while the button was held
   still on the handle (value 0.5) a subclass's `ClickRepeat` at 0.9 gave `onValueChanged` 1,
   then 0.5 at the end of the frame, then 1 again from the subclass's next step; its value
   was 1 when read in the next frame's `Update`. Under `StartCoroutine` the first step runs
   inside the call.
6. **`ClickRepeat(PointerEventData eventData)`** is the same with the point
   `eventData.pointerPressRaycast.screenPosition`; `position` and `pressPosition` are not
   read. (The camera was not measured; every measurement used an overlay canvas.)
   The handle's anchors along the axis start at v − v × `size` (v the clamped value; float
   arithmetic) and end `size` further: in all 945 left-to-right states these probes logged,
   v × (1 − `size`) differed in the last bit 79 times (v 0.8125, size 0.2: 0.65 to
   0.849999964, not 0.65000004 to 0.85).
7. **`Update`** (protected, virtual) has no measured effect: a subclass whose override does not
   call `base.Update()` gave the same values, anchors and `onValueChanged` for a value and a
   size set from code and for a press on the track held and released.

### Legacy Text glyph placement (measured 2026-10-24)

Found with Geyser Swapper (Workshop 3670972563): its converter panel is built from legacy
`Text`s in `Resources.GetBuiltinResource<Font>("Arial.ttf")` (LegacyRuntime), at 13, 11 and
10 points on the details screen's canvas (scale 0.86, so 11, 9 and 8 pixels). The ui_dumps
match, but the shot differs by 4.5 %. The original draws the 9-pixel labels 2 to 3 pixels
narrower per line. The cause is not rounding. On Linux the original draws LegacyRuntime's
glyphs from **FreeSans**, not Liberation Sans. Every advance is still a whole pixel. Liberation
Sans's rounded advances happen to equal FreeSans's at 8 pixels ("Choose a target type and
start conversion." is 141 pixels in both). At 9 pixels they are 179 against the original's
166, and Liberation's unrounded sum, 169.5, happens to be close to it.

**Measured** with `PROBE_SCENARIO=legacytext` (tools/UnityProbe, `LegacyText.cs`, lines `LX …`,
10 s into the main menu, about a minute; `PROBE_LX_PART=raster,ci,gen,anchors,align,fonts,text`
picks parts). `tools/UnityProbe/lx-check.py rule LOG` checks every rule below against a log,
and `lx-check.py face LOG FONT STYLE` checks the CharacterInfo table against a font file
through FreeType. Fonts were hidden from the original by bind-mounting an empty folder over
their directory in a user and mount namespace (`unshare --user --map-root-user --mount`).
Measured on this machine: Ubuntu with fonts-freefont-ttf 20211204 (FreeSans "Version
0412.4271"), DejaVu, Liberation 2.1.5, TLWG, Unifont, IPA Gothic, WenQuanYi and Ubuntu. These
rules held for every case the probe logged:
- 2090 CharacterInfos per style for Normal and Bold (95 ASCII characters at 5 to 26 pixels),
  and 286 each for Italic and BoldAndItalic;
- 3544 one-line generations: 15 strings at every font size and scale factor whose product is 5
  to 25 pixels (scales 0.5, 0.6, 0.75, 0.8, 0.86, 0.9, 1, 1.1, 1.25, 1.5 and 2), with styles,
  anchors and widths at the panel's sizes;
- 680 aligned generations;
- 480 `Text` components.

#### The face

- **LegacyRuntime has no font data.** Its `fontNames` is ["Arial"]. Its serialized metrics
  are at `m_FontSize` 13: `m_Ascent` 11.7685546875, `m_Descent` −2.7548828125 and
  `m_LineSpacing` 14.94873046875. `m_KerningValues` holds 21 pairs (below), and
  `m_CharacterPadding` is 1. `Font.lineHeight` reads 14 and `ascent` 11.
- **Glyphs come from the OS fonts.** The `fontNames` are tried in order, by exact family name
  among the installed fonts (`Font.GetOSInstalledFontNames()`). When none is installed (Arial
  never is here), a default chain is used. The chain is per glyph: each character comes from
  the first face in the chain that has it. On this machine the chain is **FreeSans, then
  DejaVu Sans, then Liberation Sans**:

  | installed | LegacyRuntime's glyphs at 20 px, `!_~JNTjmW1` advances |
  |---|---|
  | all | FreeSans: 7,10,10,11,15,13,5,16,19,11 |
  | FreeSans hidden | DejaVu Sans: 8,10,17,6,15,12,6,19,20,13 |
  | FreeSans and DejaVu hidden | Liberation Sans: 6,11,12,10,14,12,4,17,19,11 |
  | those three hidden | none: every advance 0 (other families were installed: Loma, Ubuntu, Unifont, WenQuanYi, IPA Gothic) |

  - U+2125 and U+2140, which FreeSans lacks and DejaVu Sans has, came from DejaVu Sans
    (advances 12 and 16 at 20 px) with FreeSans installed.
  - U+0591, in Liberation Sans alone, came from Liberation Sans: advance 0, box −3..3 ×
    −5..1.
  - U+4E2D, in no face of the chain (WenQuanYi has it), came from none. It has advance 0 and an
    empty glyph box −1..1 × −1..1, and `GetCharacterInfo` still returns true.
  - `HasCharacter` returned true for all four.
  - Faces outside the chain that are not installed here (Arial, Helvetica, …) could not be
    told apart from absent links of the chain.
- **The chain for other fonts.** Fonts made with `Font.CreateDynamicFontFromOSFont` follow the
  same lookup. A missing name ("Arial", "Helvetica", "sans-serif", "Sans", "", a made-up
  name) gives the FreeSans chain. A list takes its first installed name: ["NoSuchFont",
  "Liberation Sans"] and ["Arial", "Liberation Sans"] gave Liberation Sans. Unlike
  LegacyRuntime, such fonts take their line metrics from the face: "Arial" (FreeSans) reads
  `lineHeight` 14 and `ascent` 10. LegacyRuntime keeps its serialized 14 and 11, with FreeSans
  and with DejaVu Sans alike.
  - The same log's other fonts at 13 (FreeSans 14/10, DejaVu Sans 15/12, Liberation Sans
    14/11, Ubuntu 14/12) fit one rule: OS/2's typographic ascender, descender and line gap
    when its USE_TYPO_METRICS bit (fsSelection bit 7) is set (FreeSans, Ubuntu), else hhea's;
    the line spacing is ascender − descender + line gap, both scaled to the size and read
    rounded down (re-read from the log by the implementation, 2026-10-24).
- **Styles use the family's own faces.** Bold, Italic and BoldAndItalic glyphs are those of
  FreeSansBold, FreeSansOblique and FreeSansBoldOblique, all 100 %. They are not synthesized
  from the regular face.
- On Windows "Arial" is installed, so the chain is not reached there. That was not measured
  (no Windows runs).

#### Glyph metrics (Font.GetCharacterInfo after RequestCharactersInTexture)

For a face loaded at an integer pixel size p (the em is p pixels) with FreeType, without
hinting:
- `advance` is the face's advance rounded to the nearest whole pixel (26.6 value plus 32,
  divided by 64, rounded down). The obsolete float `width` holds the same whole number: no
  fractional advance is kept at any size, 5 to 26 pixels, in any style.
- The box is the rendered bitmap's box **grown by one pixel on every side** (the
  `m_CharacterPadding` of 1). `minX` = bitmap_left − 1, `maxX` = bitmap_left + width + 1,
  `minY` = bitmap_top − rows − 1, `maxY` = bitmap_top + 1. `glyphWidth` and `glyphHeight` are
  the grown sizes, and the UVs cover the grown box.
  - A space has the box −1..1 × −1..1.
  - At 9 px, "C": advance 6, −1..8 × −2..8. At 20 px, "!": advance 7, box 1..6.
- With hinting, FreeType's boxes match only 951 of 2090. Liberation Sans's metrics match 834
  of 2090.
- Font size 0 means the font's own size, 13.

#### Pixel size

The text is laid out and rasterized at **p = floor(fontSize × scaleFactor)**, the product in
float32. `TextGenerator.fontSizeUsedForBestFit` reports p, also without best fit: 9 for 11
points at 0.86. Rasterized sizes, one new character each, read back by `GetCharacterInfo`:

| fontSize × scale | p | fontSize × scale | p |
|---|---|---|---|
| 11 × 0.86 = 9.46 | 9 | 9 × 0.99 = 8.91 | 8 |
| 10 × 0.86 = 8.6 | 8 | 9 × 1.01 = 9.09 | 9 |
| 13 × 0.86 = 11.18 | 11 | 7 × 1.3 = 9.1 | 9 |
| 10 × 0.95 = 9.5 | 9 | 20 × 0.43 = 8.6 | 8 |
| 19 × 0.5 = 9.5 | 9 | 16 × 0.86 = 13.76 | 13 |

The glyph was in the texture at p alone, in the requested style alone.

#### Horizontal placement on a line

With `adv(c)` and `minX(c)`/`maxX(c)` the CharacterInfo values at p and the style:
- **Pen:** it starts at the line's start x, an integer (below). For each character: its
  `UICharInfo.cursorPos.x` is the pen. Then, if the character and the one before it are a
  kerning pair, the pen moves by the pair's kerning. The glyph's quad spans pen + `minX` to
  pen + `maxX`. Then the pen moves by `adv`.
  - So a kerned character's cursor is the position *before* its kerning, but its quad and
    every later cursor include the kerning.
  - `UICharInfo.charWidth` is `adv` alone, without kerning.
  - The extra character info after the last character (the caret slot) is at the final pen,
    with width 0.
- **Kerning** is LegacyRuntime's serialized `m_KerningValues`, not the face's kerning (FreeSans's
  `kern` and `GPOS` pairs, such as AV −30/1000, have no effect). Each pair is −1 pixel at
  `m_FontSize` 13, scaled to p and rounded to the nearest integer, so round(−p / 13): 0 at 5
  and 6 px, −1 at 7 to 19 px, −2 at 20 to 25 px. The 21 pairs, by character:
  `F,` `F.` `P,` `P.` `T,` `T.` `T:` `T;` `Ta` `Tc` `Te` `To` `Ts` `V,` `V.` `Y,` `Y-` `Y.` `Ye`
  `Yo` `Yq`.
  - The same pairs apply in Bold and Italic ("Ta": −1 at 8, 9 and 11 px).
- **No quad for a space** (U+0020): the space advances the pen and has a UICharInfo, but adds no
  vertices. Other characters, including those with empty bitmaps, get a quad (U+4E2D's empty
  box, above).
- **The preferred width** (`GetPreferredWidth`, `rectExtents.width` with `updateBounds`) is the
  final pen of the widest line, an integer number of pixels. `Text.preferredWidth` divides it by
  the canvas scale: "Geyser Converter" at 11 pt on 0.86 is 69 px, 80.23256 units.

**The panel's labels** (UpperLeft, from x 0):

| text | pt | p | cursors x (last: the end) | width |
|---|---|---|---|---|
| Choose a target type and start conversion. | 11 | 9 | 0,6,11,16,21,25,30,32,37,39,42,47,50,55,60,63,65,68,72,77,82,84,89,94,99,101,105,108,113,116,119,121,126,131,136,140,145,148,152,154,159,164,166 | 166 |
| Choose a target type and start conversion. | 10 | 8 | 0,6,10,14,18,22,26,28,32,34,36,40,43,47,51,53,55,57,61,65,69,71,75,79,83,85,89,91,95,98,100,102,106,110,114,118,122,125,129,131,135,139,141 | 141 |
| Choose a target type and start conversion. | 13 | 11 | 0,8,14,20,26,31,37,40,46,49,52,58,62,68,74,77,80,83,88,94,100,103,109,115,121,124,129,132,138,142,145,148,154,160,166,171,177,181,186,188,194,200,203 | 203 |
| Target type | 11 | 9 | 0,6,10,13,18,23,26,28,31,35,40,45 (charWidth 6,5,3,5,5,3,2,3,4,5,5,0: "Ta" kerned −1) | 45 |
| Target type | 10 | 8 | 0,5,8,11,15,19,21,23,25,29,33,37 | 37 |
| Select type | 11 | 9 | 0,6,11,13,18,23,26,28,31,35,40,45 | 45 |
| Geyser Converter | 13 | 11 | 0,8,14,19,24,30,34,37,45,51,57,62,68,72,75,81,85 | 85 |
| Geyser Converter | 11 | 9 | 0,7,12,16,20,25,28,30,36,41,46,50,55,58,61,66,69 | 69 |
| Geyser Converter | 10 | 8 | 0,6,10,14,18,22,25,27,33,37,41,45,49,52,54,58,61 | 61 |
| AVAWAYTaToLTPAFaVa | 20 (scale 1) | 20 | 0,13,26,39,58,71,85,98,107,120,129,140,153,166,179,191,202,215,226 ("Ta", "To": −2) | 226 |

#### Vertical metrics

From LegacyRuntime's serialized values scaled to p (k = p / 13), rounded to the nearest
integer:
- ascent a = round(11.7685546875 k) and descent d = round(2.7548828125 k);
- the block height of a line is B = a + d, and the preferred height of one line is B;
- the baseline is a below the line's top. A quad's y spans baseline + `minY` to
  baseline + `maxY`, so every vertex y is an integer;
- the line pitch is P = round(14.94873046875 k); the next line's top is P lower, and n lines
  are (n − 1) P + B high;
- `UILineInfo.height` is floor(14.94873046875 k), and `UILineInfo.leading` is P − B.

| p | a | d | B | P | UILineInfo.height | leading |
|---|---|---|---|---|---|---|
| 5 | 5 | 1 | 6 | 6 | 5 | 0 |
| 6 | 5 | 1 | 6 | 7 | 6 | 1 |
| 7 | 6 | 1 | 7 | 8 | 8 | 1 |
| 8 | 7 | 2 | 9 | 9 | 9 | 0 |
| 9 | 8 | 2 | 10 | 10 | 10 | 0 |
| 10 | 9 | 2 | 11 | 11 | 11 | 0 |
| 11 | 10 | 2 | 12 | 13 | 12 | 1 |
| 12 | 11 | 3 | 14 | 14 | 13 | 0 |
| 13 | 12 | 3 | 15 | 15 | 14 | 0 |
| 14 | 13 | 3 | 16 | 16 | 16 | 0 |
| 16 | 14 | 3 | 17 | 18 | 18 | 1 |
| 20 | 18 | 4 | 22 | 23 | 22 | 1 |
| 25 | 23 | 5 | 28 | 29 | 28 | 1 |

The heights, leadings and baselines are measured at every p from 5 to 25. The pitch is
measured at 8, 9 and 11 (wrapped lines), and the rest follow from the rule.

#### Alignment and the generation extents

In pixels, the extents are `generationExtents` × `scaleFactor`, plus 0.0001 on each axis
(`rectExtents` reads 100.0001 × 30.0001 for 100 × 30). With W and H these padded sizes, the rect
is x from xMin = −pivot.x × W, and y down from yMax = (1 − pivot.y) × H. Let L be the line's
width (its final pen) and B the block height (several lines: (n − 1) P + B). Two roundings
are used:
- Rx(v) = floor(v + 0.5);
- Ry(v) = ceil(v − 0.5), that is, ties toward −y.

| anchor | line start x |
|---|---|
| Left | Rx(xMin) |
| Center | Rx(xMin + W / 2) − ceil(L / 2) |
| Right | Rx(xMin + W) − L |

| anchor | block top y (the first line's top) |
|---|---|
| Upper | Ry(yMax) |
| Middle | Ry(yMax − (H − B) / 2) |
| Lower | Ry(yMax − H + B) |

- A centred line rounds the centre first. "Target type" (L 63) in W 100.0001, pivot 0: Rx(50.00005)
  − 32 = 18, where (W − L) / 2 = 18.50005 would give 19. At W 101.0001 it starts at 19.
- Without the 0.0001 the rule fails at exact halves. UpperLeft at pivot (1, 0) in w 100.5 starts
  at −101, where Rx(−100.5) would be −100. Upper at pivot (0.5, 0.5) in h 31 puts the top at
  16, where Ry(15.5) would be 15.
- **`updateBounds` true:** the extents become the text's own size: the block height (without
  the 0.0001) and, unless the text wraps, the widest line's L (a wrapping text keeps the padded
  W). The pivot places that box and the rules above then apply in it, so in a one-line text
  that does not wrap the anchor makes no difference at pivot (0, 1): every anchor starts at
  (0, 0). With pivot (0.5, 0.5) "Target type" at 13 px (L 63) starts at x −31 = Rx(−31.5) when
  left or right aligned and at −32 = Rx(0) − 32 when centred; at 9 px (L 45, wrapping in
  201.3 × 0.86) UpperLeft starts at −87 = Rx(−86.55905). All 288 of the probe's updateBounds
  generations fit this (re-read from the log by the implementation, 2026-10-24).
  `rectExtents` reads (0, 0, that width, block height). With `updateBounds` false it reads
  (0, 0, W, H). The x and y of `rectExtents` are 0 in every case.
- `generateOutOfBounds` made no difference in these one-line cases.
- `Text` lays out with the second behaviour (aligned): rect size × canvas scale as the extents,
  and the rect's pivot. On the 0.86 canvas a centred 232 × 17 rect gives:
  - MiddleLeft: start −100 (Rx(−99.76005));
  - MiddleCenter: −23 for "Target type" (L 45), −35 for "Geyser Converter" (69) and −83 for
    "Choose a target type and start conversion." (166).

  The mesh the Text hands its `CanvasRenderer` is the generator's vertices × (1 / scale), plus
  the pixel-perfect rounding offset on a pixel-perfect canvas (see "Text (legacy)" above). On
  the 0.86 canvas −101 becomes −117.44186 units.

#### Best fit (`resizeTextForBestFit`), as observed

`fontSizeUsedForBestFit` is a pixel size. The candidates are point sizes from
`resizeTextMinSize` to `resizeTextMaxSize`, each laid out at floor(points × scale). For
"Choose a target type and start conversion.", wrapping, in a box 20 units high:

| pt max | scale | width (units) | used (px) |
|---|---|---|---|
| 11 | 0.86 | 60 / 100 / 400 | 5 / 7 / 9 |
| 10 | 0.86 | 60 / 100 / 400 | 5 / 7 / 8 |
| 13 | 0.86 | 60 / 100 / 400 | 5 / 7 / 11 |
| 11 | 1 | 60 / 100 / 400 | 6 / 9 / 11 |
| 12 | 0.75 | 60 / 100 / 400 | 4 / 7 / 9 |

`GetPreferredWidth` and `GetPreferredHeight` with best fit on report the size without best fit
(166 and 40 for the first row). The fitting criterion itself was not pinned down. With
vertical overflow (the probe's `Text`s, 232 × 17 units) best fit took the largest size every
time, 2 wrapped lines and all: 11 px for 13 points at 0.86, 19 for 13 at 1.5.

#### Where ours deviated (2026-10-24, same probe)

The diff is `lx-check.py diff ORIG OURS`. Ours lacks `CharacterInfo` and `GetCharacterInfo`:
the `raster`, `ci` and `fonts` parts throw `TypeLoadException`.
- The face: ours always draws LegacyRuntime from Liberation Sans.
- Kerning: ours applies `m_KerningValues` only to non-dynamic fonts.
- Quads: ours uses the unpadded bitmap box.
- Baseline: ours places it at the unrounded scaled ascent (y −3.9729 where the original has
  integers).
- Line metrics: ours uses unrounded heights (preferred height 10.349 at 9 px where the original
  has 10).
- Alignment: ours does not round the line start. With `updateBounds` true, ours honours the
  anchor, and its `rectExtents` y is −height.
- `fontSizeUsedForBestFit`: ours reports the point size (11 where the original reports 9).

Of the 4246 generations both logged, none matched in its quads.

**Since then** (the implementation, 2026-10-24): `lx-check.py diff --no-uv` finds every
`ci`, `raster`, `osglyph`, `gen`, `align` and `text` line of the two players equal (28611
lines), and the `fonts` part's other lines (`osnames`, `ospaths`, `osfont`) are the same text.
Left out are the texture coordinates, which follow where each player's packer put a glyph in
the font texture (the original also stores some glyphs rotated, `flipped`). Our texture holds
glyphs with their top at the higher v; the original's at the lower. The probe also showed:
- **Pixel-perfect snapping rounds halves up:** a `Text`'s first vertex at screen x 670.5 and
  680.5 (the canvas at scale 1, the rect at x 0.5) shifts the mesh by +0.5 (to 671 and 681),
  where rounding to even would give −0.5 (657.5 goes to 658 either way).
- **The first allocation of a font texture is no rebuild:** the fonts made with
  `CreateDynamicFontFromOSFont` raise no `Font.textureRebuilt` on their first glyphs.
- **Installed fonts** (`GetPathsToOSFonts`, `GetOSInstalledFontNames`): the font in
  `~/.local/share/fonts` here (Latin Modern Math) is not listed, and neither is the colour
  bitmap face Noto Color Emoji (FreeType reports it not scalable), whose file is.

#### Open questions

- **Windows and macOS** were not measured. Ours takes the same mechanism there: the first of
  `fontNames` installed (Arial for LegacyRuntime), and, as the last link of the chain, the
  built-in Liberation Sans (the game's own copy) where Linux's default chain was measured.
  Our macOS build scans no macOS font folder, so it reaches that last link.
- **After an installed name**, whether the default chain still supplies the glyphs that face
  lacks (ours: it does).
- **A style the family has no face for** (DejaVu Sans here has no oblique): ours synthesizes it
  on the regular face.
- **`HasCharacter`** on a font without data returned true for every character tried; ours
  returns true for every character. Whether a character no installed font has is false is
  unknown.
- **Multi-line alignment** with `updateBounds` true and the alignment of lines of different
  widths in it, `lineSpacing` other than 1 (`UILineInfo.height` and the pitch), kerning across
  rich-text tags and `<quad>`, and the tab stop were not measured.
- **Best fit** with horizontal overflow (ours: width does not count) and with vertical
  truncation without best fit (whether `rectExtents` keeps the padded height then) were not
  measured apart.
- **The first font texture**: the original's LegacyRuntime texture was already 256 × 256 when
  the probe started; ours is allocated at the first glyph request.
- **~/.fonts** and the other folders the original scans are not known; ours scans ~/.fonts and
  `$XDG_DATA_DIRS/fonts` (default /usr/local/share and /usr/share).
- **The TextCore font engine's face** for a font without data (a TMP font asset made from
  LegacyRuntime): ours loads the built-in Liberation Sans; unmeasured.

#### Acceptance tests

Inputs are LegacyRuntime, with FreeSans as the face (skip when FreeSans 0412.4271 is not
installed), UpperLeft, Overflow, pivot (0, 1), rich text on and `updateBounds` true unless
given.
1. **Pixel size:** fontSize 11, scale 0.86 → `fontSizeUsedForBestFit` 9. 10 × 0.95 → 9. 9 × 0.99
   → 8. 16 × 0.86 → 13.
2. **Panel labels:** each row of the table "The panel's labels" gives those cursors x (the
   `UICharInfo` list, caret slot included) and that preferred width.
3. **Kerning:** "Target type" at 11 × 0.86 → charWidth 6,5,3,5,5,3,2,3,4,5,5,0 and cursors
   0,6,10,13,…. The quad of "a" starts at 6 − 1 + `minX`("a") = 4, and its cursorPos is 6.
   "AVAWAYTaToLTPAFaVa" at 20 × 1 → width 226, cursors as in the table. "Ta" at 5 and 6 px →
   no kerning; at 7 → −1.
4. **Spaces:** "Target type" has 12 character infos and 10 quads (40 vertices), and "Choose a
   target type and start conversion." 43 infos and 36 quads.
5. **CharacterInfo:** 'C' at 9 px → advance 6, minX −1, maxX 8, minY −2, maxY 8. ' ' at 9 px →
   advance 2, box −1..1 × −1..1. '!' at 20 px → advance 7, minX 1, maxX 6. Size 0 equals
   size 13 ('A': advance 9, −1..10 × −1..11). All 2090 Normal entries equal
   FreeType unhinted metrics as `lx-check.py face` computes them.
6. **Fallback glyphs:** U+2125 at 20 px → advance 12 (DejaVu Sans). U+4E2D → advance 0, box
   −1..1 × −1..1, `GetCharacterInfo` true.
7. **Vertical:** at 9 px one line → preferred height 10, baseline 8 below the top, every quad
   y an integer, `UILineInfo` height 10 and leading 0. At 11 px → 12, height 12, leading 1, and
   3 wrapped lines 38 high with tops 0, −13, −26.
8. **Alignment** (`updateBounds` false, fontSize 13, scale 1, extents w × h):
   - "Target type" (L 63), pivot (0, 1): UpperCenter w 100 → 18, w 100.875 → 18, w 101 → 19;
     UpperRight w 100.375 → 37, w 100.5 → 38; LowerLeft h 30.375 → top −15, h 30.5 → −16;
     MiddleLeft h 31.875 → −8, h 32 → −9.
   - Pivot (0.5, 0.5): UpperLeft w 100.875 → −50, w 101 → −51; UpperCenter → −32 for any w;
     MiddleLeft → top 7 for any h; UpperRight w 101 → −12.
   - Pivot (1, 0): UpperCenter w 101 → −83; UpperLeft w 100.5 → −101.
   - "Steam Vent" (L 67): UpperCenter pivot (0, 1), w 100 → 16; pivot (1, 0), w 101 → −85.
9. **updateBounds true:** any anchor in 201.3 × 30 at pivot (0, 1) → start (0, 0), and
   `rectExtents` (0, 0, 63, 15) for "Target type" at 13 px.
10. **Text:** a `Text` of "Target type", 11 pt, MiddleCenter, in a centred 232 × 17 rect on a
    canvas at scale 0.86 that is not pixel perfect → generator cursors start at −23,
    `preferredWidth` 52.32558 (45 / 0.86), and mesh x = generator x / 0.86.

### Measured on the shipped package

Found by the M9 differential runs; they refine the sections above.
- **Sliced images:** borders are scaled to the pixel-snapped rect first, then shrunk together
  where they do not fit; each line is offset from the rect's start. Quads are emitted column
  by column, and empty quads are left out. Trimmed sprite padding is left out of the quad.
- **Radial fills** collapse the corners past the cut.
- **Rebuild queues:** outside a rebuild pass, an element leaving a queue is replaced by the
  queue's last element (this decides the order a dynamic font packs glyphs in).
- **Dropdown:** the blocker carries a `CanvasGroup`.
- **Raycast padding:** positive padding shrinks the hit area (left, bottom, right, top).

## Verification

1. **Build:** `scripts/build-packages.sh` builds the package and runs ApiCheck: nothing missing.
2. **Unit tests**, in `tests/OniMoono.UnityEngine.Tests`, with a new reference to the package
   project:
   - the canvas update order;
   - each Image type's vertices;
   - layout groups for alignment, control, force-expand and padding;
   - the fitters;
   - `ExecuteEvents`;
   - the pointer pipeline, driven through `BaseInput` with a test input;
   - the selectable state;
   - the toggle groups;
   - the slider and scrollbar values;
   - ScrollRect dragging and clamping;
   - the raycast order.

   Where the documentation leaves a number open, measure it against the shipped package
   through a scenario run, not the shipped code.
3. **Differential runs** against the original Unity player (`scripts/e2e.py --player both`):
   - `menu` and `colony` with no differences beyond the tolerance;
   - new scenarios as you need them, for example a new game, the research screen, the
     management screens, a dropdown and the scroll views. Add them as specs in `tests/e2e/`.

   Explain every remaining difference.
4. **Performance:** DeCrew's frame rate is not worse than in the original player.
