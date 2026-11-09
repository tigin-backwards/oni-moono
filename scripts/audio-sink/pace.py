#!/usr/bin/env python3
# Reads PCM from stdin at real-time rate (48 kHz stereo s16 = 192000 B/s) and stores it, so a
# producer writing into this pipe is paced like a sound card.
import sys, time, os

out = open(sys.argv[1], 'wb')
rate = 192000
start = time.monotonic()
total = 0
fd = sys.stdin.fileno()
while True:
    due = start + total / rate
    now = time.monotonic()
    if due > now:
        time.sleep(due - now)
    b = os.read(fd, 9600)
    if not b:
        break
    out.write(b)
    total += len(b)
    if total > rate * 600:
        break
