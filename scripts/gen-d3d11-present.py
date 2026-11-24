#!/usr/bin/env python3
"""The Direct3D 11 device's presenter program (src/OniMoono.Desktop/D3d11/D3d11Present.cs): its
two DXBC blobs, PresentVertexDxbc and PresentPixelDxbc, are what the Windows SDK's fxc makes of
the HLSL in the comment above them.

    scripts/gen-d3d11-present.py           compile the HLSL with fxc and compare (exit 1: differ)
    scripts/gen-d3d11-present.py --write   compile and put the blobs into D3d11Present.cs

fxc runs on the Windows side, from WSL (FXC, default the SDK 10.0.26100.0 x64 fxc.exe), with
the flags FLAGS names. Not part of the product."""

import base64, os, re, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'src', 'OniMoono.Desktop', 'D3d11', 'D3d11Present.cs')
FXC = os.environ.get('FXC', '/mnt/c/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe')
FLAGS = ['/nologo', '/O3', '/Qstrip_reflect', '/Qstrip_debug']
# (constant, profile, entry point)
STAGES = (('PresentVertexDxbc', 'vs_4_0', 'vs'), ('PresentPixelDxbc', 'ps_4_0', 'ps'))


def hlsl(text):
    """The HLSL lines of the comment after 'from this HLSL:' (each '        //   ' + a line),
    up to the comment's blank line."""
    lines = text.splitlines()
    start = next(i for i, l in enumerate(lines) if l.strip().endswith('from this HLSL:'))
    out = []
    for line in lines[start + 2 :]:
        m = re.match(r'^\s*//   (.*)$', line)
        if not m:
            break
        out.append(m.group(1))
    if not out:
        raise ValueError('no HLSL found in the comment')
    return '\n'.join(out) + '\n'


def blobs(text):
    """{constant: bytes} as the file holds them."""
    found = {}
    for name, _, _ in STAGES:
        m = re.search(name + r' =\s*"([A-Za-z0-9+/=]+)";', text)
        if not m:
            raise ValueError(f'no {name} in {SOURCE}')
        found[name] = base64.b64decode(m.group(1))
    return found


def available():
    return os.path.exists(FXC)


def compile_all(source_hlsl):
    """{constant: bytes} fxc makes of the HLSL (on the Windows side, in its temp folder)."""
    temp = subprocess.run(['cmd.exe', '/c', 'echo %TEMP%'], capture_output=True, text=True, cwd='/mnt/c').stdout.strip()
    stage = subprocess.run(['wslpath', '-u', temp], capture_output=True, text=True).stdout.strip()
    with tempfile.TemporaryDirectory(dir=stage, prefix='oni-d3d11-present-') as d:
        win = subprocess.run(['wslpath', '-w', d], capture_output=True, text=True).stdout.strip()
        with open(os.path.join(d, 'present.hlsl'), 'w', newline='\n') as f:
            f.write(source_hlsl)
        out = {}
        for name, profile, entry in STAGES:
            result = subprocess.run(
                [FXC, *FLAGS, '/T', profile, '/E', entry, '/Fo', win + '\\' + entry + '.cso', win + '\\present.hlsl'], capture_output=True, text=True
            )
            if result.returncode != 0:
                raise RuntimeError(f'fxc {profile} failed: {result.stdout}{result.stderr}')
            with open(os.path.join(d, entry + '.cso'), 'rb') as f:
                out[name] = f.read()
        return out


def main():
    with open(SOURCE, encoding='utf-8') as f:
        text = f.read()
    made = compile_all(hlsl(text))
    if '--write' in sys.argv[1:]:
        for name, data in made.items():
            text = re.sub(
                name + r' =\s*"[A-Za-z0-9+/=]+";', lambda m, name=name, data=data: f'{name} =\n            "{base64.b64encode(data).decode()}";', text
            )
        with open(SOURCE, 'w', encoding='utf-8') as f:
            f.write(text)
        print('written')
        return 0
    held = blobs(text)
    differ = [name for name in made if made[name] != held[name]]
    for name in differ:
        print(f'{name}: fxc makes {len(made[name])} bytes, the file holds {len(held[name])} that differ')
    if not differ:
        print('the presenter blobs are fxc\'s output of the HLSL comment')
    return 1 if differ else 0


if __name__ == '__main__':
    sys.exit(main())
