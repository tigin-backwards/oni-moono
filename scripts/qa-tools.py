#!/usr/bin/env python3
"""Helpers of scripts/qa.sh.
qa-tools.py signatures LOG [ERRORS]     the log's exceptions and errors, one normalized line each;
                                        ERRORS (the player's ONI_LOG_ERRORS file) adds the
                                        errors logged as plain lines
qa-tools.py new-signatures REF NEW      lines of NEW whose canonical form REF lacks (signature files)
qa-tools.py image REFERENCE NEW DIFF [--crop X,Y,W,H] [--crop-new X,Y,W,H] [--mask X,Y,W,H]...
                                        fraction of (unmasked) pixels differing visibly; writes
                                        DIFF (a mask). --crop cuts both (--crop-new the new one
                                        apart); masks are relative to the crop
qa-tools.py verify LOG [--expect FILE]  each verify mode's last report; exit 1 on a mismatch, and
                                        with FILE (one check name a line, # comments) on a named
                                        check that reported nothing: silence is not 0 wrong
qa-tools.py perf STDOUT                 steady-window frame rates and game time span
qa-tools.py memwatch OUT HOME PID       the memory stage's watch of a U57 player (MemWatch): its
                                        peak RSS, the frame of the peak and the RssAnon/RssFile
                                        split, stopping it once the first autosave has passed
                                        and RSS has settled
"""

import os, re, sys


def normalize(text):
    text = re.sub(r' \(at [^)]*\)', '', text)  # source locations change with every edit
    text = re.sub(r' in \S+:line \d+', '', text)  # .NET's form of the same
    text = re.sub(r'0x[0-9a-fA-F]+', 'N', text)
    text = re.sub(r'\d+(\.\d+)?', 'N', text)
    return text.strip()


# Runtime internals: the first frame of a trace only says which runtime threw (Mono's
# System.IO.FileStream, .NET's Interop.ThrowExceptionForIoErrno, Unity's or our logger, our
# engine's dispatch), so a signature keeps the message alone when that frame is one of these.
RUNTIME_FRAME = re.compile(
    r'^(System|Interop|Microsoft|Mono)\.|^UnityEngine\.(Debug|Logger|DebugLogHandler|StackTraceUtility)\.'
    r'|^(?:OniMoono|OniBrutal)\.'
)


def canonical(signature):
    """A signature as both runtimes write it: Mono and .NET word some exception messages
    differently ('…'. against "…", a trailing period or none), and stack frames come as
    "at A.B (x) [N] in <…>:N" (an exception's own, in both players; with symbols, in a file),
    "A:B(x)" (Unity's) and "A:B (x)" (ours before 2026-10-24). Idempotent, so it also applies
    to signatures stored by an earlier version (references)."""
    signature = signature.replace('OniBrutal.', 'OniMoono.')  # stored before the rename
    message, _, frame = signature.partition(' @ ')
    # A trace appended to the message on its own line ("Error:" + message + " " + StackTrace, as
    # WorldGen logs it) starts with its first frame: that frame comes before the one read from
    # the next line, unless it is the runtime's own. .NET leaves out a frame the JIT inlined (a
    # Dictionary indexer's), which Mono's trace has.
    inline = re.search(r'\s{2,}at (\S.*)$', message)
    if inline:
        message = message[: inline.start()]
        first = clean_frame(inline.group(1))
        if not RUNTIME_FRAME.search(first):
            frame = first
    message = re.sub(r'\.$', '', message.replace("'", '"').rstrip())
    # Mono says only "Specified cast is not valid." of a failed unbox or cast; .NET names both
    # types ("Unable to cast object of type "A" to type "B"."): the frame tells them apart.
    message = re.sub(r'(InvalidCastException: )Unable to cast object of type ".*" to type ".*"$', r'\1Specified cast is not valid', message)
    # A path into the run's own folder (out/e2e/SPEC/RUN_ID/PLAYER-N/home/...) names the run
    # id, which differs between the invocations that ran the original and ours.
    message = re.sub(r'/[^"\s]*/(?:original|ours)-N/home/', 'RUN/home/', message)
    if frame:
        frame = clean_frame(frame)
        if RUNTIME_FRAME.search(frame):
            frame = ''
    return message + (' @ ' + frame if frame else '')


