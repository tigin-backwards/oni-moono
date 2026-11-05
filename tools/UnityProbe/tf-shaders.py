#!/usr/bin/env python3
"""Writes the shaders TextureFallback.cs needs: a copy of a game's Resources/unity_builtin_extra
whose unused shaders are Hidden/BlitCopy (texture times _Color), each keeping its own name, with
_MainTex declared with another default or not at all.

  tf-shaders.py TYPETREES.json.gz SOURCE/unity_builtin_extra DEST/unity_builtin_extra

TYPETREES is the engine's type trees for the game's Unity version
(src/OniMoono.Assets/Data/typetrees-*.json.gz); the player's files carry none. Needs UnityPy.
Put the copy in a scratch install (links to the game's files but this one), never the game's own.
"""
import copy
import gzip
import json
import sys

import UnityPy
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

# Shader name -> _MainTex's declared default (None: not declared).
PLAN = {
    'Hidden/VideoDecode': 'white', 'Hidden/VideoComposite': 'black', 'Hidden/Compositing': 'grey',
    'Hidden/BlitToDepth': 'gray', 'Hidden/BlitToDepth_MSAA': 'bump', 'Hidden/BlitCopyHDRTonemap': 'red',
    'Hidden/VR/BlitTexArraySlice': 'nonsense', 'Hidden/VR/BlitFromTex2DToTexArraySlice': None,
}


def shader_tree(path):
    db = json.load(gzip.open(path))
    tree = next(c for c in db['Classes'] if c['Name'] == 'Shader')['Tree']

    def build(e, level):
        return TypeTreeNode(m_Level=level, m_Type=e['T'], m_Name=e['N'], m_ByteSize=e['S'], m_Version=e['V'],
                            m_Children=[build(c, level + 1) for c in e['C']], m_TypeFlags=e['F'], m_MetaFlag=e['M'])
    return build(tree, 0)


def main(typetrees, source, dest):
    node = shader_tree(typetrees)
    env = UnityPy.load(source)
    shaders = {}
    for o in env.objects:
        if o.type.name == 'Shader':
            shaders[o.read_typetree(node)['m_ParsedForm']['m_Name']] = o
    blit = shaders['Hidden/BlitCopy'].read_typetree(node)
    for name, default in PLAN.items():
        o = shaders[name]
        own = o.read_typetree(node)
        t = copy.deepcopy(blit)
        t['m_Name'] = own['m_Name']
        t['m_ParsedForm']['m_Name'] = own['m_ParsedForm']['m_Name']
        props = t['m_ParsedForm']['m_PropInfo']['m_Props']
        if default is None:
            t['m_ParsedForm']['m_PropInfo']['m_Props'] = [p for p in props if p['m_Name'] != '_MainTex']
        else:
            for p in props:
                if p['m_Name'] == '_MainTex':
                    p['m_DefTexture']['m_DefaultName'] = default
                    p['m_DefTexture']['m_TexDim'] = 2
        o.save_typetree(t, node)
        print(f'{name}: _MainTex {"undeclared" if default is None else repr(default)}')
    with open(dest, 'wb') as f:
        f.write(env.file.save())


if __name__ == '__main__':
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
