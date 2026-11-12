"""runq's database (SQLite in WAL mode, one writer: the daemon).

jobs: one row a job, its request and its outcome. holds: every grant a job held (its first, and
one per phase), with start and end, for the owners' service over 24 h and the history."""

import json, os, sqlite3, time

SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    id TEXT UNIQUE,
    owner TEXT, level TEXT, label TEXT, kind TEXT,
    needs TEXT, timeout REAL, expect REAL,
    submit REAL, ord REAL,
    state TEXT,              -- queued, running, done, cancelled, lost
    cwd TEXT, argv TEXT, dir TEXT,
    after TEXT, after_ok INTEGER,
    heartbeat REAL, retry_on_lost INTEGER, keep_home INTEGER, budget_ok TEXT,
    start REAL, end_ REAL, exit INTEGER, reason TEXT,
    lanes TEXT, noisy INTEGER DEFAULT 0, attempt INTEGER DEFAULT 0, boost INTEGER DEFAULT 0,
    unit TEXT, cgroup TEXT, phase TEXT, phase_seq INTEGER DEFAULT 0, quiet_since REAL,
    cpu_s REAL, mem_peak REAL, warnings TEXT
);
CREATE TABLE IF NOT EXISTS holds (
    job TEXT, needs TEXT, start REAL, end_ REAL
);
CREATE INDEX IF NOT EXISTS jobs_state ON jobs(state);
CREATE INDEX IF NOT EXISTS holds_end ON holds(end_);
"""

JSON_FIELDS = ('needs', 'argv', 'after', 'lanes', 'phase', 'warnings')


class Store:
    def __init__(self, path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.db = sqlite3.connect(path, isolation_level=None, check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.execute('PRAGMA synchronous=NORMAL')
        self.db.executescript(SCHEMA)

    def new_id(self):
        row = self.db.execute("SELECT seq FROM sqlite_sequence WHERE name='jobs'").fetchone()
        return f'j{(row[0] if row else 0) + 1:07d}'

    def insert(self, job):
        fields = dict(job)
        for k in JSON_FIELDS:
            if k in fields:
                fields[k] = json.dumps(fields[k])
        if 'end' in fields:
            fields['end_'] = fields.pop('end')
        cols = ', '.join(fields)
        marks = ', '.join('?' for _ in fields)
        self.db.execute(f'INSERT INTO jobs ({cols}) VALUES ({marks})', list(fields.values()))

    def update(self, job_id, **fields):
        for k in JSON_FIELDS:
            if k in fields:
                fields[k] = json.dumps(fields[k])
        if 'end' in fields:
            fields['end_'] = fields.pop('end')
        sets = ', '.join(f'{k} = ?' for k in fields)
        self.db.execute(f'UPDATE jobs SET {sets} WHERE id = ?', [*fields.values(), job_id])

    @staticmethod
    def row(r):
        if r is None:
            return None
        d = dict(r)
        for k in JSON_FIELDS:
            if d.get(k) is not None:
                d[k] = json.loads(d[k])
        d['end'] = d.pop('end_')
        return d

    def get(self, job_id):
        return self.row(self.db.execute('SELECT * FROM jobs WHERE id = ?', (job_id,)).fetchone())

    def by_state(self, *states):
        marks = ', '.join('?' for _ in states)
        return [self.row(r) for r in self.db.execute(f'SELECT * FROM jobs WHERE state IN ({marks}) ORDER BY seq', states)]

    def history(self, owner=None, since=0.0, limit=50):
        q = 'SELECT * FROM jobs WHERE submit >= ?'
        args = [since]
        if owner:
            q += ' AND owner = ?'
            args.append(owner)
        q += ' ORDER BY seq DESC LIMIT ?'
        args.append(limit)
        return [self.row(r) for r in self.db.execute(q, args)]

    def hold_start(self, job_id, needs, start):
        self.db.execute('INSERT INTO holds (job, needs, start, end_) VALUES (?, ?, ?, NULL)', (job_id, json.dumps(needs), start))

    def hold_end(self, job_id, end):
        self.db.execute('UPDATE holds SET end_ = ? WHERE job = ? AND end_ IS NULL', (end, job_id))

    def service(self, now, window=86400.0):
        """{(owner, level): machine-hours} of the holds over the last WINDOW seconds."""
        out = {}
        rows = self.db.execute(
            'SELECT h.start, h.end_, j.owner, j.level FROM holds h JOIN jobs j ON j.id = h.job WHERE h.end_ IS NULL OR h.end_ >= ?', (now - window,)
        )
        for start, end, owner, level in rows:
            s, e = max(start, now - window), min(end if end is not None else now, now)
            if e > s:
                out[(owner, level)] = out.get((owner, level), 0.0) + (e - s) / 3600
        return out

    def median_duration(self, label):
        rows = [
            r[0]
            for r in self.db.execute(
                "SELECT end_ - start FROM jobs WHERE label = ? AND state = 'done' AND exit = 0 AND start IS NOT NULL ORDER BY seq DESC LIMIT 20", (label,)
            )
        ]
        rows = sorted(x for x in rows if x is not None)
        return rows[len(rows) // 2] if rows else None

    def integrity(self):
        return self.db.execute('PRAGMA integrity_check').fetchone()[0]

    def close(self):
        self.db.close()


def now():
    return time.time()
