/* Unit tests of fmod_heap.c, FMOD's quarantine allocator: alloc, free and realloc, alignment,
 * the quarantine's eviction, what it detects (a write after free or to a free slot, a double
 * free, an overrun or underrun, a pointer not its own), stray writes over live blocks that leave
 * it intact, replays of #78's stale writes and of FMOD's steal repro, and several threads at once. Built and run by build.cmd
 * (MSVC, the allocator as OniMoono.exe has it) and, with any C compiler, by
 * tests/scripts/test_fmod_heap.py (the POSIX side). Prints one line per test and its timings;
 * exits 1 on a failure.
 *   fmod_heap_test [--bench] */

#include "fmod_heap.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
typedef HANDLE thread_t;
static double seconds(void)
{
    LARGE_INTEGER t, f;
    QueryPerformanceCounter(&t);
    QueryPerformanceFrequency(&f);
    return (double)t.QuadPart / (double)f.QuadPart;
}
#else
#include <pthread.h>
#include <time.h>
typedef pthread_t thread_t;
static double seconds(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + t.tv_nsec / 1e9;
}
#endif

#define LIMIT (1u << 20)
/* Each thread's operations (fewer under ThreadSanitizer: -DCHURN_OPS=40000). */
#ifndef CHURN_OPS
#define CHURN_OPS 300000
#endif
static int failures;

