/* Unit tests of fmod_heap.c, FMOD's quarantine allocator: alloc, free and realloc, alignment,
 * the quarantine's eviction, what it detects (a write after free, a double free, an overrun, a
 * damaged header, a pointer not its own) and several threads at once. Built and run by build.cmd
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
    /* 48-byte blocks (FMOD's handle arrays: 8 + 0x18 + header): 1 MB holds about 13,000. */
    void *blocks[40000];
    moono_fmod_heap_counters s;
    for (int i = 0; i < 40000; i++) blocks[i] = moono_fmod_alloc(0x20, 0, "test");
    for (int i = 0; i < 40000; i++) moono_fmod_free(blocks[i], 0, "test");
    s = stats();
    CHECK(s.quarantined_bytes <= LIMIT && s.quarantined_bytes > LIMIT - 1000, "quarantine holds %llu of %u bytes", (unsigned long long)s.quarantined_bytes, LIMIT);
    CHECK(s.quarantined_blocks > 10000 && s.quarantined_blocks < 40000, "%llu blocks quarantined", (unsigned long long)s.quarantined_blocks);
    /* The newest are still in it (their fill intact), the oldest out: FIFO. */
    CHECK(((unsigned char *)blocks[39999])[0] == 0xDB, "the last freed is filled");
    unsigned long long held = s.quarantined_blocks;
    /* Blocks over a quarantine's quarter skip it. */
    void *large = moono_fmod_alloc(LIMIT / 2, 0, "test");
    moono_fmod_free(large, 0, "test");
    s = stats();
    CHECK(s.quarantined_blocks == held, "a block over a quarter of the quarantine went into it");
    moono_fmod_heap_flush();
    s = stats();
    CHECK(s.quarantined_blocks == 0 && s.quarantined_bytes == 0, "flushed");
    CHECK(drain(seen) == 0, "events from correct use");
    printf("quarantine: 40,000 blocks freed, %llu held (%u bytes at most), oldest out first\n", held, LIMIT);
}

static void write_after_free(void)
{
    unsigned char *p = (unsigned char *)moono_fmod_alloc(0x20, 0x200000, "test");
    moono_fmod_free(p, 0, "test");
    p[5] = 0;           /* FMOD's stale write: one byte of a freed block */
    p[-8] = 0x11;       /* and one into its header */
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
        CHECK(e->first_offset == -8 && e->last_offset == 5, "offsets %lld..%lld", (long long)e->first_offset, (long long)e->last_offset);
        CHECK(e->offsets[1] == 5 && e->values[1] == 0 && e->expected[1] == 0xDB, "byte 5: %02x, expected %02x", e->values[1], e->expected[1]);
        CHECK(e->values[0] == 0x11, "header byte %02x", e->values[0]);
        CHECK(e->address == (uint64_t)(uintptr_t)p && e->time_ms > 1700000000000LL && e->age_ms >= 0, "address, time and age");
    }
    printf("write after free: found as the block leaves, with offsets and values\n");
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
    /* An overrun: the canary after the end. */
    unsigned char *o = (unsigned char *)moono_fmod_alloc(24, 0, "test");
    o[24] ^= 0xFF;
    o[26] ^= 0xFF;
    moono_fmod_free(o, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_OVERRUN && seen[0].first_offset == 24 && seen[0].last_offset == 26 && seen[0].changed == 2, "overrun: %d events", n);
    /* A damaged header: the size written over. Leaked, not trusted. */
    unsigned char *d = (unsigned char *)moono_fmod_alloc(40, 0, "test");
    d[-24] = 0x77;
    moono_fmod_free(d, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_HEADER_DAMAGED, "damaged header: %d events", n);
    /* A pointer it never returned. */
    static unsigned char foreign[128];
    moono_fmod_free(foreign + 64, 0, "test");
    n = drain(seen);
    CHECK(n == 1 && seen[0].kind == FH_NOT_OURS && seen[0].first_offset == -32, "not ours: %d events", n);
    /* More events than the ring holds: counted as dropped, never blocking. */
    moono_fmod_heap_counters before = stats();
    for (int i = 0; i < 300; i++) moono_fmod_free(foreign + 64, 0, "test");
    moono_fmod_heap_counters after = stats();
    n = drain(seen);
    CHECK(n == 256 && after.events_dropped - before.events_dropped == 44, "ring: %d taken, %llu dropped", n, (unsigned long long)(after.events_dropped - before.events_dropped));
    moono_fmod_heap_flush();
    CHECK(drain(seen) == 0, "no more");
    printf("misuse: double free, overrun, damaged header, foreign pointer, ring overflow\n");
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
    /* (misuse leaked the block whose header it damaged) */
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
    misuse();
    threads();
    if (argc > 1 && strcmp(argv[1], "--bench") == 0) bench();
    printf(failures ? "fmod_heap: %d failed\n" : "fmod_heap: all passed\n", failures);
    return failures ? 1 : 0;
}
