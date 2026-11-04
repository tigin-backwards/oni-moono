# UnityEvents: functional specification

Namespace `UnityEngine.Events`, in the engine assembly (`src/OniMoono.UnityEngine`). A
UnityEvent is a list of callbacks that a component invokes. uGUI uses one for every interaction
(a button's `onClick`, a slider's `onValueChanged`); the game's code adds listeners at run time and
invokes events of its own.

## Interface

- **Public and protected API:** as Unity's Scripting API documents it for `UnityEventBase`,
  `UnityEvent`, `UnityEvent<T0>`, `UnityEvent<T0,T1>`, `UnityEvent<T0,T1,T2>`. The delegates
  `UnityAction…` and the enums `PersistentListenerMode` and `UnityEventCallState` are already
  declared in `Generated/UnityEngine.CoreModule/UnityEngine.Events.cs`, together with:
  - empty `[Serializable]` shells of `UnityEventBase`, the generic events, and three internal
    data classes `PersistentCall`, `PersistentCallGroup`, `ArgumentCache`;
  - `UnityEventBase`'s `protected abstract MethodInfo FindMethod_Impl(string name, Type targetObjType)`.
- **Members required:** what `tools/ApiCheck` reports as missing (`scripts/check.sh` runs it).
  It covers the game's assemblies and uGUI, which may also use the engine's internal members.
- **Serialized data:** these field names and types are fixed by the game's data:
  - `UnityEventBase.m_PersistentCalls`: a `PersistentCallGroup`.
  - `PersistentCallGroup.m_Calls`: `List<PersistentCall>`.
  - `PersistentCall`:
    - `m_Target`: `UnityEngine.Object`;
    - `m_TargetAssemblyTypeName`: string, the assembly-qualified name of the target's type;
    - `m_MethodName`: string;
    - `m_Mode`: `PersistentListenerMode`;
    - `m_Arguments`: `ArgumentCache`;
    - `m_CallState`: `UnityEventCallState`.
  - `ArgumentCache`:
    - `m_ObjectArgument`: `UnityEngine.Object`;
    - `m_ObjectArgumentAssemblyTypeName`: string;
    - `m_IntArgument`: int;
    - `m_FloatArgument`: float;
    - `m_StringArgument`: string;
    - `m_BoolArgument`: bool.

  Defaults for a call created in code: mode `EventDefined`, call state `RuntimeOnly`.

  The engine's serializer fills these fields by name (`Engine/Serialization`). It then calls
  `ISerializationCallbackReceiver.OnAfterDeserialize` on objects that implement it, and the
  cloner (`Instantiate`) does the same.

## Behaviour

### Listeners

- Two kinds of listener:
  - **Persistent:** the serialized calls.
  - **Runtime:** added with `AddListener`.
- **Order:** `Invoke` calls the persistent listeners in their serialized order, then the runtime
  listeners in the order they were added.
- **Removing:**
  - `RemoveListener(action)` removes every runtime listener whose delegate has the same target
    and method as `action`.
  - `RemoveAllListeners` removes all runtime listeners. Persistent listeners stay.
- **Changes during an invocation:** a listener added or removed while an `Invoke` is running
  does not change that invocation. It applies from the next one.
- **Exceptions:** an exception thrown by a listener propagates out of `Invoke`, and the
  remaining listeners of that invocation are not called.
- **Destroyed targets:** a listener whose delegate target is a `UnityEngine.Object` that has
  been destroyed (`== null` by Unity's rules) is skipped. This applies to runtime and persistent
  listeners alike.
- **Static methods:** a listener whose method is static has no target and is always called.
- **Allocation:** invoking an event whose listeners have not changed allocates nothing.

### Persistent calls

**Resolution** happens lazily: on the first `Invoke` after deserialization or cloning, and
after `SetPersistentListenerState`. Earlier resolved persistent listeners are discarded at
those points.

**Which calls take part:**
- A call is skipped when:
  - its method name is empty;
  - it has no target and its target type name is empty. A call with a target runs with an
    empty type name (measured: the debug element picker's filter field, whose calls were saved
    without one, filters the list in the original player);
  - its call state is `Off`;
  - its method cannot be found.
- `EditorAndRuntime` and `RuntimeOnly` calls run.
- An instance method needs a live target (not null, not destroyed); otherwise the call is
  skipped.

**Finding the method:**
- The type searched is the target's runtime type. If the target is null, it is the type named
  by `m_TargetAssemblyTypeName`, which allows static methods.
- The search starts at that type and goes up its base types, stopping before
  `System.Object`. It considers public and non-public, instance and static methods.
- The first type that declares a matching method wins. A method matches when it has that name
  and .NET's default binder would accept the required types for its parameters
  (`Type.GetMethod(name, flags, binder: null, types, modifiers: null)` on that type alone).
  That allows widening primitive conversions (an `int` for a `float` parameter) and reference
  conversions (a derived type for a base-type parameter).
- A candidate is then rejected when any parameter's "is primitive" differs from the required
  type's: an `int` must not match an `object` parameter. The search then continues with the
  base type.
- This search is also the public static `UnityEventBase.GetValidMethodInfo(Type, string, Type[])`.
  The overload taking an object uses the object's type.

**Required parameters and arguments, by mode:**

| Mode | Method's parameters | Called with |
|---|---|---|
| `EventDefined` | the event's own parameter types (none for `UnityEvent`, `T0` for `UnityEvent<T0>`, …) | the arguments `Invoke` was given |
| `Void` | none | nothing, whatever the event's own arguments |
| `Int`, `Float`, `String`, `Bool` | one `int`, `float`, `string` or `bool` | the stored constant (`m_IntArgument`, …) |
| `Object` | one parameter of the type named by `m_ObjectArgumentAssemblyTypeName` (`UnityEngine.Object` when that name is empty or does not resolve) | `m_ObjectArgument`, or null when it is not an instance of that type |

**Persistent-call accessors:**
- `GetPersistentEventCount()` counts every serialized call, valid or not.
- `GetPersistentTarget(i)` and `GetPersistentMethodName(i)` read the serialized values. An
  index outside the serialized calls throws `ArgumentOutOfRangeException`, as for the other
  persistent-call accessors. A call whose method name is null reads as the empty string.
- `GetPersistentListenerState(i)` returns the call state.
- `SetPersistentListenerState(i, state)` sets it, then re-resolves the persistent calls before
  the next `Invoke`.

### Cloning

- `Instantiate` copies serialized data only, so a clone's events have the original's persistent
  calls and no runtime listeners. A persistent target inside the cloned hierarchy refers to the
  clone's object: the cloner remaps object references.
- Each clone resolves its own persistent calls. Invoking the original never runs the clone's
  calls, and the reverse.

### Other

`ToString()` returns the default object string, a space, and the event's full type name.

## Tests to write

These go in `tests/OniMoono.UnityEngine.Tests/UnityEventTests.cs`, and they come before the
implementation:
- The listener order.
- Every mode, with resolution through base types and non-public methods.
- The primitive rule.
- Off calls.
- Destroyed targets.
- Changes during an invocation.
- Exceptions.
- `RemoveListener` and `RemoveAllListeners`.
- Re-resolution after `SetPersistentListenerState`.
- Cloning: persistent targets remapped, runtime listeners not copied.
- No allocation on a repeated `Invoke`, checked with `GC.GetAllocatedBytesForCurrentThread`.

For the end-to-end check, use the game itself: uGUI buttons are wired with persistent calls, so
the scripted menu and new-game runs exercise them.