#define CHECK(cond, ...)                                                                    \
    do                                                                                      \
    {                                                                                       \
        if (!(cond))                                                                        \
        {                                                                                   \
            failures++;                                                                     \
            printf("  FAIL %s:%d: %s: ", __FILE__, __LINE__, #cond);                        \
            printf(__VA_ARGS__);                                                            \
            printf("\n");                                                                   \
        }                                                                                   \
    } while (0)

static moono_fmod_heap_counters stats(void)
{
    moono_fmod_heap_counters s;
    moono_fmod_heap_stats(&s);
    return s;
}

/* The events since the last call, into `out` (at most 512); how many. */
static int drain(moono_fmod_heap_event *out)
{
    int n = 0, got;
    while (n < 512 && (got = moono_fmod_heap_events(out + n, 512 - n)) > 0) n += got;
    return n;
}

static moono_fmod_heap_event seen[512];

/* Events by kind, drained as often as the tests that make many call it (the ring holds 256). */
static int kinds[8];
static void tally(void)
{
    moono_fmod_heap_event e[64];
    int n;
    while ((n = moono_fmod_heap_events(e, 64)) > 0)
        for (int i = 0; i < n; i++) kinds[e[i].kind < 8 ? e[i].kind : 0]++;
}
static void tally_reset(void)
{
    tally();
    memset(kinds, 0, sizeof kinds);
}

static void basics(void)
{
    for (unsigned size = 0; size < 4200; size += size < 64 ? 1 : 37)
    {
        unsigned char *p = (unsigned char *)moono_fmod_alloc(size, 0, "test");
        CHECK(p != NULL, "alloc %u", size);
        if (!p) continue;
        CHECK(((uintptr_t)p & 15) == 0, "alloc %u at %p: not 16-byte aligned", size, (void *)p);
        memset(p, 0x5A, size);
        moono_fmod_free(p, 0, "test");
    }
    unsigned char *big = (unsigned char *)moono_fmod_alloc(3u << 20, 4, "test");
    CHECK(big && ((uintptr_t)big & 15) == 0, "3 MB block");
    if (big)
    {
        memset(big, 1, 3u << 20);
        moono_fmod_free(big, 4, "test");
    }
    moono_fmod_free(NULL, 0, "test");
    moono_fmod_heap_flush();
    moono_fmod_heap_counters s = stats();
    CHECK(s.live_blocks == 0 && s.live_bytes == 0, "live after freeing all: %llu blocks, %llu bytes", (unsigned long long)s.live_blocks, (unsigned long long)s.live_bytes);
    CHECK(s.quarantined_blocks == 0 && s.quarantined_bytes == 0, "quarantined after a flush: %llu", (unsigned long long)s.quarantined_blocks);
    CHECK(drain(seen) == 0, "events from correct use");
    printf("basics: alloc and free of sizes 0-4200 and 3 MB, 16-byte aligned\n");
}

static void reallocs(void)
{
    unsigned char *p = (unsigned char *)moono_fmod_realloc(NULL, 100, 0, "test");
    CHECK(p && ((uintptr_t)p & 15) == 0, "realloc(NULL) allocates");
    for (int i = 0; i < 100; i++) p[i] = (unsigned char)i;
    unsigned char *q = (unsigned char *)moono_fmod_realloc(p, 5000, 0, "test");
    CHECK(q && ((uintptr_t)q & 15) == 0, "grow");
    int same = 1;
    for (int i = 0; i < 100; i++) same &= q[i] == (unsigned char)i;
    CHECK(same, "grow keeps the contents");
    memset(q + 100, 7, 4900);
    unsigned char *r = (unsigned char *)moono_fmod_realloc(q, 40, 0, "test");
    CHECK(r != NULL, "shrink");
    same = 1;
    for (int i = 0; i < 40; i++) same &= r[i] == (unsigned char)i;
    CHECK(same, "shrink keeps the first bytes");
    unsigned char *z = (unsigned char *)moono_fmod_realloc(r, 0, 0, "test");
    CHECK(z != NULL && ((uintptr_t)z & 15) == 0, "realloc to 0 gives an empty block");
    moono_fmod_free(z, 0, "test");
    moono_fmod_heap_flush();
    moono_fmod_heap_counters s = stats();
    CHECK(s.live_blocks == 0, "live after realloc and free: %llu", (unsigned long long)s.live_blocks);
    CHECK(drain(seen) == 0, "events from correct use");
    printf("realloc: from NULL, grow, shrink, to 0; contents kept\n");
}

static void quarantine(void)
{
    /* 0x20-byte blocks (FMOD's handle arrays): 48-byte slots with their canary; with no large
     * block quarantined the small ones have all of 1 MB, about 21,800. */
    static void *blocks[40000];
    moono_fmod_heap_counters s;
    for (int i = 0; i < 40000; i++) blocks[i] = moono_fmod_alloc(0x20, 0, "test");
    for (int i = 0; i < 40000; i++) moono_fmod_free(blocks[i], 0, "test");
    s = stats();
    CHECK(s.small_quarantine_limit == LIMIT, "small share %llu", (unsigned long long)s.small_quarantine_limit);
    CHECK(s.small_quarantined_bytes <= LIMIT && s.small_quarantined_bytes > LIMIT - 48, "quarantine holds %llu of %u bytes", (unsigned long long)s.small_quarantined_bytes, LIMIT);
    CHECK(s.quarantined_blocks > 21000 && s.quarantined_blocks < 22000, "%llu blocks quarantined", (unsigned long long)s.quarantined_blocks);
    CHECK(s.small_frees >= 40000 && s.slab_bytes > 0 && s.side_bytes > 0 && s.held_bytes >= s.slab_bytes + s.side_bytes, "counters");
    /* The newest are still in it (their fill intact), the oldest out: FIFO. */
    CHECK(((unsigned char *)blocks[39999])[0] == 0xDB, "the last freed is filled");
    unsigned long long held = s.quarantined_blocks;
    /* Large blocks have a quarter at most; one over half of that skips it, into the cache of
     * large blocks (up to 2 MB) or, larger, its pages unmapped at once. */
    void *large = moono_fmod_alloc(LIMIT / 2, 0, "test");
    moono_fmod_heap_counters before = stats();
    moono_fmod_free(large, 0, "test");
    s = stats();
    CHECK(s.quarantined_blocks == held, "a block over a quarter of the quarantine went into it");
    CHECK(s.large_bytes == before.large_bytes, "a 512 KB block is cached");
    CHECK(moono_fmod_alloc(LIMIT / 2 - 4, 0, "test") == large, "and reused for the same pages");
    moono_fmod_free(large, 0, "test");
    large = moono_fmod_alloc(3u << 20, 0, "test");
    before = stats();
    moono_fmod_free(large, 0, "test");
    s = stats();
    CHECK(before.large_bytes - s.large_bytes >= 3u << 20, "a 3 MB block's pages were kept");
    void *mid = moono_fmod_alloc(100000, 0, "test");
    moono_fmod_free(mid, 0, "test");
    s = stats();
    CHECK(s.quarantined_bytes - s.small_quarantined_bytes == 102400, "a 100 KB block quarantined in its pages: %llu", (unsigned long long)(s.quarantined_bytes - s.small_quarantined_bytes));
    CHECK(s.small_quarantine_limit == LIMIT - 102400 && s.quarantined_bytes <= LIMIT, "small blocks make room: %llu", (unsigned long long)s.small_quarantine_limit);
    /* A burst of large frees (sample data being loaded) takes a quarter at most: the small
     * blocks keep three quarters. */
    for (int i = 0; i < 30; i++) moono_fmod_free(moono_fmod_alloc(100000, 4, "test"), 4, "test");
    s = stats();
    CHECK(s.quarantined_bytes - s.small_quarantined_bytes <= LIMIT / 4 && s.small_quarantined_bytes >= LIMIT - LIMIT / 4 - 48 && s.quarantined_bytes <= LIMIT,
          "after a burst: large %llu, small %llu", (unsigned long long)(s.quarantined_bytes - s.small_quarantined_bytes), (unsigned long long)s.small_quarantined_bytes);
    moono_fmod_heap_flush();
    s = stats();
    CHECK(s.quarantined_blocks == 0 && s.quarantined_bytes == 0, "flushed");
    CHECK(s.large_bytes == 0, "large pages after a flush: %llu", (unsigned long long)s.large_bytes);
    CHECK(drain(seen) == 0, "events from correct use");
    printf("quarantine: 40,000 blocks freed, %llu held (%u bytes at most), oldest out first\n", held, LIMIT);
}

static void write_after_free(void)
{
    unsigned char *p = (unsigned char *)moono_fmod_alloc(0x20, 0x200000, "test");
    moono_fmod_free(p, 0, "test");
    p[5] = 0;  /* FMOD's stale write: one byte of a freed block */
    p[34] = 1; /* and one in its slot after its end (its canary while it was live) */
    CHECK(drain(seen) == 0, "nothing found before the block leaves");
    moono_fmod_heap_flush();
    int n = drain(seen);
    CHECK(n == 1, "%d events", n);
    if (n == 1)
    {
        moono_fmod_heap_event *e = &seen[0];
        CHECK(e->kind == FH_WRITE_AFTER_FREE, "kind %u", e->kind);
        CHECK(e->size == 0x20 && e->type == 0x200000, "size %u type %#x", e->size, e->type);
        CHECK(e->changed == 2 && e->count == 2, "changed %u", e->changed);
        CHECK(e->first_offset == 5 && e->last_offset == 34, "offsets %lld..%lld", (long long)e->first_offset, (long long)e->last_offset);
        CHECK(e->offsets[0] == 5 && e->values[0] == 0 && e->expected[0] == 0xDB, "byte 5: %02x, expected %02x", e->values[0], e->expected[0]);
        CHECK(e->values[1] == 1 && e->expected[1] == 0xDB, "byte 34 %02x for %02x", e->values[1], e->expected[1]);
        CHECK(e->address == (uint64_t)(uintptr_t)p && e->time_ms > 1700000000000LL && e->age_ms >= 0, "address, time and age");
    }
    /* The slot is free again, its fill restored: reused without an event. */
    unsigned char *again = (unsigned char *)moono_fmod_alloc(0x20, 0, "test");
    CHECK(again == p, "the slot is reused first (%p, %p)", (void *)again, (void *)p);
    moono_fmod_free(again, 0, "test");
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0, "its fill was restored");
    /* A large block's too. */
    unsigned char *big = (unsigned char *)moono_fmod_alloc(40000, 0, "test");
    moono_fmod_free(big, 0, "test");
    big[39999] = 0x11;
    moono_fmod_heap_flush();
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_WRITE_AFTER_FREE && seen[0].first_offset == 39999 && seen[0].size == 40000, "large: %d events", n);
    printf("write after free: found as the block leaves, with offsets and values\n");
}

