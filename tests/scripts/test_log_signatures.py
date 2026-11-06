"""qa-tools.py's log signatures: the same error reads the same from Mono's log and from ours."""
import contextlib, io, os, tempfile, unittest

import helpers

qa_tools = helpers.script('qa-tools.py')


def signatures(text, errors=None):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, 'log')
        with open(path, 'w') as f:
            f.write(text)
        errors_path = None
        if errors is not None:
            errors_path = os.path.join(d, 'errors')
            with open(errors_path, 'w') as f:
                f.write(errors)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            qa_tools.signatures(path, errors_path)
        return out.getvalue().splitlines()


class LogSignatures(unittest.TestCase):
    def test_io_message_and_runtime_frames(self):
        ours = signatures("FileNotFoundException: Could not find file '/x/State.json'.\n"
                          "  at Interop.ThrowExceptionForIoErrno (Interop+ErrorInfo errorInfo)\n"
                          "  at Microsoft.Win32.SafeHandles.SafeFileHandle.Open (System.String fullPath)\n"
                          "  at DraggableUI.State.Load () [0x00000] in <0>:0\n")
        mono = signatures('FileNotFoundException: Could not find file "/x/State.json"\n'
                          '  at System.IO.FileStream..ctor (System.String path) [0x00001] in <aa>:0\n'
                          '  at DraggableUI.State.Load () [0x00000] in <bb>:0\n')
        self.assertEqual(ours, mono)

    def test_logged_error_then_exception(self):
        # Neither player writes a stack trace for an error (UnityProbe's logging scenario); an
        # exception's own frames are in Mono's format in both, then the logging call's frames.
        ours = signatures('[08:02:46.562] [1] [ERROR] Could not find ChoreGroup: \n'
                          'Could not find ChoreGroup: \n'
                          'NullReferenceException: Object reference not set to an instance of an object.\n'
                          '  at CharacterContainer.SetInfoText () [0x007d7] in <b4ac>:0 \n'
                          '  at CharacterContainer+<DelayedGeneration>d__67.MoveNext () [0x00045] in <b4ac>:0 \n'
                          'LogCatcher:UnityEngine.ILogHandler.LogException(Exception, Object)\n'
                          '\n')
        mono = signatures('[07:57:30.972] [1] [ERROR] Could not find ChoreGroup: \n'
                          'Could not find ChoreGroup: \n'
                          'NullReferenceException: Object reference not set to an instance of an object\n'
                          '  at CharacterContainer.SetInfoText () [0x007d7] in <b4ac>:0 \n'
                          '  at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, '
                          'System.IntPtr returnValueAddress) [0x00026] in <49b3>:0 \n'
                          'UnityEngine.DebugLogHandler:Internal_LogException_Injected(Exception, IntPtr)\n'
                          'LogCatcher:UnityEngine.ILogHandler.LogException(Exception, Object)\n'
                          'UnityEngine.Logger:LogException(Exception, Object)\n'
                          'UnityEngine.Debug:CallOverridenDebugHandler(Exception, Object)\n'
                          '\n')
        self.assertEqual(ours, mono)
        self.assertIn('Could not find ChoreGroup: @ CharacterContainer.SetInfoText', mono)

    def test_unity_frames_with_and_without_the_space(self):
        # Unity writes "A:B(x)"; ours wrote "A:B (x) (at file)" until 2026-10-24, which signatures
        # stored then (references) still hold.
        old = signatures('[12:00:01.000] [1] [ERROR] Could not find ChoreGroup:\n'
                         'UnityEngine.Debug:LogError (Object) (at /src/Core/Debug.cs:28)\n'
                         'CharacterContainer:SetInfoText ()\n')
        new = signatures('[12:00:01.000] [1] [ERROR] Could not find ChoreGroup:\n'
                         'UnityEngine.Debug:LogError(Object)\n'
                         'CharacterContainer:SetInfoText()\n')
        self.assertEqual(old, new)

    def test_a_frame_with_its_source_file(self):
        # The engine has symbols: Mono's format then names the file where the original names <mvid>.
        ours = signatures('NullReferenceException: x\n'
                          '  at UnityEngine.Transform.set_parent (UnityEngine.Transform value) [0x00012] in '
                          '/w/src/OniMoono.UnityEngine/Core/Transform.cs:40 \n')
        mono = signatures('NullReferenceException: x\n'
                          '  at UnityEngine.Transform.set_parent (UnityEngine.Transform value) [0x00012] in <abc>:0 \n')
        self.assertEqual(ours, mono)
        self.assertEqual(mono, ['NullReferenceException: x @ UnityEngine.Transform.set_parent'])

    def test_references_from_before_the_rename(self):
        # A reference stored when the engine's namespaces were OniBrutal.* still matches.
        self.assertEqual(qa_tools.unknown(['InvalidCastException: to OniMoono.Engine.X'],
                                          ['InvalidCastException: to OniBrutal.Engine.X']), [])
        self.assertEqual(signatures('NullReferenceException: x\n  at OniBrutal.Engine.EngineLog.Log ()\n'),
                         signatures('NullReferenceException: x\n  at OniMoono.Engine.EngineLog.Log ()\n'))

    def test_run_folder_is_masked(self):
        # The original and ours run in separate invocations, each in a folder of its run id.
        ours = signatures("FileNotFoundException: Could not find file '/w/out/e2e/s/20261024-040121-cfd644/"
                          "ours-1/home/config/mods/Local/1/State.json'.\n")
        mono = signatures('FileNotFoundException: Could not find file "/w/out/e2e/s/20261024-035631-81d98e/'
                          'original-1/home/config/mods/Local/1/State.json"\n')
        self.assertEqual(ours, mono)
        self.assertEqual(qa_tools.unknown(ours, mono), [])

    def test_trace_after_the_message_on_its_line(self):
        # WorldGen logs "Error:" + message + " " + StackTrace (More Vents Rime World's missing
        # element). Mono's trace starts in Dictionary's indexer; .NET inlined it and starts in the
        # game's ComposableDictionary, the first frame past the runtime's in both.
        mono = signatures("[02:19:28.045] [42] [ERROR] Error:The given key 'Katairite' was not present in the "
                          "dictionary.   at System.Collections.Generic.Dictionary`2[TKey,TValue].get_Item (TKey key) "
                          "[0x0001e] in <1a79>:0 \n"
                          "  at ProcGen.ComposableDictionary`2[Key,Value].get_Item (Key key) [0x0000c] in <faab>:0 \n"
                          "  at ProcGenGame.WorldGen.ProcessByTerrainCell (ProcGenGame.WorldgenSimData& simData) "
                          "[0x00619] in <b4ac>:0 \n")
        ours = signatures("[02:25:35.027] [78] [ERROR] Error:The given key 'Katairite' was not present in the "
                          "dictionary.    at ProcGen.ComposableDictionary`2.get_Item(Key key)\n"
                          "   at ProcGenGame.WorldGen.ProcessByTerrainCell(WorldgenSimData& simData)\n")
        self.assertEqual(qa_tools.unknown(ours, mono), [])
        self.assertEqual([qa_tools.canonical(s) for s in mono],
                         ['Error:The given key "Katairite" was not present in the dictionary @ '
                          'ProcGen.ComposableDictionary`N.get_Item'])
        # A different first frame still tells them apart.
        other = signatures("[02:25:35.027] [78] [ERROR] Error:The given key 'Katairite' was not present in the dictionary.    at "
                           "ProcGen.Other.get_Item(Key key)\n")
        self.assertEqual(len(qa_tools.unknown(other, mono)), 1)

    def test_inner_exception_on_its_own_line(self):
        # A mod logs "Failed patch X: " + exception (Reversible Heater Clones): Mono's
        # Exception.ToString continues the outer message with " ---> Inner: message", .NET's
        # starts that on a new line. One signature in both, as Mono writes it.
        mono = signatures('[23:58:02.227] [1] [WARNING] [RHC] Failed patch P: HarmonyLib.HarmonyException: Patching '
                          'exception in method null ---> System.ArgumentException: Undefined target method for '
                          'patch method static System.Void P::Prefix(System.Object[] __args)\n'
                          '  at HarmonyLib.PatchClassProcessor.PatchWithAttributes (System.Reflection.MethodBase& '
                          'lastOriginal) [0x00045] in <a1b2>:0 \n'
                          '   --- End of inner exception stack trace ---\n'
                          '  at HarmonyLib.PatchClassProcessor.ReportException (System.Exception exception, '
                          'System.Reflection.MethodBase original) [0x00045] in <a1b2>:0 \n')
        ours = signatures('[00:33:03.277] [1] [WARNING] [RHC] Failed patch P: HarmonyLib.HarmonyException: Patching '
                          'exception in method null\n'
                          ' ---> System.ArgumentException: Undefined target method for patch method static '
                          'System.Void P::Prefix(System.Object[] __args)\n'
                          '   at HarmonyLib.PatchClassProcessor.PatchWithAttributes(MethodBase& lastOriginal)\n'
                          '   --- End of inner exception stack trace ---\n'
                          '   at HarmonyLib.PatchClassProcessor.ReportException(Exception exception, MethodBase '
                          'original)\n')
        self.assertEqual(ours, mono)
        self.assertEqual(len(ours), 1)
        # Inner exceptions of inner exceptions join the same line.
        self.assertEqual(signatures('A: a\n ---> B: b\n ---> C: c\n   at M.N()\n'),
                         signatures('A: a ---> B: b ---> C: c\n  at M.N () [0x00000] in <x>:0\n'))

    def test_harmony_replacement_frames(self):
        # Printerceptor Reimagined logs Harmony's refusal of an inherited method: the original
        # runs PatchProcessor.Patch unpatched, ours a replacement of it (D14).
        mono = signatures('[03:46:57.029] [1] [WARNING] [GravitasMod] Patch failed for KScreen.OnCleanUp: '
                          'System.ArgumentException: You can only patch implemented methods/constructors.\n'
                          '  at HarmonyLib.PatchProcessor.Patch () [0x00051] in <f14b>:0 \n'
                          '  at HarmonyLib.Harmony.Patch (System.Reflection.MethodBase original) [0x0002a] in <f14b>:0 \n')
        ours = signatures('[03:46:47.742] [1] [WARNING] [GravitasMod] Patch failed for KScreen.OnCleanUp: '
                          'System.ArgumentException: You can only patch implemented methods/constructors.\n'
                          '   at HarmonyLib.PatchProcessor.Patch_Patch2(PatchProcessor this)\n'
                          '   at HarmonyLib.Harmony.Patch(MethodBase original)\n')
        self.assertEqual(ours, mono)
        self.assertTrue(mono[0].endswith(' @ HarmonyLib.PatchProcessor.Patch'))
        # Mono's frame of a replacement names MonoMod's owner type first.
        wrapped = signatures('NullReferenceException: x\n'
                             '  at (wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition.'
                             'OverlayScreen.RegisterModes_Patch1(OverlayScreen)\n')
        self.assertEqual(wrapped, signatures('NullReferenceException: x\n   at OverlayScreen.RegisterModes_Patch3(OverlayScreen this)\n'))
        self.assertEqual(wrapped, ['NullReferenceException: x @ OverlayScreen.RegisterModes'])

    def test_trailing_period(self):
        self.assertEqual(qa_tools.canonical('NullReferenceException: Object reference not set to an instance of an object.'),
                         qa_tools.canonical('NullReferenceException: Object reference not set to an instance of an object'))

    def test_canonical_is_idempotent(self):
        for s in ["X: it's 'here'. @ at A.B (int) [N] in <abc>:N", 'Y: plain', 'Z: m @ UnityEngine.Debug:LogError (object)']:
            once = qa_tools.canonical(s)
            self.assertEqual(once, qa_tools.canonical(once))

    def test_reference_without_frame_covers_any_frame(self):
        # An older reference kept the message alone when its first frame was runtime internals.
        self.assertEqual(qa_tools.unknown(['X: msg. @ Mod.F'], ['X: msg']), [])
        self.assertEqual(qa_tools.unknown(['X: other @ Mod.F'], ['X: msg']), ['X: other @ Mod.F'])

    def test_signature_without_frame_matches_any_frame(self):
        # Ours logs an error without the trace Unity prints after it.
        self.assertEqual(qa_tools.unknown(['X: msg'], ['X: msg @ Debug.LogError']), [])
        self.assertEqual(qa_tools.unknown(['X: other'], ['X: msg @ Debug.LogError']), ['X: other'])

    def test_invalid_cast_messages(self):
        ours = 'InvalidCastException: Unable to cast object of type "Boxed`N[A+B]" to type "B". @ Mod.Prefix'
        self.assertEqual(qa_tools.unknown([ours], ['InvalidCastException: Specified cast is not valid. @ Mod.Prefix']), [])
        self.assertEqual(qa_tools.unknown([ours], ['InvalidCastException: Specified cast is not valid. @ Mod.Other']), [ours])

    def test_plain_logged_errors(self):
        # UnityEngine.Debug.LogError writes a plain line, which the player's ONI_LOG_ERRORS file
        # names as an error; the game's Debug.LogError's [ERROR] line already gives its message.
        log = ("Material 'Material:0x6BB96198' with Shader 'Klei/X' doesn't have a texture property 'instanceTex'\n"
               'A plain Debug.Log line\n'
               '[08:02:46.562] [1] [ERROR] Could not find ChoreGroup: \n'
               'Could not find ChoreGroup: \n')
        errors = ("Material 'Material:0x6BB96198' with Shader 'Klei/X' doesn't have a texture property 'instanceTex'\n"
                  'Could not find ChoreGroup: \n')
        self.assertEqual(signatures(log), ['Could not find ChoreGroup:'])
        self.assertEqual(signatures(log, errors),
                         ['Could not find ChoreGroup:',
                          'Material "Material:N" with Shader "Klei/X" doesn"t have a texture property "instanceTex"'])
        self.assertEqual(signatures(log, None), signatures(log, ''))

    def test_a_different_frame_is_new(self):
        self.assertEqual(qa_tools.unknown(['X: msg @ Mod.G'], ['X: msg @ Mod.F']), ['X: msg @ Mod.G'])


if __name__ == '__main__':
    unittest.main()