def clean_frame(frame):
    """A frame's method alone, as both runtimes name it."""
    frame = re.sub(r'^at ', '', frame.strip())
    # A Harmony replacement's frame is its patched method's: Mono names it "(wrapper
    # dynamic-method) MonoMod.Utils.DynamicMethodDefinition.T.M_PatchN", .NET "T.M_PatchN", and
    # ours patches some of Harmony's own methods (ARCHITECTURE D14, D38) that the original runs
    # unpatched, so a mod's failed patch shows "PatchProcessor.Patch_Patch2" in ours alone.
    frame = re.sub(r'^\(wrapper dynamic-method\) (?:MonoMod\.Utils\.DynamicMethodDefinition\.)?', '', frame)
    frame = re.sub(r'_Patch(?:N|\d+)(?=\s*(?:\(|$))', '', frame)
    # Mono's location: "[0x…] in <mvid>:0", or the source file with symbols (ours has the
    # engine's).
    frame = re.sub(r' (\[N\] in .*|in <[^>]*>(:N)?)$', '', frame)
    frame = re.sub(r'\s*\(.*\)$', '', frame).replace(':', '.')
    # Mono names a generic type's parameters ("Dictionary`2[TKey,TValue]"), .NET does not.
    return re.sub(r"(`(?:N|\d+))\[[^\]]*\]", r"\1", frame)


def unknown(signatures, reference):
    """The SIGNATURES not in REFERENCE once both are canonical. A reference entry with no frame
    (written when its first frame was runtime internals) stands for its message with any frame,
    and a signature with no frame matches its message with any: ours writes a logged error
    without the stack trace Unity's player prints after one from a worker thread."""
    known = {canonical(s) for s in reference}
    messages = {k.partition(' @ ')[0] for k in known}

    def new(s):
        c = canonical(s)
        message, _, frame = c.partition(' @ ')
        return c not in known and message not in known and (frame or message not in messages)

    return sorted(s for s in signatures if new(s))


def new_signatures(reference, new):
    read = lambda path: open(path, encoding='utf-8', errors='replace').read().splitlines()
    for line in unknown(read(new), read(reference)):
        print(line)


def joined_inner_exceptions(lines):
    """LINES with each inner exception on its outer one's line: .NET's Exception.ToString starts
    " ---> Inner: message" on a line of its own, Mono's continues the outer message's line
    (ARCHITECTURE D37)."""
    out = []
    for line in lines:
        if out and re.match(r'\s*---> ', line):
            out[-1] = out[-1].rstrip() + ' ' + line.strip()
        else:
            out.append(line)
    return out


def signatures(path, errors=None):
    lines = joined_inner_exceptions(open(path, encoding='utf-8', errors='replace').read().splitlines())
    found = set()
    for i, line in enumerate(lines):
        body = re.sub(r'^\[[^\]]*\] \[\d+\] \[[A-Z]+\] ', '', line)
        if re.search(r'\b\w+(Exception|Error)\b:', body) or '[ERROR]' in line:
            frame = ''
            for nxt in lines[i + 1 : i + 9]:
                if re.match(r'\s*(at |[\w.`]+[:.][\w<>`]+ ?\()', nxt):
                    frame = nxt.strip()
                    if not RUNTIME_FRAME.search(re.sub(r'^at ', '', frame).replace(':', '.')):
                        break  # the first frame past the runtime's own
                    frame = ''
            found.add(canonical(normalize(body) + (' @ ' + normalize(frame) if frame else '')))
    # A UnityEngine.Debug.LogError's message is a plain line in the log, as in Unity's player
    # (the game's own Debug.LogError writes an [ERROR] line before it): the player lists those
    # messages in ERRORS. One the log already gave a signature (an [ERROR] line's) adds none.
    if errors and os.path.exists(errors):
        messages = {s.partition(' @ ')[0] for s in found}
        for line in open(errors, encoding='utf-8', errors='replace').read().splitlines():
            s = canonical(normalize(line))
            if s and s not in messages:
                found.add(s)
                messages.add(s)
    for s in sorted(found):
        print(s)


def rect(text):
    x, y, w, h = (int(round(float(v))) for v in text.split(','))
    return x, y, w, h