/* A late write after the quarantine, to a slot nobody has: found when the slot is reused. */
static void write_to_free(void)
{
    unsigned char *p = (unsigned char *)moono_fmod_alloc(200, 0, "test");
    moono_fmod_free(p, 0, "test");
    moono_fmod_heap_flush();
    p[7] = 0x42;
    CHECK(drain(seen) == 0, "nothing found before reuse");
    unsigned char *q = (unsigned char *)moono_fmod_alloc(190, 0, "test");
    int n = drain(seen);
    CHECK(q == p, "the same slot");
    CHECK(n == 1 && seen[0].kind == FH_WRITE_TO_FREE && seen[0].first_offset == 7 && seen[0].values[0] == 0x42 && seen[0].age_ms == -1 && seen[0].size == 208,
          "%d events, kind %u size %u", n, n ? seen[0].kind : 0, n ? seen[0].size : 0);
    moono_fmod_free(q, 0, "test");
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0, "no more");
    printf("write to a free slot: found when it is reused\n");
}

static void misuse(void)
{
    /* A double free: an event, the block stays quarantined once. */
    unsigned char *p = (unsigned char *)moono_fmod_alloc(64, 0, "test");
    moono_fmod_free(p, 0, "test");
    moono_fmod_free(p, 0, "test");
    CHECK(moono_fmod_realloc(p, 100, 0, "test") == NULL, "realloc of a freed block");
    int n = drain(seen);
    CHECK(n == 2 && seen[0].kind == FH_DOUBLE_FREE && seen[1].kind == FH_DOUBLE_FREE && seen[0].size == 64, "%d events, kind %u", n, n ? seen[0].kind : 0);
    /* Of a freed large block too. */
    unsigned char *big = (unsigned char *)moono_fmod_alloc(50000, 0, "test");
    moono_fmod_free(big, 0, "test");
    moono_fmod_free(big, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_DOUBLE_FREE && seen[0].size == 50000, "large double free: %d events", n);
    /* An overrun: the canary after the end, to the slot's end. */
    unsigned char *o = (unsigned char *)moono_fmod_alloc(24, 0, "test");
    o[24] ^= 0xFF;
    o[26] ^= 0xFF;
    moono_fmod_free(o, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_OVERRUN && seen[0].first_offset == 24 && seen[0].last_offset == 26 && seen[0].changed == 2, "overrun: %d events", n);
    /* An underrun: before a block's start is the slot before it (here its canary), found when
     * that block is freed. */
    unsigned char *a = NULL, *b = NULL, *pair[16];
    for (int i = 0; i < 16; i++) pair[i] = (unsigned char *)moono_fmod_alloc(1000, 0, "test");
    for (int i = 0; i < 16; i++)
        for (int j = 0; j < 16; j++)
            if (pair[j] == pair[i] + 1024) a = pair[i], b = pair[j];
    CHECK(a && b, "neighbours");
    for (int i = 0; i < 16; i++)
        if (pair[i] != a && pair[i] != b) moono_fmod_free(pair[i], 0, "test");
    drain(seen);
    b[-3] = 0x77;
    moono_fmod_free(b, 0, "test");
    CHECK(drain(seen) == 0, "b itself is whole");
    moono_fmod_free(a, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_OVERRUN && seen[0].address == (uint64_t)(uintptr_t)a && seen[0].first_offset == 1021, "underrun as the neighbour's overrun: %d events", n);
    /* Pointers it never returned: elsewhere, inside one of its blocks, in a slab not carved. */
    static unsigned char foreign[128];
    unsigned char *inside = (unsigned char *)moono_fmod_alloc(100, 0, "test");
    moono_fmod_free(foreign + 64, 0, "test");
    moono_fmod_free(inside + 16, 0, "test");
    moono_fmod_free(big + 4096, 0, "test");
    n = drain(seen);
    CHECK(n == 3 && seen[0].kind == FH_NOT_OURS && seen[1].kind == FH_NOT_OURS && seen[2].kind == FH_NOT_OURS, "not ours: %d events", n);
    moono_fmod_free(inside, 0, "test");
    /* More events than the ring holds: counted as dropped, never blocking. */
    moono_fmod_heap_counters before = stats();
    for (int i = 0; i < 300; i++) moono_fmod_free(foreign + 64, 0, "test");
    moono_fmod_heap_counters after = stats();
    n = drain(seen);
    CHECK(n == 256 && after.events_dropped - before.events_dropped == 44, "ring: %d taken, %llu dropped", n, (unsigned long long)(after.events_dropped - before.events_dropped));
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0, "no more");
    printf("misuse: double free, overrun, underrun, foreign pointers, ring overflow\n");
}

