#!/usr/bin/env python3
"""Offline survey of FMOD event limits (docs/research/vole-audio.md, "Event limits across the
banks"). FMOD's API doesn't expose an event's max instances, stealing mode or cooldown, so this
loads every bank of an ONI build with the game's own FMOD library (libfmodstudioL.so or
fmodstudioL.dll, FMOD 2.03.12) and measures them: N instances started at the listener in one
update (which survive: the first k = no stealing, the last k = stealing, the rest virtual =
virtualize), then one new instance against k old ones, quieter or further, to tell oldest from
quietest and furthest. Output NOSOUND_NRT with synchronous updates, so every run is the same.
One JSON line per event: path, oneshot, 3D, min/max distance, length, user properties, class,
limit, stealing, cooldown. About 10 minutes for all 5,300 events of U60.

usage: limits.py LIB OUT.jsonl BANKDIR... [--override DIR] [--only REGEX] [--n 64]
Banks in later directories (and --override) replace earlier ones of the same file name.
"""
import ctypes as C, sys, os, re, json, time, argparse

ap = argparse.ArgumentParser()
ap.add_argument('lib'); ap.add_argument('out'); ap.add_argument('dirs', nargs='+')
ap.add_argument('--override', action='append', default=[])
ap.add_argument('--only'); ap.add_argument('--n', type=int, default=64)
a = ap.parse_args()

F = C.CDLL(a.lib)
VER = 0x00020312
P = C.c_void_p

class VEC(C.Structure): _fields_ = [('x', C.c_float), ('y', C.c_float), ('z', C.c_float)]
class ATTR(C.Structure): _fields_ = [('position', VEC), ('velocity', VEC), ('forward', VEC), ('up', VEC)]
class GUID(C.Structure): _fields_ = [('d1', C.c_uint32), ('d2', C.c_uint16), ('d3', C.c_uint16), ('d4', C.c_ubyte * 8)]
class UVAL(C.Union): _fields_ = [('i', C.c_int), ('f', C.c_float), ('s', C.c_char_p)]
class UPROP(C.Structure): _fields_ = [('name', C.c_char_p), ('type', C.c_int), ('v', UVAL)]

def ck(r, what):
    if r != 0: raise RuntimeError(f'{what}: FMOD error {r}')

sysp = P(); ck(F.FMOD_Studio_System_Create(C.byref(sysp), VER), 'create')
core = P(); ck(F.FMOD_Studio_System_GetCoreSystem(sysp, C.byref(core)), 'core')
ck(F.FMOD_System_SetOutput(core, 4), 'output NOSOUND_NRT')
ck(F.FMOD_System_SetSoftwareFormat(core, 48000, 3, 0), 'format')  # stereo, as the game
ck(F.FMOD_System_SetSoftwareChannels(core, 128), 'channels')      # the game's 128
ck(F.FMOD_System_SetDSPBufferSize(core, 1024, 4), 'dsp')
ck(F.FMOD_Studio_System_Initialize(sysp, 1024, 0x2, 0, None), 'init')  # SYNCHRONOUS_UPDATE

UPD = [0]
def upd(n=1):
    for _ in range(n): F.FMOD_Studio_System_Update(sysp); UPD[0] += 1

lis = ATTR(VEC(0, 0, 0), VEC(0, 0, 0), VEC(0, 0, 1), VEC(0, 1, 0))
ck(F.FMOD_Studio_System_SetListenerAttributes(sysp, 0, C.byref(lis), None), 'listener')

files = {}
for d in a.dirs + a.override:
    for fn in sorted(os.listdir(d)):
        if fn.endswith('.bank'): files[fn] = os.path.join(d, fn)
order = sorted(files, key=lambda f: (not f.startswith('Master Bank'), f))
banks = []
for fn in order:
    b = P(); r = F.FMOD_Studio_System_LoadBankFile(sysp, files[fn].encode(), 0, C.byref(b))
    if r != 0: print(f'load {fn}: error {r}', file=sys.stderr); continue
    banks.append((fn, b))
upd(2)

def sget(fn, h):
    buf = C.create_string_buffer(1024); n = C.c_int()
    return buf.value.decode() if fn(h, buf, 1024, C.byref(n)) == 0 else None

def guid_s(g):
    return '{%08x-%04x-%04x-%s-%s}' % (g.d1, g.d2, g.d3, bytes(g.d4[:2]).hex(), bytes(g.d4[2:]).hex())