def compare_images(reference, new, diff, crop=None, crop_new=None, masks=(), level=40, size_tolerance=2):
    """The fraction of unmasked pixels whose largest channel difference exceeds LEVEL. CROP
    (x, y, w, h) cuts both images, CROP_NEW the new one instead; MASKS are rectangles relative to
    the crop that do not count. Sizes up to SIZE_TOLERANCE pixels apart are trimmed to the
    smaller. DIFF gets the mask image: differing pixels white, masked ones grey."""
    from PIL import Image, ImageChops
    import os
    import numpy as np

    if not os.path.exists(reference) or not os.path.exists(new):
        return {'fraction': 1.0, 'note': 'missing'}
    images = []
    for path, box in ((reference, crop), (new, crop_new or crop)):
        image = Image.open(path).convert('RGB')
        if box:
            x, y, w, h = (int(round(v)) for v in box)
            clipped = (max(0, x), max(0, y), min(image.width, x + w), min(image.height, y + h))
            if clipped[2] <= clipped[0] or clipped[3] <= clipped[1]:
                return {'fraction': 1.0, 'note': f'crop {list(box)} is off the image'}
            image = image.crop(clipped)
        images.append(image)
    a, b = images
    if a.size != b.size:
        if abs(a.width - b.width) > size_tolerance or abs(a.height - b.height) > size_tolerance:
            return {'fraction': 1.0, 'note': f'size {a.size} against {b.size}'}
        size = (min(a.width, b.width), min(a.height, b.height))
        a, b = a.crop((0, 0) + size), b.crop((0, 0) + size)
    differs = np.asarray(ImageChops.difference(a, b)).max(axis=2) > level
    counted = np.ones(differs.shape, dtype=bool)
    for m in masks:
        x, y, w, h = (int(round(v)) for v in m)
        counted[max(0, y) : max(0, y + h), max(0, x) : max(0, x + w)] = False
    if diff:
        # Always a PNG, whatever the path's extension (or none, as /dev/null).
        Image.fromarray(np.where(counted, differs * 255, 128).astype('uint8')).save(diff, format='PNG')
    total = int(counted.sum())
    fraction = float((differs & counted).sum()) / total if total else 0.0
    return {'fraction': round(fraction, 6), 'size': list(a.size), 'masked': int(counted.size - total)}


def image(reference, new, diff, *options):
    crop = crop_new = None
    masks, level = [], 40
    for name, value in zip(options[::2], options[1::2]):
        if name == '--crop':
            crop = rect(value)
        elif name == '--crop-new':
            crop_new = rect(value)
        elif name == '--mask':
            masks.append(rect(value))
        elif name == '--level':
            level = int(value)
        else:
            sys.exit(f'unknown option {name}')
    result = compare_images(reference, new, diff, crop, crop_new, masks, level)
    print(f'{result["fraction"]:.4f}' + (f' {result["note"]}' if 'note' in result else ''))


# Verify-mode lines: periodic summaries (each kind of check has its own, all counts must be zero)
# and per-incident lines (any is a failure).
# A summary is a line of a check's name whose report counts what must be zero ("0 wrong");
# compact-nav-links and paged-path-grids name the moment ("nav links verify (in play)").
VERIFY_NAMES = (
    r'(fast-[a-z-]+|path-probe-repair|instant-reachability|trim-anim-lists|nav links verify(?: \([a-z ]+\))?|path grids verify'
    r'|canvas skips?|canvas products|component layouts|texel copies|partial uploads|parallel draws|bindless handles'
    r'|raw texture loads|raycast cache|cull cache|fetch-by-cell|canvas cull|mesh reuse|property sheets|pass states)'
)
ZERO_WORDS = (
    r"(?:wrong skips|wrong|stale items not marked|stale|mismatches|changed while copying|differed|cell\(s\) differ"
    r"|differ|otherwise than a flood|otherwise than the game's|changes the feed missed)"
)
ZERO_COUNTS = re.compile(r'(?<![\w.])(\d[\d,]*) ' + ZERO_WORDS + r'(?![\w(])')
# Lines that report one mismatch each (any is a failure).
INCIDENT = re.compile(r"\(\d+\)$|answers otherwise|the feed missed|differs from|threw|wrong|stale|mismatch|ran \d+ times")


def verify(path, *options):
    expected = []
    if options:
        if len(options) != 2 or options[0] != '--expect':
            sys.exit('verify LOG [--expect FILE]')
        with open(options[1], encoding='utf-8') as f:
            expected = [line.split('#', 1)[0].strip() for line in f]
        expected = [name for name in expected if name]
    last, incidents = {}, []
    for line in open(path, encoding='utf-8', errors='replace'):
        if any(
            w in line
            for w in (
                'canvas skip wrong',
                'raycast cache wrong',
                'cull cache wrong',
                'canvas cull wrong',
                'mesh reuse wrong',
                'property sheets wrong',
                'pass states wrong',
                'upload record wrong',
            )
        ):
            incidents.append(line.strip())
            continue
        m = re.search(VERIFY_NAMES + r': (.*)$', line)
        if not m:
            continue
        name, report = m.group(1), m.group(2).strip()
        counts = ZERO_COUNTS.findall(report)
        if counts and not re.search(r'answers otherwise|feed missed a change', report):
            # The summary's shape (its words) names the check: one tweak can report several. What
            # follows a mismatch (`; first: ...`) and the time taken are not part of it.
            shape = re.sub(r'\d[\d,.]*', 'N', re.sub(r'; first: .*$', '', report))
            last[(name, shape)] = report
        elif INCIDENT.search(report):
            incidents.append(f'{name}: {report}')
    bad = False
    for (name, _), report in sorted(last.items()):
        ok = all(c == '0' for c in ZERO_COUNTS.findall(report))
        bad |= not ok
        print(f'{"ok  " if ok else "FAIL"} {name}: {report}')
    reported = {name for name, _ in last}
    for name in expected:
        if name not in reported:
            bad = True
            print(f'FAIL {name}: reported nothing')
    for i in incidents[:20]:
        print(f'FAIL incident {i}')
    bad |= bool(incidents)
    sys.exit(1 if bad else 0)