/* Stray writes all over live blocks (their data, their canaries, the slack after a slab's last
 * slot): reported when the blocks are freed, and the allocator carries on as before. */
static void stray_writes(void)
{
    enum { N = 2000 };
    static unsigned char *live[N];
    unsigned seed = 12345;
    moono_fmod_heap_counters before = stats();
    tally_reset();
    for (int i = 0; i < N; i++) live[i] = (unsigned char *)moono_fmod_alloc(1 + (unsigned)(i * 37) % 900, (unsigned)i & 1, "test");
    /* 100,000 random bytes over the slabs these live in (each 64 KB). */
    for (int i = 0; i < 100000; i++)
    {
        seed = seed * 1103515245u + 12345u;
        unsigned char *slab = (unsigned char *)((uintptr_t)live[(seed >> 8) % N] & ~(uintptr_t)0xFFFF);
        seed = seed * 1103515245u + 12345u;
        slab[(seed >> 4) & 0xFFFF] = (unsigned char)(seed >> 24);
    }
    for (int i = 0; i < N; i++)
    {
        moono_fmod_free(live[i], 0, "test");
        if (i % 100 == 0) tally();
    }
    moono_fmod_heap_flush();
    tally();
    int overruns = kinds[FH_OVERRUN], other = kinds[FH_WRITE_AFTER_FREE] + kinds[FH_DOUBLE_FREE] + kinds[FH_NOT_OURS] + kinds[FH_WRITE_TO_FREE];
    moono_fmod_heap_counters s = stats();
    CHECK(other == 0, "%d events other than overruns (%d after free, %d to free)", other, kinds[FH_WRITE_AFTER_FREE], kinds[FH_WRITE_TO_FREE]);
    CHECK(overruns > 100, "%d overruns found", overruns);
    CHECK(s.live_blocks == before.live_blocks && s.live_bytes == before.live_bytes, "live after: %llu", (unsigned long long)s.live_blocks);
    /* Each block came back: the same sizes again get blocks, all distinct and whole. */
    for (int i = 0; i < N; i++)
    {
        unsigned size = 1 + (unsigned)(i * 37) % 900;
        live[i] = (unsigned char *)moono_fmod_alloc(size, 0, "test");
        memset(live[i], i & 0xFF, size);
        if (i % 100 == 0) tally(); /* the stray bytes in free slots, found as they are reused */
    }
    int bad = 0;
    for (int i = 0; i < N; i++)
    {
        unsigned size = 1 + (unsigned)(i * 37) % 900;
        for (unsigned j = 0; j < size; j++) bad += live[i][j] != (unsigned char)(i & 0xFF);
        moono_fmod_free(live[i], 0, "test");
    }
    CHECK(bad == 0, "%d bytes overlapped", bad);
    moono_fmod_heap_flush();
    tally();
    CHECK(kinds[FH_WRITE_AFTER_FREE] + kinds[FH_DOUBLE_FREE] + kinds[FH_NOT_OURS] == 0, "events of other kinds");
    printf("stray writes: 100,000 random bytes over live slabs, %d overruns reported, allocator intact\n", overruns);
}