events = []
for fn, b in banks:
    n = C.c_int(); F.FMOD_Studio_Bank_GetEventCount(b, C.byref(n))
    if n.value == 0: continue
    arr = (P * n.value)(); got = C.c_int()
    F.FMOD_Studio_Bank_GetEventList(b, arr, n.value, C.byref(got))
    for i in range(got.value): events.append((fn, P(arr[i])))

def flag(fname, d):
    v = C.c_int(); F[fname](d, C.byref(v)); return bool(v.value)

ST = {0: 'PLAYING', 1: 'SUSTAINING', 2: 'STOPPED', 3: 'STARTING', 4: 'STOPPING'}
def state(i):
    s = C.c_int(); F.FMOD_Studio_EventInstance_GetPlaybackState(i, C.byref(s))
    v = C.c_int(); F.FMOD_Studio_EventInstance_IsVirtual(i, C.byref(v))
    return s.value, bool(v.value)

def make(d, dist=0.0, vol=1.0):
    i = P(); ck(F.FMOD_Studio_EventDescription_CreateInstance(d, C.byref(i)), 'createInstance')
    at = ATTR(VEC(dist, 0, 0), VEC(0, 0, 0), VEC(0, 0, 1), VEC(0, 1, 0))
    F.FMOD_Studio_EventInstance_Set3DAttributes(i, C.byref(at))
    F.FMOD_Studio_EventInstance_SetVolume(i, C.c_float(vol))
    return i

def settle(insts, cap=300):
    for k in range(cap):
        upd()
        if not any(state(i)[0] == 3 for i in insts): return k + 1
    return cap

def alive(st): return st[0] in (0, 1)   # PLAYING / SUSTAINING; STOPPING = stolen, fading

def kill(insts):
    for i in insts:
        F.FMOD_Studio_EventInstance_Stop(i, 1); F.FMOD_Studio_EventInstance_Release(i)
    upd(2)

def gap(): upd(250)   # ~5.3 s of mix, past any cooldown

def steal_test(d, k, old_dist, old_vol, new_dist, new_vol):
    olds = [make(d, old_dist, old_vol) for _ in range(k)]
    for i in olds: F.FMOD_Studio_EventInstance_Start(i)
    settle(olds)
    nw = make(d, new_dist, new_vol); F.FMOD_Studio_EventInstance_Start(nw); settle([nw])
    so = [state(i) for i in olds]; sn = state(nw)
    kill(olds + [nw]); gap()
    if alive(sn) and sn[1]: return 'new-virtual'
    if alive(sn) and not alive(so[0]) and all(alive(s) for s in so[1:]): return 'steal-first'
    if alive(sn) and not all(alive(s) for s in so): return 'steal-other'
    if alive(sn): return 'both'
    return 'drop-new'

