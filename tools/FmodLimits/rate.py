#!/usr/bin/env python3
"""Real-time FMOD Studio load test without the game (docs/research/vole-audio.md, "Event limits
across the banks"): the game's FMOD library and banks, Studio in its normal asynchronous mode,
and one event started RATE times a second (create, 3D position, volume, start, release, as
KFMOD's one-shots) from a 60 Hz loop standing in for the main thread. Runs on Linux and on
Windows (python.exe with fmodstudioL.dll). Logs a line a second: Studio command queue (usage,
peak, capacity, stalls), handles, Studio and mixer CPU, live instances, channels playing, and
the time the loop spent in FMOD calls.

usage: rate.py LIB BANKDIR... --out LOG [--event PATH] [--rate N] [--seconds S]
       [--output nosound|wavwriter|auto] [--wav FILE] [--dsp 1024x4] [--period MS]
       [--queue BYTES] [--maxch N] [--timer1 1] [--spread 0|1]
"""

import ctypes as C, sys, time, argparse, os

ap = argparse.ArgumentParser()
ap.add_argument('lib')
ap.add_argument('bankdir', nargs='+')
ap.add_argument('--out', required=True)
ap.add_argument('--event', default='event:/Creatures/Generic Movement Materials/Rock/Rock_creature_land')
ap.add_argument('--rate', type=float, default=2900)
ap.add_argument('--seconds', type=float, default=30)
ap.add_argument('--output', default='nosound')
ap.add_argument('--wav', default='')
ap.add_argument('--dsp', default='1024x4')
ap.add_argument('--period', type=int, default=0)
ap.add_argument('--spread', type=int, default=0)
ap.add_argument('--queue', type=int, default=0)
ap.add_argument('--maxch', type=int, default=512)
ap.add_argument('--timer1', type=int, default=0)
a = ap.parse_args()

F = C.CDLL(a.lib)
P = C.c_void_p
if a.timer1 and sys.platform == 'win32':
    C.windll.winmm.timeBeginPeriod(1)  # 1 ms system timer for this process


class VEC(C.Structure):
    _fields_ = [('x', C.c_float), ('y', C.c_float), ('z', C.c_float)]


class ATTR(C.Structure):
    _fields_ = [('position', VEC), ('velocity', VEC), ('forward', VEC), ('up', VEC)]


class BINFO(C.Structure):
    _fields_ = [('cur', C.c_int), ('peak', C.c_int), ('cap', C.c_int), ('stalls', C.c_int), ('stalltime', C.c_float)]


class BUSE(C.Structure):
    _fields_ = [('q', BINFO), ('h', BINFO)]


class SCPU(C.Structure):
    _fields_ = [('update', C.c_float)]


class CCPU(C.Structure):
    _fields_ = [('dsp', C.c_float), ('stream', C.c_float), ('geometry', C.c_float), ('update', C.c_float), ('c1', C.c_float), ('c2', C.c_float)]


class ADV(C.Structure):
    _fields_ = [
        ('cbsize', C.c_int),
        ('commandqueuesize', C.c_uint),
        ('handleinitialsize', C.c_uint),
        ('studioupdateperiod', C.c_int),
        ('idlesampledatapoolsize', C.c_int),
        ('streamingscheduledelay', C.c_uint),
        ('encryptionkey', C.c_char_p),
    ]


def ck(r, w):
    if r:
        raise SystemExit(f'{w}: FMOD error {r}')


s = P()
ck(F.FMOD_Studio_System_Create(C.byref(s), 0x00020312), 'create')
core = P()
F.FMOD_Studio_System_GetCoreSystem(s, C.byref(core))
out = {'auto': 0, 'nosound': 2, 'wavwriter': 3}[a.output]
ck(F.FMOD_System_SetOutput(core, out), 'output')
F.FMOD_System_SetSoftwareFormat(core, 48000, 3, 0)
F.FMOD_System_SetSoftwareChannels(core, 128)
ln, num = map(int, a.dsp.split('x'))
ck(F.FMOD_System_SetDSPBufferSize(core, ln, num), 'dsp')
adv = ADV()
adv.cbsize = C.sizeof(ADV)
F.FMOD_Studio_System_GetAdvancedSettings(s, C.byref(adv))
if a.period:
    adv.studioupdateperiod = a.period
if a.queue:
    adv.commandqueuesize = a.queue
