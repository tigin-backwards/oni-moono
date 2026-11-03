#!/usr/bin/env python3
"""A synthetic saves tree shaped like a real one, for the Load screen with many saves
(docs/research/load-screen-many-saves.md, tests/e2e/load-screen-many-saves.yaml).
  scripts/load-screen-saves.py REAL TEMPLATE.sav OUT
REAL is only listed and stat'ed (never opened): OUT gets the same folders and file names, each
.sav at the real one's size (sparse) and modification time with a header made from TEMPLATE's
(baseName the colony folder's name, colonyGuid from it, isAutoSave from the auto_save folder; an
empty file stays empty), and each .png a hard link to one of two generated previews of the
real shapes (528x1842 for files over 500 KB, else 528x306; the game reads only their pixels).
Run the e2e spec with E2E_CLOUD_SAVES=OUT; never point a player at REAL."""
import json, os, random, struct, sys, uuid, zlib


def png(path, w, h, noise=0.12):
    rnd = random.Random(w * h)
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            v = (x * 3 + y * 5) & 255
            if rnd.random() < noise:
                v = rnd.randrange(256)
            rows += bytes((v, (v * 7) & 255, (v * 13) & 255, 255))

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress(bytes(rows), 6)) + chunk(b'IEND', b''))


def main(real, template, out):
    real, out = os.path.abspath(real), os.path.abspath(out)
    if out.startswith(real + os.sep) or out == real:
        sys.exit('OUT must not be inside REAL')
    with open(template, 'rb') as f:
        build, size, version, compression = struct.unpack('<IiIi', f.read(16))
        info = json.loads(f.read(size).decode('utf-8'))
    os.makedirs(out, exist_ok=True)
    big, small = os.path.join(out, '.preview-big.png'), os.path.join(out, '.preview-small.png')
    png(big, 528, 1842)
    png(small, 528, 306)
    saves = previews = 0
    for folder, _, names in os.walk(real):
        rel = os.path.relpath(folder, real)
        target = os.path.join(out, rel)
        os.makedirs(target, exist_ok=True)
        parts = rel.split(os.sep)
        for name in names:
            st = os.stat(os.path.join(folder, name))
            path = os.path.join(target, name)
            if name.endswith('.sav'):
                with open(path, 'wb') as f:
                    if st.st_size >= 1024:
                        header = dict(info, baseName=parts[0], originalSaveName=name,
                                      colonyGuid=str(uuid.uuid5(uuid.NAMESPACE_URL, parts[0])),
                                      isAutoSave='auto_save' in parts)
                        body = json.dumps(header, separators=(',', ':')).encode('utf-8')
                        f.write(struct.pack('<IiIi', build, len(body), version, compression) + body)
                    f.truncate(st.st_size)
                os.utime(path, ns=(st.st_atime_ns, st.st_mtime_ns))
                saves += 1
            elif name.endswith('.png'):
                if os.path.lexists(path):
                    os.remove(path)
                os.link(big if st.st_size > 500000 else small, path)
                previews += 1
    print(f'{saves} saves, {previews} previews under {out}')


if __name__ == '__main__':
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
