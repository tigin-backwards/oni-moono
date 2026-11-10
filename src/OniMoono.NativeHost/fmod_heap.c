/* FMOD's allocator with a quarantine: game tweak fmod-quarantine (FmodQuarantine.cs, which gives
 * these three callbacks to FMOD_Memory_Initialize before FMOD Studio starts; docs/TWEAKS.md).
 *
 * Why: issues #72 and #58 are heap corruption (0xC0000374) found in RtlFreeHeap on the "FMOD
 * Studio update thread" while FMOD destroys an event instance. FMOD's staff acknowledged a write
 * to deallocated memory in the stop/steal path of 2.01.18 and later, "which could crash if the
 * memory is reallocated immediately" (qa.fmod.com/t/hard-crash-in-fmodstudiol/24589), with no fix.
 * FMOD's static CRT allocates from the process heap, shared with the runtime, the GL driver and
 * the rest, so its stale write lands in whatever took the block next. Here:
 *
 * - FMOD gets its own heap (HeapCreate on Windows; glibc's malloc elsewhere, already thread-safe),
 *   so a stale write can only reach FMOD's own blocks.
 * - A freed block is not given back at once: it is filled with FILL and kept in a FIFO
 *   quarantine of at most `limit` bytes, so a late write lands in a block nobody uses. When it
 *   leaves, the whole block is checked; a changed byte is an event (FH_WRITE_AFTER_FREE) with
 *   its offsets and values, which the player logs.
 * - Each block has a header (magic, size, type, a check word bound to its address) and an 8-byte
 *   canary after its end: a double free, a pointer not ours, a damaged header or an overrun is an
 *   event too, and never a crash (the block is then leaked rather than trusted).
 *
 * Nothing here calls managed code: FMOD calls in on its mixer, update and loading threads. The
 * events go into a lock-free ring that the main thread drains (moono_fmod_heap_events). Blocks
 * are 16-byte aligned (what HeapAlloc and malloc return on x64, plus a 32-byte header). One lock
 * covers the quarantine's FIFO; blocks are checked and freed outside it. */

