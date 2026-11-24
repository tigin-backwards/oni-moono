"""The Direct3D 11 presenter's program (D3d11Present.cs): its committed DXBC blobs are what fxc
makes of the HLSL in the comment above them (scripts/gen-d3d11-present.py). The comparison needs
the Windows SDK's fxc from WSL and is skipped without it; the blobs' own shape is checked
everywhere."""

import struct, unittest

import helpers

gen = helpers.script('gen-d3d11-present.py')


def source():
    with open(gen.SOURCE, encoding='utf-8') as f:
        return f.read()


class D3d11PresentTest(unittest.TestCase):
    def test_the_comment_holds_both_entry_points(self):
        text = gen.hlsl(source())
        self.assertIn('PSIn vs(VSIn i)', text)
        self.assertIn('float4 ps(PSIn i) : SV_Target', text)

    def test_the_blobs_are_whole_dxbc_containers_of_the_comments_signatures(self):
        for name, data in gen.blobs(source()).items():
            self.assertEqual(data[:4], b'DXBC', name)
            # The container's size field (bytes 24-27) is its whole length.
            self.assertEqual(struct.unpack_from('<I', data, 24)[0], len(data), name)
            for semantic in (b'TEXCOORD', b'COLOR'):
                self.assertIn(semantic, data, name)
        blobs = gen.blobs(source())
        self.assertIn(b'POSITION', blobs['PresentVertexDxbc'])
        self.assertIn(b'SV_Target', blobs['PresentPixelDxbc'])

    def test_the_blobs_are_fxcs_output_of_the_comment(self):
        if not gen.available():
            self.skipTest(f'no fxc at {gen.FXC}')
        text = source()
        made = gen.compile_all(gen.hlsl(text))
        held = gen.blobs(text)
        for name in made:
            self.assertEqual(made[name], held[name], f'{name}: run scripts/gen-d3d11-present.py --write')


if __name__ == '__main__':
    unittest.main()
