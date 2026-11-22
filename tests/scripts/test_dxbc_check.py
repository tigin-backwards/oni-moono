"""The DXBC token-format check (src/OniMoono.NativeHost/dxbc_check.cpp): it is what
scripts/gen-dxbc-check.py makes of the asset reader's constants (DxbcTokenFormat.cs), and it
compiles against DirectX-Headers' D3D12TokenizedProgramFormat.hpp with this machine's C++ compiler
(a stub winapifamily.h stands in for the Windows SDK's; the d3dcommon.h values are checked by the
Windows build only). Skipped without a C++ compiler (c++, g++, clang++)."""

import os, shutil, subprocess, tempfile, unittest

import helpers

gen = helpers.script('gen-dxbc-check.py')


def compiler():
    for name in ('c++', 'g++', 'clang++'):
        if shutil.which(name):
            return name
    return None


class DxbcCheckTest(unittest.TestCase):
    def test_committed_file_is_the_generators_output(self):
        with open(gen.SOURCE, encoding='utf-8') as f:
            expected = gen.generate(f.read())
        with open(gen.OUTPUT, encoding='utf-8') as f:
            self.assertEqual(f.read(), expected, 'run scripts/gen-dxbc-check.py')

    def test_every_constant_is_checked(self):
        found = gen.constants('public const int D3D10_SB_A = 1, D3D10_SB_B = 0x2;\npublic const uint X = 3; // = D3D10_SB_C + 1\n')
        self.assertEqual(found, [('D3D10_SB_A', '1', 'D3D10_SB_A'), ('D3D10_SB_B', '0x2', 'D3D10_SB_B'), ('X', '3', 'D3D10_SB_C + 1')])
        with self.assertRaises(ValueError):
            gen.constants('public const int Unnamed = 1;\n')

    def test_it_compiles_and_a_wrong_value_does_not(self):
        cxx = compiler()
        if not cxx:
            self.skipTest('no C++ compiler')
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, 'winapifamily.h'), 'w') as f:
                f.write('#define WINAPI_FAMILY_PARTITION(x) 1\n')
            headers = os.path.join(helpers.ROOT, 'src', 'ThirdParty', 'DirectX-Headers')

            def build(source):
                path = os.path.join(d, 'check.cpp')
                with open(path, 'w') as f:
                    f.write(source)
                return subprocess.run([cxx, '-std=c++17', '-fsyntax-only', '-I', d, '-I', headers, path], capture_output=True, text=True)

            with open(gen.OUTPUT, encoding='utf-8') as f:
                source = f.read()
            result = build(source)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = build(source.replace('SAME(89, D3D10_SB_OPCODE_DCL_CONSTANT_BUFFER)', 'SAME(90, D3D10_SB_OPCODE_DCL_CONSTANT_BUFFER)'))
            self.assertNotEqual(result.returncode, 0)


if __name__ == '__main__':
    unittest.main()