def perf(path):
    rows = []
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'PERF f(\d+) fps ([\d.]+) cycle (\d+)(?: time (\d+))?', line)
        if m:
            rows.append((int(m.group(1)), float(m.group(2)), int(m.group(3)), int(m.group(4) or 0)))
    steady = [r[1] for r in rows[1:]]  # the first window holds the load
    mean = sum(steady) / len(steady) if steady else 0.0
    span = rows[-1][3] - rows[0][3] if len(rows) > 1 else 0
    print(f'{mean:.2f} {span} {len(steady)}')


class MemWatch:
    """qa.sh's memory stage (memwatch): a player's resident memory against its frame count. The
    frame of a sample is estimated from PerfProbe's PERF lines (every 600 frames, with the frame
    rate since the last). Peak memory comes with the first autosave (about 2,500 frames after U57
    has loaded) and does not depend on the machine's load; the watch says to stop once an autosave
    has passed and RSS has not risen by more than RISE_KB for SETTLE frames since."""

    PERF = re.compile(r'PERF f(\d+) fps ([\d.]+)')
    AUTOSAVE = re.compile(r'(Saved to \[|background save written: ).*auto_save')

    def __init__(self, settle=1200, rise_kb=64 * 1024):
        self.settle, self.rise_kb = settle, rise_kb
        self.perf = None  # (frame, fps, wall time) of the last PERF line
        self.autosave = None  # the frame of the last autosave line
        self.peak = None  # the sample with the most RSS: {rss, anon, file, frame, time}
        self.level, self.risen = 0, None  # RSS that counts as a rise from, and the frame it last rose at
        self.frame = None

    def line(self, text, now):
        m = self.PERF.search(text)
        if m:
            self.perf = (int(m.group(1)), float(m.group(2)), now)
        elif self.AUTOSAVE.search(text):
            self.autosave = self.estimate(now)

    def estimate(self, now):
        """The frame at NOW: the last PERF line's, plus its frame rate's worth since (at most the
        600 frames to the next one); 0 before the first."""
        if not self.perf:
            return 0
        frame, fps, wall = self.perf
        return frame + int(min(600, max(0.0, now - wall) * fps))

    def sample(self, rss, anon, file, now):
        """One reading (kB); returns why to stop, or None."""
        self.frame = self.estimate(now)
        if self.peak is None or rss > self.peak['rss']:
            self.peak = {'rss': rss, 'anon': anon, 'file': file, 'frame': self.frame, 'time': now}
        if rss > self.level + self.rise_kb or self.risen is None:
            self.level, self.risen = max(rss, self.level), self.frame
        if self.autosave is not None and self.frame >= self.autosave and self.frame - max(self.risen, self.autosave) >= self.settle:
            return (
                f'the first autosave (f{self.autosave}) has passed and RSS has not risen by more than '
                f'{self.rise_kb // 1024} MB for {self.frame - max(self.risen, self.autosave)} frames'
            )
        return None

    def summary(self):
        p = self.peak or {'rss': 0, 'anon': 0, 'file': 0, 'frame': 0}
        return {
            'peak_kb': p['rss'],
            'peak_frame': p['frame'],
            'rss_anon_kb': p['anon'],
            'rss_file_kb': p['file'],
            'autosave_frame': self.autosave,
            'last_frame': self.frame,
        }


def proc_status(pid):
    """VmRSS, RssAnon and RssFile of a process in kB, or None once it has gone."""
    values = {}
    try:
        with open(f'/proc/{pid}/status') as f:
            for line in f:
                key, _, value = line.partition(':')
                if key in ('VmRSS', 'RssAnon', 'RssFile'):
                    values[key] = int(value.split()[0])
    except (OSError, ValueError):
        return None
    return values if 'VmRSS' in values else None


