#!/usr/bin/env python3
"""A model window manager for e2e specs (`window_manager: true`), standing in for a desktop whose
panel takes the bottom of the screen: KDE Plasma's kept an undecorated monitor-sized window above
it (issue #108). It does three things a real EWMH window manager does, and nothing else:

- Advertises EWMH (_NET_SUPPORTING_WM_CHECK, _NET_SUPPORTED with _NET_WM_STATE_FULLSCREEN, and a
  _NET_WORKAREA without the panel), so GLFW and SDL ask it for fullscreen instead of bypassing it
  with an override-redirect window.
- Keeps every undecorated window (_MOTIF_WM_HINTS with no decorations) inside the work area, as
  that panel did, unless the window is in _NET_WM_STATE_FULLSCREEN.
- Gives a window in _NET_WM_STATE_FULLSCREEN the whole screen, panel included.

Decorated windows are mapped and configured as asked: it draws no frames and reparents nothing,
so screen and window coordinates stay those of a server with no window manager.

    e2e-wm.py DISPLAY LOG [PANEL_HEIGHT]

Writes `ready` to LOG once it manages the screen, then one line per decision. Exits when the X
server goes (Xlib's I/O error handler).
"""

import ctypes
import ctypes.util
import sys

display_name, log_path = sys.argv[1], sys.argv[2]
panel = int(sys.argv[3]) if len(sys.argv) > 3 else 76
log = open(log_path, 'a', buffering=1)

x11 = ctypes.CDLL(ctypes.util.find_library('X11') or 'libX11.so.6')
ulong, c_int, c_long, c_void_p = ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.c_void_p
x11.XOpenDisplay.restype = c_void_p
x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
x11.XDefaultRootWindow.restype = ulong
x11.XDefaultRootWindow.argtypes = [c_void_p]
x11.XInternAtom.restype = ulong
x11.XInternAtom.argtypes = [c_void_p, ctypes.c_char_p, c_int]
x11.XCreateSimpleWindow.restype = ulong
x11.XCreateSimpleWindow.argtypes = [c_void_p, ulong, c_int, c_int, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint, ulong, ulong]
x11.XChangeProperty.argtypes = [c_void_p, ulong, ulong, ulong, c_int, c_int, c_void_p, c_int]
x11.XSelectInput.argtypes = [c_void_p, ulong, c_long]
x11.XNextEvent.argtypes = [c_void_p, c_void_p]
x11.XMapWindow.argtypes = [c_void_p, ulong]
x11.XRaiseWindow.argtypes = [c_void_p, ulong]
x11.XMoveResizeWindow.argtypes = [c_void_p, ulong, c_int, c_int, ctypes.c_uint, ctypes.c_uint]
x11.XSync.argtypes = [c_void_p, c_int]
x11.XFree.argtypes = [c_void_p]
x11.XDisplayWidth.argtypes = x11.XDisplayHeight.argtypes = [c_void_p, c_int]
x11.XGetGeometry.argtypes = [c_void_p, ulong, ctypes.POINTER(ulong), *[ctypes.POINTER(c_int)] * 2, *[ctypes.POINTER(ctypes.c_uint)] * 4]
x11.XGetWindowProperty.argtypes = [
    c_void_p,
    ulong,
    ulong,
    c_long,
    c_long,
    c_int,
    ulong,
    ctypes.POINTER(ulong),
    ctypes.POINTER(c_int),
    ctypes.POINTER(ulong),
    ctypes.POINTER(ulong),
    ctypes.POINTER(c_void_p),
]

ERROR_HANDLER = ctypes.CFUNCTYPE(c_int, c_void_p, c_void_p)


@ERROR_HANDLER
def on_error(_display, event):
    # A window gone before a request about it reached the server (BadWindow): not fatal.
    code = ctypes.cast(event, ctypes.POINTER(ctypes.c_ubyte))[32]  # XErrorEvent.error_code
    log.write(f'X error {code}\n')
    return 0


x11.XSetErrorHandler(on_error)

dpy = x11.XOpenDisplay(display_name.encode())
if not dpy:
    sys.exit(f'e2e-wm: cannot open display {display_name}')
root = x11.XDefaultRootWindow(dpy)
width, height = x11.XDisplayWidth(dpy, 0), x11.XDisplayHeight(dpy, 0)
work = (0, 0, width, height - panel)


def atom(name):
    return x11.XInternAtom(dpy, name.encode(), 0)


XA_ATOM, XA_CARDINAL, XA_WINDOW = 4, 6, 33
NET_WM_STATE, FULLSCREEN, MOTIF_HINTS = atom('_NET_WM_STATE'), atom('_NET_WM_STATE_FULLSCREEN'), atom('_MOTIF_WM_HINTS')


def set_longs(window, prop, kind, values):
    data = (c_long * len(values))(*values)
    x11.XChangeProperty(dpy, window, prop, kind, 32, 0, data, len(values))  # PropModeReplace