/* #78: a stale EventInstance::stop on an instance already freed adds 1 to the dword at +0x84 and
 * zeroes the byte at +0xBD (57 bytes on). The same writes while the instance is quarantined,
 * after its quarantine, once its slot has a new instance, and once its slab holds another size:
 * each is logged or changes only FMOD's data, and the process carries on. */
static void stale_stop(unsigned char *instance)
{
    uint32_t refs;
    memcpy(&refs, instance + 0x84, 4);
    refs++;
    memcpy(instance + 0x84, &refs, 4);
    instance[0xBD] = 0;
}

static void replay_78(void)
{
    enum { SIZE = 0x1F8, N = 500 };
    static unsigned char *inst[N];
    int n;
    /* Quarantined: found as it leaves, as in #78's log (+4 dc for db, +61 00 for db). */
    unsigned char *p = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    moono_fmod_free(p, 0, "test");
    stale_stop(p);
    moono_fmod_heap_flush();
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_WRITE_AFTER_FREE && seen[0].changed == 2 && seen[0].first_offset == 0x84 && seen[0].values[0] == 0xDC && seen[0].last_offset == 0xBD && seen[0].values[1] == 0,
          "quarantined: %d events", n);
    /* After its quarantine: found when the slot is reused. */
    stale_stop(p);
    unsigned char *q = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    n = drain(seen);
    CHECK(q == p && n == 1 && seen[0].kind == FH_WRITE_TO_FREE && seen[0].changed == 2, "free slot: %d events", n);
    /* The slot reused by another instance: its data changes (FMOD's), nothing else. */
    memset(q, 0x11, SIZE);
    stale_stop(p);
    CHECK(q[0x84] == 0x12 && q[0xBD] == 0, "the new instance took the write");
    moono_fmod_free(q, 0, "test");
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0, "within its data: nothing to find");
    /* Many instances freed with their slabs emptied (into the pool), carved again for another
     * size; then every stale stop, a churn, and all of it freed. */
    for (int i = 0; i < N; i++) inst[i] = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    for (int i = 0; i < N; i++) moono_fmod_free(inst[i], 0, "test");
    moono_fmod_heap_flush();
    static unsigned char *other[4000];
    for (int i = 0; i < 4000; i++) other[i] = (unsigned char *)moono_fmod_alloc(24 + (unsigned)i % 100, 0, "test");
    tally_reset();
    uint64_t dropped = stats().events_dropped;
    for (int i = 0; i < N; i++) stale_stop(inst[i]);
    for (int i = 0; i < 4000; i++)
    {
        moono_fmod_free(other[i], 0, "test");
        if (i % 100 == 0) tally();
    }
    moono_fmod_heap_flush();
    /* Each instance's bytes are now in a live, free or quarantined slot of another size: what
     * changed outside the blocks' data is found, the rest was FMOD's data. */
    for (int i = 0; i < 2000; i++)
    {
        moono_fmod_free(moono_fmod_alloc(SIZE, 0, "test"), 0, "test");
        if (i % 100 == 0) tally();
    }
    for (int i = 0; i < 4000; i++)
    {
        moono_fmod_free(moono_fmod_alloc(24 + (unsigned)i % 100, 0, "test"), 0, "test");
        if (i % 100 == 0) tally();
    }
    moono_fmod_heap_flush();
    tally();
    int found = kinds[FH_OVERRUN] + kinds[FH_WRITE_TO_FREE] + kinds[FH_WRITE_AFTER_FREE], wrong = kinds[FH_DOUBLE_FREE] + kinds[FH_NOT_OURS];
    CHECK(wrong == 0, "%d events of another kind", wrong);
    CHECK(found > 0 && stats().events_dropped == dropped, "%d found", found);
    printf("#78 replay: +0x84 dword inc and +0xBD zero on freed instances (quarantined, free, reused, re-carved), %d logged, no crash\n", found);
}