out = open(a.out, 'w')
rx = re.compile(a.only) if a.only else None
t0 = time.time()
for idx, (bank, d) in enumerate(events):
    path = sget(F.FMOD_Studio_EventDescription_GetPath, d)
    g = GUID(); F.FMOD_Studio_EventDescription_GetID(d, C.byref(g))
    if path is None: path = guid_s(g)
    if rx and not rx.search(path): continue
    rec = {'bank': bank, 'path': path, 'guid': guid_s(g)}
    rec['oneshot'] = flag('FMOD_Studio_EventDescription_IsOneshot', d)
    rec['is3d'] = flag('FMOD_Studio_EventDescription_Is3D', d)
    rec['stream'] = flag('FMOD_Studio_EventDescription_IsStream', d)
    rec['sustain'] = flag('FMOD_Studio_EventDescription_HasSustainPoint', d)
    mn, mx = C.c_float(), C.c_float(); F.FMOD_Studio_EventDescription_GetMinMaxDistance(d, C.byref(mn), C.byref(mx))
    rec['min'], rec['max'] = round(mn.value, 2), round(mx.value, 2)
    ln = C.c_int(); F.FMOD_Studio_EventDescription_GetLength(d, C.byref(ln)); rec['len_ms'] = ln.value
    pc = C.c_int(); F.FMOD_Studio_EventDescription_GetUserPropertyCount(d, C.byref(pc)); props = {}
    for j in range(pc.value):
        up = UPROP()
        if F.FMOD_Studio_EventDescription_GetUserPropertyByIndex(d, j, C.byref(up)) == 0:
            nm = up.name.decode()
            props[nm] = up.v.i if up.type in (0, 1) else round(up.v.f, 3) if up.type == 2 else (up.v.s or b'').decode()
    rec['props'] = props
    pcount = C.c_int(); F.FMOD_Studio_EventDescription_GetParameterDescriptionCount(d, C.byref(pcount)); rec['nparams'] = pcount.value
    # sample data, so instances don't wait on loading
    F.FMOD_Studio_EventDescription_LoadSampleData(d)
    for _ in range(2000):
        s = C.c_int(); F.FMOD_Studio_EventDescription_GetSampleLoadingState(d, C.byref(s))
        if s.value in (3, 4): break
        upd(); time.sleep(0.001)
    try:
        N = a.n
        ins = [make(d) for _ in range(N)]
        for i in ins: F.FMOD_Studio_EventInstance_Start(i)
        w = settle(ins)
        s = [state(i) for i in ins]
        upd(50); s50 = [state(i) for i in ins]
        kill(ins); gap()
        al = [k for k, x in enumerate(s) if alive(x)]
        real = [k for k in al if not s[k][1]]; virt = [k for k in al if s[k][1]]
        rec['burst'] = {'alive': len(al), 'real': len(real), 'virtual': len(virt), 'settle': w,
                        'stopping': sum(1 for x in s if x[0] == 4),
                        'alive50': sum(1 for x in s50 if alive(x)), 'real50': sum(1 for x in s50 if alive(x) and not x[1])}
        if not al: rec['class'] = 'ends-at-once'; rec['limit'] = 0
        elif len(real) == N: rec['class'] = 'no-limit'; rec['limit'] = None
        elif virt and len(al) == N: rec['class'] = 'virtualize'; rec['limit'] = len(real)
        elif real == list(range(len(real))): rec['class'] = 'none'; rec['limit'] = len(real)
        elif real == list(range(N - len(real), N)): rec['class'] = 'steal'; rec['limit'] = len(real)
        else: rec['class'] = 'other'; rec['limit'] = len(real); rec['pattern'] = ''.join('V' if x[1] and alive(x) else 'P' if alive(x) else '.' for x in s)
        if al:
            # cooldown: after one instance starts (and is stopped), how many updates until another plays
            A = make(d); F.FMOD_Studio_EventInstance_Start(A); u0 = UPD[0]; settle([A]); F.FMOD_Studio_EventInstance_Stop(A, 1); upd()
            cd = None
            for t in range(300):
                B = make(d); F.FMOD_Studio_EventInstance_Start(B); settle([B], 3); ok = alive(state(B)); kill([B])
                if ok: cd = (UPD[0] - u0) if t else 0; break
            kill([A]); gap()
            rec['cooldown_ms'] = None if cd is None else round(cd * 1024 / 48)  # updates since A started, upper bound
            if cd: rec['class'] += '+cooldown'
            if rec['class'] == 'steal':
                k = rec['limit']; far = max(rec['max'] * 0.9, 1.0); mid = (rec['min'] + rec['max']) / 2 if rec['max'] > 0 else 1.0
                rec['b0_newquiet'] = steal_test(d, k, 0, 1.0, 0, 0.05)
                if rec['is3d']:
                    rec['b1_newfar'] = steal_test(d, k, 0, 1.0, far, 1.0)
                    rec['b2_newfar_oldquiet'] = steal_test(d, k, 0, 0.01, mid, 1.0)
                b0, b1, b2 = rec['b0_newquiet'], rec.get('b1_newfar'), rec.get('b2_newfar_oldquiet')
                # b1 can steal for quietest too: many events attenuate by a game parameter, not distance
                if b0 == 'steal-first' and b1 in (None, 'steal-first') and b2 in (None, 'steal-first'): rec['steal'] = 'oldest'
                elif b0 == 'drop-new' and (b1 is None or 'steal' in b1 or 'steal' in (b2 or '')): rec['steal'] = 'quietest'
                elif b1 == 'drop-new' and b2 == 'drop-new': rec['steal'] = 'furthest'
                else: rec['steal'] = 'unclear'
    except RuntimeError as e:
        rec['class'] = 'error'; rec['err'] = str(e)
    F.FMOD_Studio_EventDescription_UnloadSampleData(d); upd()
    out.write(json.dumps(rec) + '\n'); out.flush()
    if idx % 200 == 0: print(f'{idx}/{len(events)} {time.time() - t0:.0f}s', file=sys.stderr)
print(f'done {len(events)} events in {time.time() - t0:.0f}s', file=sys.stderr)
