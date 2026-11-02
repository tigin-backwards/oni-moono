"""bridge.py PORT [--probe]: (Windows Python) joins stdin/stdout to 127.0.0.1:PORT, so a WSL
process reaches a Windows-only loopback server. --probe: exit 0 if the port answers, else 1."""
import socket, sys, threading

port = int(sys.argv[1])
try:
    s = socket.create_connection(('127.0.0.1', port), timeout=2)
except OSError:
    sys.exit(1)
if '--probe' in sys.argv:
    s.close()
    sys.exit(0)
s.settimeout(None)
s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
out = sys.stdout.buffer


def down():
    try:
        while True:
            data = s.recv(65536)
            if not data:
                break
            out.write(data)
            out.flush()
    except OSError:
        pass
    finally:
        import os
        os._exit(0)


threading.Thread(target=down, daemon=True).start()
inp = sys.stdin.buffer
try:
    while True:
        data = inp.read1(65536)
        if not data:
            break
        s.sendall(data)
    s.shutdown(socket.SHUT_WR)
except OSError:
    pass
threading.Event().wait(30)