typedef struct worker
{
    unsigned seed;
    long long ops;
    int bad;
} worker;

#define SLOTS 512

static unsigned next(unsigned *s)
{
    *s ^= *s << 13;
    *s ^= *s >> 17;
    *s ^= *s << 5;
    return *s;
}

static
#ifdef _WIN32
    DWORD WINAPI
#else
    void *
#endif
    churn(void *arg)
{
    worker *w = (worker *)arg;
    unsigned char *slot[SLOTS] = {0};
    unsigned size[SLOTS] = {0};
    unsigned char mark = (unsigned char)(w->seed & 0xFF);
    for (long long i = 0; i < w->ops; i++)
    {
        unsigned r = next(&w->seed), k = r % SLOTS;
        if (slot[k])
        {
            for (unsigned j = 0; j < size[k]; j++)
                if (slot[k][j] != (unsigned char)(mark + j)) w->bad++;
            if (r & 0x100)
            {
                unsigned grow = 1 + next(&w->seed) % 700;
                unsigned char *q = (unsigned char *)moono_fmod_realloc(slot[k], grow, 0, "test");
                if (!q) { w->bad++; continue; }
                for (unsigned j = 0; j < grow; j++) q[j] = (unsigned char)(mark + j);
                slot[k] = q;
                size[k] = grow;
                continue;
            }
            moono_fmod_free(slot[k], 0, "test");
            slot[k] = NULL;
            continue;
        }
        size[k] = (r & 0xF000) == 0 ? 65536 : 1 + next(&w->seed) % 300;
        slot[k] = (unsigned char *)moono_fmod_alloc(size[k], 0, "test");
        if (!slot[k] || ((uintptr_t)slot[k] & 15)) { w->bad++; continue; }
        for (unsigned j = 0; j < size[k]; j++) slot[k][j] = (unsigned char)(mark + j);
    }
    for (int k = 0; k < SLOTS; k++)
        if (slot[k]) moono_fmod_free(slot[k], 0, "test");
#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}

static void threads(void)
{
    enum { N = 8 };
    worker w[N];
    thread_t t[N];
    moono_fmod_heap_counters before = stats();
    double start = seconds();
    for (int i = 0; i < N; i++)
    {
        w[i].seed = 0x9E3779B9u * (unsigned)(i + 1);
        w[i].ops = CHURN_OPS;
        w[i].bad = 0;
#ifdef _WIN32
        t[i] = CreateThread(NULL, 0, churn, &w[i], 0, NULL);
#else
        pthread_create(&t[i], NULL, churn, &w[i]);
#endif
    }
    int bad = 0;
    for (int i = 0; i < N; i++)
    {
#ifdef _WIN32
        WaitForSingleObject(t[i], INFINITE);
        CloseHandle(t[i]);
#else
        pthread_join(t[i], NULL);
#endif
        bad += w[i].bad;
    }
    double took = seconds() - start;
    moono_fmod_heap_flush();
    moono_fmod_heap_counters s = stats();
    CHECK(bad == 0, "%d blocks lost their contents or failed", bad);
    CHECK(s.live_blocks == before.live_blocks && s.live_bytes == before.live_bytes && s.quarantined_bytes == 0, "after: %llu live", (unsigned long long)s.live_blocks);
    int n = drain(seen);
    CHECK(n == 0, "%d events from correct use", n);
    printf("threads: %d threads, %d alloc/realloc/free each with contents checked, in %.2f s\n", N, CHURN_OPS, took);
}

/* What a block costs: alloc and free of FMOD's 0x20-byte blocks, through the quarantine and
 * through the C runtime, single-threaded. */
static void bench(void)
{
    enum { ROUNDS = 2000000 };
    double start = seconds();
    for (int i = 0; i < ROUNDS; i++) moono_fmod_free(moono_fmod_alloc(0x20, 0, "bench"), 0, "bench");
    double ours = seconds() - start;
    start = seconds();
    for (int i = 0; i < ROUNDS; i++)
    {
        void *volatile p = malloc(0x20);
        free(p);
    }
    double crt = seconds() - start;
    moono_fmod_heap_flush();
    printf("bench: 0x20-byte alloc+free %.0f ns (C runtime %.0f ns)\n", ours / ROUNDS * 1e9, crt / ROUNDS * 1e9);
}

/* FMOD's own repro of forum thread 24589: a bus-level steal of a released instance still
 * waiting for its samples destroys the instance in changeStateStopped and then writes 4 zero
 * bytes at +0x4B8 of the freed 1,312-byte EventInstance (a 1,536-byte slot here), at once. */
static void replay_steal(void)
{
    enum { SIZE = 1312, AT = 0x4B8, N = 400 };
    static unsigned char *inst[N];
    int n;
    /* Right after the free: in the quarantine, found as it leaves. */
    unsigned char *p = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    moono_fmod_free(p, 0, "test");
    memset(p + AT, 0, 4);
    moono_fmod_heap_flush();
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_WRITE_AFTER_FREE && seen[0].size == SIZE && seen[0].changed == 4 && seen[0].first_offset == AT && seen[0].last_offset == AT + 3 && seen[0].values[0] == 0,
          "quarantined: %d events", n);
    /* After the quarantine: found on reuse. */
    memset(p + AT, 0, 4);
    unsigned char *q = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    n = drain(seen);
    CHECK(q == p && n == 1 && seen[0].kind == FH_WRITE_TO_FREE && seen[0].first_offset == AT && seen[0].changed == 4, "free slot: %d events", n);
    /* Reused: the new instance's field, nothing else. */
    memset(q, 0x11, SIZE);
    memset(p + AT, 0, 4);
    moono_fmod_free(q, 0, "test");
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0 && q[AT] == 0xDB, "within its data: nothing to find");
    /* Slabs of them emptied and carved again for small blocks, then every stale write. */
    tally_reset();
    uint64_t dropped = stats().events_dropped;
    for (int i = 0; i < N; i++) inst[i] = (unsigned char *)moono_fmod_alloc(SIZE, 0, "test");
    for (int i = 0; i < N; i++) moono_fmod_free(inst[i], 0, "test");
    moono_fmod_heap_flush();
    tally();
    static unsigned char *other[6000];
    for (int i = 0; i < 6000; i++)
    {
        other[i] = (unsigned char *)moono_fmod_alloc(8 + (unsigned)i % 200, 0, "test");
        if (i % 100 == 0) tally(); /* (the earlier tests' late writes, found on reuse) */
    }
    tally_reset();
    for (int i = 0; i < N; i++) memset(inst[i] + AT, 0, 4);
    for (int i = 0; i < 6000; i++)
    {
        moono_fmod_free(other[i], 0, "test");
        if (i % 100 == 0) tally();
    }
    for (int i = 0; i < 6000; i++)
    {
        moono_fmod_free(moono_fmod_alloc(8 + (unsigned)i % 200, 0, "test"), 0, "test");
        moono_fmod_free(moono_fmod_alloc(SIZE, 0, "test"), 0, "test");
        if (i % 100 == 0) tally();
    }
    moono_fmod_heap_flush();
    tally();
    CHECK(kinds[FH_DOUBLE_FREE] + kinds[FH_NOT_OURS] == 0 && stats().events_dropped == dropped, "events of another kind: %d double, %d not ours, %llu dropped", kinds[FH_DOUBLE_FREE], kinds[FH_NOT_OURS], (unsigned long long)(stats().events_dropped - dropped));
    printf("steal replay: 4 zero bytes at +0x4B8 of freed 1,312-byte instances (quarantined, free, reused, re-carved), %d logged, no crash\n",
           kinds[FH_WRITE_AFTER_FREE] + kinds[FH_WRITE_TO_FREE] + kinds[FH_OVERRUN]);
}

int main(int argc, char **argv)
{
    if (!moono_fmod_heap_init(LIMIT))
    {
        printf("FAIL: init\n");
        return 1;
    }
    CHECK(moono_fmod_heap_init(LIMIT) == 1, "a second init keeps the first");
    basics();
    reallocs();
    quarantine();
    write_after_free();
    write_to_free();
    misuse();
    threads();
    /* Last: they leave late writes in free slots. */
    stray_writes();
    replay_78();
    replay_steal();
    if (argc > 1 && strcmp(argv[1], "--bench") == 0) bench();
    printf(failures ? "fmod_heap: %d failed\n" : "fmod_heap: all passed\n", failures);
    return failures ? 1 : 0;
}