def longs(window, prop):
    kind, fmt, count, after, data = ulong(), c_int(), ulong(), ulong(), c_void_p()
    if x11.XGetWindowProperty(dpy, window, prop, 0, 64, 0, 0, *(ctypes.byref(v) for v in (kind, fmt, count, after, data))) != 0 or not data.value:
        return []
    values = list(ctypes.cast(data, ctypes.POINTER(c_long))[: count.value]) if fmt.value == 32 else []
    x11.XFree(data)
    return values


# SubstructureRedirect: map and configure requests of top-level windows come here.
x11.XSelectInput(dpy, root, (1 << 20) | (1 << 19))
check = x11.XCreateSimpleWindow(dpy, root, -1, -1, 1, 1, 0, 0, 0)
for window in (root, check):
    set_longs(window, atom('_NET_SUPPORTING_WM_CHECK'), XA_WINDOW, [check])
name = b'e2e-wm'
x11.XChangeProperty(dpy, check, atom('_NET_WM_NAME'), atom('UTF8_STRING'), 8, 0, ctypes.c_char_p(name), len(name))
supported = ['_NET_SUPPORTED', '_NET_SUPPORTING_WM_CHECK', '_NET_WM_STATE', '_NET_WM_STATE_FULLSCREEN', '_NET_WORKAREA', '_NET_WM_NAME']
set_longs(root, atom('_NET_SUPPORTED'), XA_ATOM, [atom(n) for n in supported])
set_longs(root, atom('_NET_WORKAREA'), XA_CARDINAL, list(work))
x11.XSync(dpy, 0)
log.write(f'ready {width}x{height}, work area {work[2]}x{work[3]}\n')

fullscreen = set()


def geometry(window):
    r, x, y, w, h, b, d = ulong(), c_int(), c_int(), ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint(), ctypes.c_uint()
    if not x11.XGetGeometry(dpy, window, ctypes.byref(r), ctypes.byref(x), ctypes.byref(y), ctypes.byref(w), ctypes.byref(h), ctypes.byref(b), ctypes.byref(d)):
        return None
    return [x.value, y.value, w.value, h.value]


def undecorated(window):
    hints = longs(window, MOTIF_HINTS)  # flags, functions, decorations, ...
    return len(hints) >= 3 and hints[0] & 2 and hints[2] == 0


def place(window, x, y, w, h, why):
    if window in fullscreen:
        x, y, w, h = 0, 0, width, height
        rule = 'fullscreen: the whole screen'
    elif undecorated(window):
        w, h = min(w, work[2]), min(h, work[3])
        x = max(work[0], min(x, work[0] + work[2] - w))
        y = max(work[1], min(y, work[1] + work[3] - h))
        rule = 'undecorated: kept in the work area'
    else:
        rule = 'as asked'
    x11.XMoveResizeWindow(dpy, window, x, y, max(w, 1), max(h, 1))
    log.write(f'{why} 0x{window:x}: {w}x{h} at {x},{y} ({rule})\n')


MAP_REQUEST, CONFIGURE_REQUEST, CLIENT_MESSAGE, DESTROY_NOTIFY = 20, 23, 33, 17
event = (c_long * 24)()
ints = ctypes.cast(event, ctypes.POINTER(c_int))
while True:
    x11.XNextEvent(dpy, event)
    kind = ints[0]
    if kind == MAP_REQUEST:
        window = event[5]  # XMapRequestEvent.window
        # A window may ask for fullscreen before it is mapped, in its own _NET_WM_STATE (SDL does).
        if FULLSCREEN in longs(window, NET_WM_STATE):
            fullscreen.add(window)
        if g := geometry(window):
            place(window, *g, 'map')
        x11.XMapWindow(dpy, window)
    elif kind == CONFIGURE_REQUEST:
        window, mask = event[5], event[11]  # XConfigureRequestEvent.window, value_mask
        if g := geometry(window):
            for bit, i in ((1, 0), (2, 1), (4, 2), (8, 3)):  # CWX, CWY, CWWidth, CWHeight
                if mask & bit:
                    g[i] = ints[12 + i]
            place(window, *g, 'configure')
    elif kind == CLIENT_MESSAGE and event[5] == NET_WM_STATE:
        window, action = event[4], event[7]  # XClientMessageEvent.window, data.l[0]
        if FULLSCREEN in (event[8], event[9]):
            on = action == 1 or action == 2 and window not in fullscreen
            (fullscreen.add if on else fullscreen.discard)(window)
            set_longs(window, NET_WM_STATE, XA_ATOM, [FULLSCREEN] if on else [])
            log.write(f'_NET_WM_STATE {"add" if on else "remove"} FULLSCREEN 0x{window:x}\n')
            if on and (g := geometry(window)):
                place(window, *g, 'fullscreen')
                x11.XRaiseWindow(dpy, window)
    elif kind == DESTROY_NOTIFY:
        fullscreen.discard(event[5])
    x11.XSync(dpy, 0)