#include "fmod_heap.h"
#include <string.h>
#include <stddef.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
typedef volatile LONG64 fh_atomic;
#define fh_add(p, v) InterlockedExchangeAdd64((p), (LONG64)(v))
#define fh_cas(p, expected, desired) (InterlockedCompareExchange64((p), (LONG64)(desired), (LONG64)(expected)) == (LONG64)(expected))
#define fh_load(p) InterlockedCompareExchange64((p), 0, 0)
#define fh_store(p, v) InterlockedExchange64((p), (LONG64)(v))
static HANDLE heap;
static SRWLOCK lock = SRWLOCK_INIT;
static void *raw_alloc(size_t n) { return HeapAlloc(heap, 0, n); }
static void raw_free(void *p) { HeapFree(heap, 0, p); }
static int raw_init(void) { return (heap = HeapCreate(0, 0, 0)) != NULL; }
static void fh_lock(void) { AcquireSRWLockExclusive(&lock); }
static void fh_unlock(void) { ReleaseSRWLockExclusive(&lock); }
static int64_t now_ms(void)
{
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    return (int64_t)((((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime) - 116444736000000000ULL) / 10000;
}
/* For a block's age: cheap, to the tick (10-16 ms). */
static int64_t ticks_ms(void) { return (int64_t)GetTickCount64(); }
#else
#include <stdlib.h>
#include <pthread.h>
#include <time.h>
typedef volatile int64_t fh_atomic;
#define fh_add(p, v) __atomic_fetch_add((p), (int64_t)(v), __ATOMIC_SEQ_CST)
static int fh_cas_(fh_atomic *p, int64_t expected, int64_t desired)
{
    return __atomic_compare_exchange_n(p, &expected, desired, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
}
#define fh_cas(p, expected, desired) fh_cas_((p), (int64_t)(expected), (int64_t)(desired))
#define fh_load(p) __atomic_load_n((p), __ATOMIC_SEQ_CST)
#define fh_store(p, v) __atomic_store_n((p), (int64_t)(v), __ATOMIC_SEQ_CST)
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static void *raw_alloc(size_t n) { return malloc(n); }
static void raw_free(void *p) { free(p); }
static int raw_init(void) { return 1; }
static void fh_lock(void) { pthread_mutex_lock(&lock); }
static void fh_unlock(void) { pthread_mutex_unlock(&lock); }
static int64_t now_ms(void)
{
    struct timespec t;
    clock_gettime(CLOCK_REALTIME, &t);
    return (int64_t)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}
static int64_t ticks_ms(void)
{
    struct timespec t;
#ifdef CLOCK_MONOTONIC_COARSE
    clock_gettime(CLOCK_MONOTONIC_COARSE, &t);
#else
    clock_gettime(CLOCK_MONOTONIC, &t);
#endif
    return (int64_t)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}
#endif

#define MAGIC_LIVE 0x4D4F4F4E4F464D31LL  /* "1MFONOOM" */
#define MAGIC_FREED 0x4D4F4F4E4F464D30LL
#define FILL 0xDB
#define FILL_WORD 0xDBDBDBDBDBDBDBDBULL
#define TAIL 8

typedef struct fh_header
{
    fh_atomic magic;
    uint32_t size;
    uint32_t type;
    uint64_t check;   /* bound to the header's address, size and type (check_of) */
    int64_t freed_ms; /* when it went into quarantine (ticks_ms) */
} fh_header;

typedef char fh_header_is_32[sizeof(fh_header) == 32 ? 1 : -1];

/* A quarantined block, with what its header must still say (kept here, not in the block, so a
 * write into the block cannot change what it is checked against). */
typedef struct fh_entry
{
    fh_header *block;
    uint32_t size, type;
    uint64_t check;
    int64_t freed_ms;
} fh_entry;

static int initialized;
static uint64_t secret;
static uint64_t limit;

static fh_entry *ring;
static size_t ring_capacity, ring_head, ring_count;
static uint64_t ring_bytes;

static fh_atomic live_bytes, live_blocks, peak_live, held_bytes, peak_held;
static fh_atomic allocs, frees, reallocs, bytes_allocated, bytes_freed, checked_bytes, events, events_dropped;

static uint64_t mix(uint64_t x)
{
    x ^= x >> 33;
    x *= 0xFF51AFD7ED558CCDULL;
    x ^= x >> 33;
    x *= 0xC4CEB9FE1A85EC53ULL;
    return x ^ (x >> 33);
}

static uint64_t check_of(const fh_header *h, uint32_t size, uint32_t type)
{
    return mix((uint64_t)(uintptr_t)h ^ ((uint64_t)size << 32 | type) ^ secret);
}

static uint64_t tail_of(uint64_t check) { return mix(check ^ 0x7461696C7461696CULL); }

static size_t total_of(uint32_t size) { return sizeof(fh_header) + (size_t)size + TAIL; }

static void peak(fh_atomic *at, int64_t value)
{
    int64_t seen = fh_load(at);
    while (value > seen && !fh_cas(at, seen, value)) seen = fh_load(at);
}

/* The events' ring: a bounded multi-producer queue (Vyukov's), each slot with its sequence. */
#define RING_EVENTS 256
static struct
{
    fh_atomic sequence;
    moono_fmod_heap_event event;
} slots[RING_EVENTS];
static fh_atomic enqueue_at, dequeue_at;

static void post(const moono_fmod_heap_event *e)
{
    int64_t at = fh_load(&enqueue_at);
    for (;;)
    {
        int64_t sequence = fh_load(&slots[at & (RING_EVENTS - 1)].sequence);
        if (sequence == at)
        {
            if (fh_cas(&enqueue_at, at, at + 1)) break;
            at = fh_load(&enqueue_at);
        }
        else if (sequence < at)
        {
            fh_add(&events_dropped, 1);
            return;
        }
        else at = fh_load(&enqueue_at);
    }
    slots[at & (RING_EVENTS - 1)].event = *e;
    fh_store(&slots[at & (RING_EVENTS - 1)].sequence, at + 1);
    fh_add(&events, 1);
}

FH_EXPORT int moono_fmod_heap_events(moono_fmod_heap_event *out, int most)
{
    int taken = 0;
    if (!initialized) return 0;
    while (taken < most)
    {
        int64_t at = fh_load(&dequeue_at);
        int64_t sequence = fh_load(&slots[at & (RING_EVENTS - 1)].sequence);
        if (sequence != at + 1) break;
        if (!fh_cas(&dequeue_at, at, at + 1)) continue;
        out[taken++] = slots[at & (RING_EVENTS - 1)].event;
        fh_store(&slots[at & (RING_EVENTS - 1)].sequence, at + RING_EVENTS);
    }
    return taken;
}

static void event_start(moono_fmod_heap_event *e, uint32_t kind, const void *user, uint32_t size, uint32_t type)
{
    memset(e, 0, sizeof *e);
    e->kind = kind;
    e->address = (uint64_t)(uintptr_t)user;
    e->size = size;
    e->type = type;
    e->time_ms = now_ms();
}

static void event_byte(moono_fmod_heap_event *e, int64_t offset, uint8_t value, uint8_t expected)
{
    if (e->changed == 0) e->first_offset = offset;
    e->last_offset = offset;
    e->changed++;
    if (e->count < FH_EVENT_BYTES)
    {
        e->offsets[e->count] = (int32_t)offset;
        e->values[e->count] = value;
        e->expected[e->count] = expected;
        e->count++;
    }
}

/* Bytes `at` against `expected` (NULL: all FILL), as offsets from `base`. */
static void compare(moono_fmod_heap_event *e, const uint8_t *at, const uint8_t *expected, size_t n, int64_t base)
{
    size_t i = 0;
    if (!expected)
        for (; i + 8 <= n; i += 8)
        {
            uint64_t word;
            memcpy(&word, at + i, 8);
            if (word == FILL_WORD) continue;
            for (size_t j = i; j < i + 8; j++)
                if (at[j] != FILL) event_byte(e, base + (int64_t)j, at[j], FILL);
        }
    for (; i < n; i++)
    {
        uint8_t want = expected ? expected[i] : (uint8_t)FILL;
        if (at[i] != want) event_byte(e, base + (int64_t)i, at[i], want);
    }
}

/* A quarantined block on its way out: checked whole, then freed. */
static void release(const fh_entry *q)
{
    fh_header want;
    moono_fmod_heap_event e;
    uint8_t *user = (uint8_t *)(q->block + 1);
    size_t total = total_of(q->size);
    want.magic = MAGIC_FREED;
    want.size = q->size;
    want.type = q->type;
    want.check = q->check;
    want.freed_ms = q->freed_ms;
    event_start(&e, FH_WRITE_AFTER_FREE, user, q->size, q->type);
    compare(&e, (const uint8_t *)q->block, (const uint8_t *)&want, sizeof want, -(int64_t)sizeof want);
    compare(&e, user, NULL, (size_t)q->size + TAIL, 0);
    fh_add(&checked_bytes, total);
    if (e.changed)
    {
        e.age_ms = ticks_ms() - q->freed_ms;
        post(&e);
    }
    fh_add(&held_bytes, -(int64_t)total);
    raw_free(q->block);
}

/* Takes up to `most` of the oldest blocks out while the quarantine is over `keep` bytes (under
 * the lock). */
static size_t take_oldest(fh_entry *out, size_t most, uint64_t keep)
{
    size_t n = 0;
    while (ring_count && ring_bytes > keep && n < most)
    {
        out[n] = ring[ring_head];
        ring_head = (ring_head + 1) % ring_capacity;
        ring_count--;
        ring_bytes -= total_of(out[n].size);
        n++;
    }
    return n;
}

static void evict(uint64_t keep)
{
    fh_entry out[64];
    for (;;)
    {
        fh_lock();
        size_t n = take_oldest(out, 64, keep);
        fh_unlock();
        for (size_t i = 0; i < n; i++) release(&out[i]);
        if (n < 64) return;
    }
}

/* Adds a block to the FIFO (under the lock); 0 when the FIFO can't grow. */
static int push(const fh_entry *q)
{
    if (ring_count == ring_capacity)
    {
        size_t capacity = ring_capacity ? ring_capacity * 2 : 4096;
        fh_entry *grown = (fh_entry *)raw_alloc(capacity * sizeof *grown);
        if (!grown) return 0;
        for (size_t i = 0; i < ring_count; i++) grown[i] = ring[(ring_head + i) % ring_capacity];
        if (ring) raw_free(ring);
        ring = grown;
        ring_capacity = capacity;
        ring_head = 0;
    }
    ring[(ring_head + ring_count) % ring_capacity] = *q;
    ring_count++;
    ring_bytes += total_of(q->size);
    return 1;
}

FH_EXPORT int moono_fmod_heap_init(uint64_t quarantine_bytes)
{
    if (initialized) return 1;
    if (!raw_init()) return 0;
    for (int i = 0; i < RING_EVENTS; i++) slots[i].sequence = i;
    secret = mix((uint64_t)(uintptr_t)&secret ^ (uint64_t)now_ms());
    limit = quarantine_bytes;
    initialized = 1;
    return 1;
}

static void *alloc(uint32_t size, uint32_t type)
{
    size_t total = total_of(size);
    fh_header *h;
    uint8_t *user;
    uint64_t tail;
    if (!initialized) return NULL;
    h = (fh_header *)raw_alloc(total);
    if (!h)
    {
        moono_fmod_heap_flush();
        h = (fh_header *)raw_alloc(total);
        if (!h) return NULL;
    }
    h->size = size;
    h->type = type;
    h->check = check_of(h, size, type);
    h->freed_ms = 0;
    user = (uint8_t *)(h + 1);
    tail = tail_of(h->check);
    memcpy(user + size, &tail, TAIL);
    fh_store(&h->magic, MAGIC_LIVE);
    peak(&peak_live, fh_add(&live_bytes, size) + size);
    fh_add(&live_blocks, 1);
    peak(&peak_held, fh_add(&held_bytes, total) + (int64_t)total);
    fh_add(&bytes_allocated, size);
    return user;
}

/* The live block `user` is in, or NULL after posting why it isn't one. */
static fh_header *live_header(void *user)
{
    fh_header *h = (fh_header *)user - 1;
    int64_t magic = fh_load(&h->magic);
    moono_fmod_heap_event e;
    if (magic == MAGIC_LIVE && h->check == check_of(h, h->size, h->type)) return h;
    if (magic == MAGIC_FREED && h->check == check_of(h, h->size, h->type))
        event_start(&e, FH_DOUBLE_FREE, user, h->size, h->type);
    else if (magic == MAGIC_LIVE)
        /* The size or type was written over (or the check word): neither is trusted, the block
         * is leaked. The size and type as found. */
        event_start(&e, FH_HEADER_DAMAGED, user, h->size, h->type);
    else
    {
        /* No header of ours: the magic as found, against ours. */
        int64_t want = MAGIC_LIVE;
        event_start(&e, FH_NOT_OURS, user, 0, 0);
        compare(&e, (const uint8_t *)h, (const uint8_t *)&want, sizeof want, -(int64_t)sizeof *h);
    }
    post(&e);
    return NULL;
}

static void release_live(void *user)
{
    fh_header *h = live_header(user);
    uint8_t *u = (uint8_t *)user;
    uint64_t tail;
    size_t total;
    fh_entry q;
    if (!h) return;
    if (!fh_cas(&h->magic, MAGIC_LIVE, MAGIC_FREED))
    {
        moono_fmod_heap_event e;
        event_start(&e, FH_DOUBLE_FREE, user, h->size, h->type);
        post(&e);
        return;
    }
    tail = tail_of(h->check);
    if (memcmp(u + h->size, &tail, TAIL) != 0)
    {
        moono_fmod_heap_event e;
        event_start(&e, FH_OVERRUN, user, h->size, h->type);
        compare(&e, u + h->size, (const uint8_t *)&tail, TAIL, h->size);
        post(&e);
    }
    total = total_of(h->size);
    fh_add(&live_bytes, -(int64_t)h->size);
    fh_add(&live_blocks, -1);
    fh_add(&bytes_freed, h->size);
    if (limit == 0 || total > limit / 4)
    {
        fh_add(&held_bytes, -(int64_t)total);
        raw_free(h);
        return;
    }
    q.block = h;
    q.size = h->size;
    q.type = h->type;
    q.check = h->check;
    q.freed_ms = h->freed_ms = ticks_ms();
    memset(u, FILL, (size_t)h->size + TAIL);
    fh_lock();
    int queued = push(&q);
    fh_unlock();
    if (!queued)
    {
        release(&q);
        return;
    }
    evict(limit);
}

FH_EXPORT void *moono_fmod_alloc(unsigned int size, unsigned int type, const char *sourcestr)
{
    (void)sourcestr;
    fh_add(&allocs, 1);
    return alloc(size, type);
}

FH_EXPORT void moono_fmod_free(void *ptr, unsigned int type, const char *sourcestr)
{
    (void)type;
    (void)sourcestr;
    if (!ptr || !initialized) return;
    fh_add(&frees, 1);
    release_live(ptr);
}

/* As C's realloc: NULL allocates; on failure NULL, and the old block is still FMOD's. The old
 * block goes through the quarantine as a freed one. */
FH_EXPORT void *moono_fmod_realloc(void *ptr, unsigned int size, unsigned int type, const char *sourcestr)
{
    fh_header *h;
    void *grown;
    (void)sourcestr;
    if (!ptr) return moono_fmod_alloc(size, type, sourcestr);
    if (!initialized) return NULL;
    fh_add(&reallocs, 1);
    h = live_header(ptr);
    if (!h) return NULL;
    grown = alloc(size, type);
    if (!grown) return NULL;
    memcpy(grown, ptr, h->size < size ? h->size : size);
    release_live(ptr);
    return grown;
}

FH_EXPORT void moono_fmod_heap_flush(void)
{
    if (initialized) evict(0);
}

FH_EXPORT void moono_fmod_heap_stats(moono_fmod_heap_counters *out)
{
    out->live_bytes = (uint64_t)fh_load(&live_bytes);
    out->live_blocks = (uint64_t)fh_load(&live_blocks);
    out->peak_live_bytes = (uint64_t)fh_load(&peak_live);
    fh_lock();
    out->quarantined_bytes = ring_bytes;
    out->quarantined_blocks = ring_count;
    fh_unlock();
    out->peak_held_bytes = (uint64_t)fh_load(&peak_held);
    out->allocs = (uint64_t)fh_load(&allocs);
    out->frees = (uint64_t)fh_load(&frees);
    out->reallocs = (uint64_t)fh_load(&reallocs);
    out->bytes_allocated = (uint64_t)fh_load(&bytes_allocated);
    out->bytes_freed = (uint64_t)fh_load(&bytes_freed);
    out->checked_bytes = (uint64_t)fh_load(&checked_bytes);
    out->events = (uint64_t)fh_load(&events);
    out->events_dropped = (uint64_t)fh_load(&events_dropped);
    out->quarantine_limit = limit;
}