ck(F.FMOD_Studio_System_SetAdvancedSettings(s, C.byref(adv)), 'advanced')
extra = C.c_char_p(a.wav.encode()) if a.wav else None
ck(F.FMOD_Studio_System_Initialize(s, a.maxch, 0, 0, extra), 'init')
adv2 = ADV()
adv2.cbsize = C.sizeof(ADV)
F.FMOD_Studio_System_GetAdvancedSettings(s, C.byref(adv2))
lis = ATTR(VEC(0, 0, 0), VEC(0, 0, 0), VEC(0, 0, 1), VEC(0, 1, 0))
F.FMOD_Studio_System_SetListenerAttributes(s, 0, C.byref(lis), None)
for d in a.bankdir:
    for fn in sorted(os.listdir(d), key=lambda f: (not f.startswith('Master Bank'), f)):
        if fn.endswith('.bank'):
            b = P()
            F.FMOD_Studio_System_LoadBankFile(s, os.path.join(d, fn).encode(), 0, C.byref(b))
F.FMOD_Studio_System_FlushCommands(s)
desc = P()
ck(F.FMOD_Studio_System_GetEvent(s, a.event.encode(), C.byref(desc)), 'getEvent ' + a.event)
F.FMOD_Studio_EventDescription_LoadSampleData(desc)
F.FMOD_Studio_System_FlushSampleLoading(s)
log = open(a.out, 'w')
log.write(
    f'# {sys.platform} output {a.output} dsp {ln}x{num} studioupdateperiod {adv2.studioupdateperiod} queue {adv2.commandqueuesize} handles0 {adv2.handleinitialsize} rate {a.rate} maxch {a.maxch} timer1 {a.timer1} spread {a.spread} event {a.event}\n'
)
due = 0.0
t0 = time.perf_counter()
last = t0
n = 0
fmod_t = 0.0
worst = 0.0
frames = 0
made = 0
while True:
    now = time.perf_counter()
    if now - t0 >= a.seconds:
        break
    due += a.rate / 60.0
    f0 = time.perf_counter()
    while due >= 1:
        due -= 1
        n += 1
        i = P()
        F.FMOD_Studio_EventDescription_CreateInstance(desc, C.byref(i))
        x = float((n % 40) - 20) if a.spread else 0.0
        y = float((n // 40 % 20) - 10) if a.spread else 0.0
        at = ATTR(VEC(x, y, 0), VEC(0, 0, 0), VEC(0, 0, 1), VEC(0, 1, 0))
        F.FMOD_Studio_EventInstance_Set3DAttributes(i, C.byref(at))
        F.FMOD_Studio_EventInstance_SetVolume(i, C.c_float(1.0))
        F.FMOD_Studio_EventInstance_Start(i)
        F.FMOD_Studio_EventInstance_Release(i)
        made += 1
    F.FMOD_Studio_System_Update(s)
    dt = time.perf_counter() - f0
    fmod_t += dt
    worst = max(worst, dt)
    frames += 1
    rest = 1 / 60.0 - (time.perf_counter() - now)
    if rest > 0:
        time.sleep(rest)
    if time.perf_counter() - last >= 1.0:
        last = time.perf_counter()
        u = BUSE()
        F.FMOD_Studio_System_GetBufferUsage(s, C.byref(u))
        F.FMOD_Studio_System_ResetBufferUsage(s)
        sc, cc = SCPU(), CCPU()
        F.FMOD_Studio_System_GetCPUUsage(s, C.byref(sc), C.byref(cc))
        live = C.c_int()
        F.FMOD_Studio_EventDescription_GetInstanceCount(desc, C.byref(live))
        ch, real = C.c_int(), C.c_int()
        F.FMOD_System_GetChannelsPlaying(core, C.byref(ch), C.byref(real))
        log.write(
            f't {last - t0:.0f} frames {frames} made {made} fmodcalls {fmod_t * 1000:.0f}ms worst {worst * 1000:.0f}ms q {u.q.cur}/{u.q.peak}/{u.q.cap} stalls {u.q.stalls} {u.q.stalltime:.3f}s handles {u.h.cur}/{u.h.peak} studio {sc.update:.1f}% dsp {cc.dsp:.1f}% live {live.value} ch {ch.value} real {real.value}\n'
        )
        log.flush()
        fmod_t = worst = 0.0
        frames = made = 0
F.FMOD_Studio_System_Release(s)
log.write('# done\n')