def find_player(home):
    """Our player (/proc comm OniMoono) whose environment names the run home HOME."""
    import os

    for entry in os.listdir('/proc'):
        if not entry.isdigit():
            continue
        try:
            with open(f'/proc/{entry}/comm') as f:
                if f.read().strip() != 'OniMoono':
                    continue
            with open(f'/proc/{entry}/environ', 'rb') as f:
                if f'ONI_RUN_HOME={home}'.encode() in f.read().split(b'\0'):
                    return int(entry)
        except OSError:
            continue
    return None


def memwatch(out, home, launcher, settle=None, rise_mb=None, hang_min=None, scripts=None):
    """qa-tools.py memwatch OUT HOME LAUNCHER_PID: watches the player of run home HOME (started by
    LAUNCHER_PID) until MemWatch says to stop, the launcher ends, it hangs (no PERF line for
    QA_HANG_MIN minutes: diagnostics into OUT/hang) or it passes 24 GB; stops it; writes
    OUT/rss.txt (time, VmRSS, RssAnon, RssFile, frame), OUT/peak-rss (kB) and OUT/memory.json."""
    import json, os, signal, subprocess, time

    settle = int(settle or os.environ.get('QA_MEMORY_SETTLE_FRAMES') or 1200)
    rise_mb = float(rise_mb or os.environ.get('QA_MEMORY_RISE_MB') or 64)
    hang = 60 * float(hang_min or os.environ.get('QA_HANG_MIN') or 5)
    watch = MemWatch(settle, int(rise_mb * 1024))
    log_path, launcher = os.path.join(out, 'stdout.txt'), int(launcher)
    offset, progress, perfs, reason, player = 0, time.time(), None, None, None
    with open(os.path.join(out, 'rss.txt'), 'w') as samples:
        while os.path.exists(f'/proc/{launcher}') and not reason:
            now = time.time()
            try:
                with open(log_path, 'rb') as f:
                    f.seek(offset)
                    chunk = f.read()
                cut = chunk.rfind(b'\n') + 1
                offset += cut
                for text in chunk[:cut].decode('utf-8', 'replace').splitlines():
                    watch.line(text, now)
            except OSError:
                pass
            if watch.perf != perfs:
                perfs, progress = watch.perf, now
            player = find_player(home) or player
            values = proc_status(player) if player else None
            if values:
                reason = watch.sample(values['VmRSS'], values.get('RssAnon', 0), values.get('RssFile', 0), now)
                samples.write(f'{int(now)} {values["VmRSS"]} {values.get("RssAnon", 0)} {values.get("RssFile", 0)} {watch.frame}\n')
                samples.flush()
                if values['VmRSS'] > 24 * 1024 * 1024:
                    reason = 'runaway: more than 24 GB'
                elif now - progress >= hang and not os.path.isdir(os.path.join(out, 'hang')):
                    reason = f'hung: no PERF line for {hang / 60:g} min'
                    subprocess.run(
                        [os.path.join(scripts or os.path.dirname(os.path.abspath(__file__)), 'hang-dump.sh'), str(player), os.path.join(out, 'hang'), log_path],
                        capture_output=True,
                    )
            time.sleep(1)
    if reason and player:
        for sig, wait in ((signal.SIGTERM, 10), (signal.SIGKILL, 5)):
            try:
                os.kill(player, sig)
            except OSError:
                break
            deadline = time.time() + wait
            while os.path.exists(f'/proc/{player}') and time.time() < deadline:
                time.sleep(0.2)
    result = dict(watch.summary(), stopped=reason or 'the player ended', settle_frames=settle, rise_mb=rise_mb)
    with open(os.path.join(out, 'memory.json'), 'w') as f:
        json.dump(result, f, indent=1)
    with open(os.path.join(out, 'peak-rss'), 'w') as f:
        f.write(f'{result["peak_kb"]}\n')
    mb = lambda kb: f'{kb / 1024:,.0f} MB'
    print(
        f'peak {mb(result["peak_kb"])} at about f{result["peak_frame"]} (RssAnon {mb(result["rss_anon_kb"])}, '
        f'RssFile {mb(result["rss_file_kb"])}); autosave at '
        + (f'about f{result["autosave_frame"]}' if result['autosave_frame'] is not None else 'none seen')
        + f'; stopped at about f{result["last_frame"]}: {result["stopped"]}'
    )


if __name__ == '__main__':
    cmd, *args = sys.argv[1:]
    {'signatures': signatures, 'new-signatures': new_signatures, 'image': image, 'verify': verify, 'perf': perf, 'memwatch': memwatch}[cmd](*args)
