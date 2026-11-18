# U60 review: the save tweaks

Static review of the save game tweaks against U60 (761015, public test) from U59 (744825). No player
ran; the tweaks were built against the U60 assemblies (`win761015-game`). Line numbers are in the
U60 decomp (oni-archives tag U60-761015, `raw/Assembly-CSharp/`). KSerialization lives in
Assembly-CSharp-firstpass, which the decomp read then did not cover: it was decompiled from both
builds with ilspycmd and compared (only `Helper`, `SerializationTemplate`, `Manager` and
`Serializer` differ, and only in compiler shape: extra locals, renamed temporaries, inverted early
returns; `IOHelper`, `TypeInfo` and `SerializationTypeInfo` are identical).

General U60 finding: Assembly-CSharp is now compiled without optimization. `SaveLoader.Save`
has 75 `nop`s in U60 and none in U59, and most of the decomp's U59-to-U60 diff in these files is
that (locals for every intermediate value, `foreach` over a copied array). Transpilers that match
adjacent instructions need their pattern rechecked in the IL, not the C#; the two here still
match.

| Tweak | Game members touched | U60 change | Verdict | Runtime check on a U60 player |
|---|---|---|---|---|
| fast-save-type-checks | Transpiles `SaveLoadRoot.SaveWithoutTransform`: replaces its `MemberInfo.IsDefined(Type, bool)` calls (expects 2) and the `Object.ToString()` right after `Object.GetType()` (expects 1); assumes both answers depend only on the component's type | Only compiler shape (SaveLoadRoot.cs:108-168: copied arrays, extra locals). IL still has 2 `callvirt MemberInfo::IsDefined` and `callvirt Object::GetType()` immediately followed by `callvirt Object::ToString()` (IL_0122/IL_0127) | unchanged | No `fast-save-type-checks: SaveLoadRoot.SaveWithoutTransform has …` line in the log after a save; save bytes equal with the tweak on and off |
| background-save | Prefix on `SaveLoader.CompressContents(BinaryWriter, byte[], int)` (skips it for a `FileStream` writer); wait prefixes on `SaveLoader.Save(string, bool, bool)`, `SaveLoader.Load(string)`, `LoadScreen.DeleteFileAndEmptyFolder`, and now `LoadScreen.FileMatch`, `CheckCloudLocalOverlap` and `MigrateSave` (the callers of `MigrateFile`, whose `catch … when` clauses Harmony cannot patch, ARCHITECTURE D53), `ConvertToLocalOrCloud`; assumes `Save` writes the header, then `Manager.SerializeDirectory`, then the body through `CompressContents` from `memoryStream.GetBuffer()`, and nothing reads the body before `Save` returns | `CompressContents` (SaveLoader.cs:294) and the write sequence in `Save` (SaveLoader.cs:880-905) unchanged. New: `isInitialSave` (166, 798-806) only passed to `Timelapser.SaveColonyPreview` (930), which writes the PNG on its own tasks; `SaveAndQuitRoutine` (809) waits for `Game.CanSave`, calls `Save` and `App.Quit` (Global.cs:426), and the host's end of run waits for the write. Pre-existing gap, same in U59: the wait on LoadScreen methods named Move/Copy matched no method; the migration's copies and moves (LoadScreen.cs:427, 460, 750) now wait too | updated (LoadScreen waits; not a U60 change) | Save, then load at once: log shows `waiting for a save being written` and the load succeeds; Save and Quit from the rail dialog leaves a whole file (`background save written:`); loading the saved file in vanilla U60 |
| fast-save-compression | Prefixes on `SaveLoader.CompressContents` (when background-save is off) and `SaveLoader.DecompressContents(byte[])`; assumes the zlib format (Ionic `ZlibStream`, `BestSpeed`) | Neither method changed (SaveLoader.cs:294-299, 308-311); Ionic.Zip.dll identical (same MD5) | unchanged | A save written with the tweak loads in vanilla U60, and a vanilla U60 save loads with it |
| fast-serialization | Prefix replacing `KSerialization.SerializationTemplate.SerializeData(object, BinaryWriter)`; reads by name `serializableFields`, `serializableProperties`, `onSerializing`, `onSerialized`, `customSerialize`, `serializableType`, `SerializationField.field`/`typeInfo`, `SerializationProperty.property`/`typeInfo`, `TypeInfo.info`; calls `Helper.WriteValue`, `IOHelper.WriteSingleFast`, the game's `Debug.LogError(object)`; reimplements `WriteValue` for kinds 1-11, 13 (int enum), 14-16, 23 | `SerializeData` and `WriteValue` unchanged apart from compiler shape; all members present. Klei's own change: `BubbleManager.Disease` gains `int hashForSerialization` before `Count` and `byte Idx` moves last, filled in a new `[OnSerializing]` (BubbleManager.cs:22, 378); `EntombedItemManager` gains `[Serialize] List<int> diseaseHash` with `[OnSerializing]`/`[OnSerialized]` (EntombedItemManager.cs:44, 48, 67). The tweak takes field order and callbacks from the template and runs `OnSerializing` before reading fields, so the new layout is written as the game writes it (int, int, byte all on the direct path). Install now fails at start-up, naming the member, if any is missing (was a NullReferenceException at the first save) | unchanged (install check added) | SaveTest-style comparison: a U60 save with `FastSerialization.Enabled` on and off is byte-identical, including a colony with bubbles and entombed items carrying germs |
| no-save-gc | Transpiles `SaveLoader.Save(string, bool, bool)`: replaces its one `GC.Collect()` with `nop`, throws at install if it finds a number other than one | `GC.Collect()` still the last call before `return` (SaveLoader.cs:932; IL_0480, the only one in the method). Klei's "incremental GC" and "stutter after the first save" items do not remove it: the first is a Unity player setting, the second is the timelapse preview (`isInitialSave`, Timelapser) | unchanged | No `no-save-gc: expected one GC.Collect()` exception at start-up; no full collection in a save's trace |

Not a target of these tweaks but checked: `StateMachineSerializer` (only
`DoesVersionHaveTypeSuffix` rewritten to an equivalent expression) and the BubbleManager and
EntombedItemManager disease-hash change above, which goes through the template like any other
type.

No tweak here is a retire candidate: Klei's U60 save changes (disease ids by hash, the initial
save's timelapse) do not touch template serialization, compression, the forced collection or
the component type checks.

Commits: 7060372f (background-save waits before the load screen's migrations), 9a77d0bb
(fast-serialization checks KSerialization members at install).
