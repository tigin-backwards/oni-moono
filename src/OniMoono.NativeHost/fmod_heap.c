/* FMOD's allocator with a quarantine: game tweak fmod-quarantine (FmodQuarantine.cs, which gives
 * these three callbacks to FMOD_Memory_Initialize before FMOD Studio starts; docs/TWEAKS.md).
 *
 * Why: issues #72, #73, #58 and #78 are heap corruption (0xC0000374) found in RtlFreeHeap on the
 * "FMOD Studio update thread". FMOD's staff acknowledged a write to deallocated memory in the
 * stop/steal path of 2.01.18 and later, "which could crash if the memory is reallocated
 * immediately" (qa.fmod.com/t/hard-crash-in-fmodstudiol/24589), with no fix. In #78's dump a stale
 * EventInstance::stop increments a dword at +0x84 of a freed instance and zeroes the byte at +0xBD.
 * A first version gave FMOD a private Windows heap: the late write then landed on that heap's own
 * entry headers once a block left the quarantine, and RtlFreeHeap still failed fast. So here no
 * bookkeeping that we or Windows trust lives in or next to FMOD's blocks:
 *
 * - Blocks up to 16 KB (with their canary) are slots of a size class, carved from 64 KB slabs in
 *   one reserved range of address space. Which class a slab holds, each slot's state (free, live,
 *   quarantined), its size and FMOD's type for it, and each slab's free slots are side tables in
 *   memory of their own, found from the address alone. Larger blocks get pages of their own
 *   (VirtualAlloc, mmap elsewhere), listed in a table of their own.
 * - A stray write can then change only FMOD's data, or a freed block's fill, which is checked.
 *   Nothing the allocator reads back from a block decides what it does.
 * - A freed block is filled with FILL and kept in a FIFO quarantine of `limit` bytes, oldest out
 *   first; large blocks may hold a quarter of it at most, so a burst of freed sample data does not
 *   push the small blocks (event instances among them) out early. When it leaves, the whole block is checked; a changed byte is an event
 *   (FH_WRITE_AFTER_FREE) with its offsets and values, which the player logs. A free slot is
 *   checked once more when it is reused: a late write after its quarantine is FH_WRITE_TO_FREE.
 * - The bytes after each block's end, to its slot's end (at least 8), are a canary: an overrun is
 *   an event. A write just before a block lands in the previous slot (its canary or fill), so it
 *   is found too, reported against that slot. A double free is the slot's state, and a pointer
 *   this allocator never returned is not in a slot or the large table: events, never a crash.
 * - Empty slabs stay committed and are carved again for any class (checked first): slab memory
 *   is never unmapped, so a late write never faults. Large blocks out of their quarantine wait in
 *   a small cache (8 MB) for an allocation of the same pages, checked again when reused; the
 *   oldest are unmapped.
 *
 * Nothing here calls managed code: FMOD calls in on its mixer, update and loading threads. The
 * events go into a lock-free ring that the main thread drains (moono_fmod_heap_events). Blocks
 * are 16-byte aligned. Each size class has its lock; the slabs, the large table and the
 * quarantine one each. Blocks are filled and checked outside the locks. */

#if !defined(_WIN32) && !defined(_DEFAULT_SOURCE)
#define _DEFAULT_SOURCE /* mmap's MAP_ANONYMOUS under -std=c11 */
#endif
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
typedef SRWLOCK fh_mutex;
static void mutex_init(fh_mutex *m) { InitializeSRWLock(m); }
static void mutex_lock(fh_mutex *m) { AcquireSRWLockExclusive(m); }
static void mutex_unlock(fh_mutex *m) { ReleaseSRWLockExclusive(m); }
static void *vm_reserve(size_t n) { return VirtualAlloc(NULL, n, MEM_RESERVE, PAGE_NOACCESS); }
static int vm_commit(void *p, size_t n) { return VirtualAlloc(p, n, MEM_COMMIT, PAGE_READWRITE) != NULL; }
static void *vm_alloc(size_t n) { return VirtualAlloc(NULL, n, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE); }
static void vm_free(void *p, size_t n)
{
    (void)n;
    VirtualFree(p, 0, MEM_RELEASE);
}
static int64_t now_ms(void)
{
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    return (int64_t)((((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime) - 116444736000000000ULL) / 10000;
}
/* For a block's age: cheap, to the tick (10-16 ms). */
static int64_t ticks_ms(void) { return (int64_t)GetTickCount64(); }
#else
#include <pthread.h>
#include <sys/mman.h>
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
typedef pthread_mutex_t fh_mutex;
static void mutex_init(fh_mutex *m) { pthread_mutex_init(m, NULL); }
static void mutex_lock(fh_mutex *m) { pthread_mutex_lock(m); }
static void mutex_unlock(fh_mutex *m) { pthread_mutex_unlock(m); }
#ifndef MAP_NORESERVE
#define MAP_NORESERVE 0
#endif
static void *vm_reserve(size_t n)
{
    void *p = mmap(NULL, n, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    return p == MAP_FAILED ? NULL : p;
}
static int vm_commit(void *p, size_t n) { return mprotect(p, n, PROT_READ | PROT_WRITE) == 0; }
static void *vm_alloc(size_t n)
{
    void *p = mmap(NULL, n, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    return p == MAP_FAILED ? NULL : p;
}
static void vm_free(void *p, size_t n) { munmap(p, n); }
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

#define FILL 0xDB
#define FILL_WORD 0xDBDBDBDBDBDBDBDBULL
#define CANARY 0xCA
#define CANARY_WORD 0xCACACACACACACACAULL
#define TAIL 8 /* at least this much canary after each block */
#define PAGE 4096u

/* The slabs: SLAB bytes each, in ARENA bytes of address space reserved at init and committed a
 * slab at a time. */
#define SLAB 65536u
#define SLAB_SHIFT 16
#define ARENA ((size_t)16 << 30)
#define SLABS ((uint32_t)(ARENA >> SLAB_SHIFT))
#define MAX_SLOT (16384u)
#define MAX_SLOTS (SLAB / 16)
#define NONE 0xFFFFFFFFu
/* Each slab's side tables, at its index times SIDE in a range of their own: a slot_info for each
 * slot, then its free slots' indices (a stack). */
#define SIDE (MAX_SLOTS * (sizeof(slot_info) + sizeof(uint16_t)))

enum { FREE = 0, LIVE = 1, QUARANTINED = 2 };

typedef struct slot_info
{
    uint16_t size;
    uint8_t state;
    uint8_t type; /* FMOD's type, packed (pack_type) */
} slot_info;

typedef struct slab
{
    fh_atomic cls; /* its size class, 0 while it is in the empty pool or not carved yet */
    uint16_t slots, free;
    uint32_t prev, next; /* in its class's list of slabs with free slots, or the empty pool */
    uint32_t side_bytes; /* side table committed for it */
    uint32_t partial;    /* in its class's list */
} slab;

/* Size classes: 16 to 256 bytes by 16, then four per power of two up to MAX_SLOT. */
#define CLASSES 40
static uint32_t class_slot[CLASSES + 1];
static uint32_t class_slots[CLASSES + 1];
static fh_mutex class_lock[CLASSES + 1];
static uint32_t class_partial[CLASSES + 1];

static uint8_t *arena;
static slot_info *side;  /* SIDE bytes per slab */
static slab *slabs;      /* a slab's descriptor */
static fh_mutex arena_lock;
static fh_atomic carved;  /* slabs committed so far, from the arena's start (their descriptors too) */
static uint32_t pool = NONE; /* empty slabs */

static uint32_t class_of(size_t n)
{
    unsigned b = 0;
    size_t m;
    if (n <= 256) return (uint32_t)((n + 15) / 16 + (n == 0));
    m = n - 1;
    while (m >> (b + 1)) b++; /* b = floor(log2(n - 1)), 8..13 */
    return 16 + (b - 8) * 4 + (uint32_t)(m >> (b - 2)) - 4 + 1;
}

static uint8_t pack_type(uint32_t type) { return (uint8_t)((type & 0x1F) | ((type >> 21 & 1) << 5) | ((type & ~0x20001Fu) ? 0x40 : 0)); }
static uint32_t unpack_type(uint8_t t) { return (uint32_t)(t & 0x1F) | ((uint32_t)(t >> 5 & 1) << 21) | ((t & 0x40) ? 0x80000000u : 0); }

static slot_info *infos_of(uint32_t s) { return (slot_info *)((uint8_t *)side + (size_t)s * SIDE); }
static uint16_t *stack_of(uint32_t s) { return (uint16_t *)(infos_of(s) + slabs[s].slots); }
static uint8_t *base_of(uint32_t s) { return arena + ((size_t)s << SLAB_SHIFT); }

/* The large blocks' table: open addressing on the page number, under large_lock. */
typedef struct large_entry
{
    uint64_t key; /* address >> 12; 0 empty, 1 removed */
    uint32_t size, type;
    uint64_t bytes; /* the pages it has */
    uint32_t state, unused;
} large_entry;

static large_entry *large;
static size_t large_capacity, large_used, large_count;
static fh_mutex large_lock;

/* Large blocks out of their quarantine, kept (all fill, their table entries FREE) for an
 * allocation of the same pages, oldest first: mapping and unmapping pages costs a few
 * microseconds and a page fault per page. At most CACHE_BLOCKS of them and CACHE_BYTES. */
#define CACHE_BLOCKS 64
#define CACHE_BYTES ((uint64_t)8 << 20)
static struct { uint8_t *block; uint64_t bytes; } cache[CACHE_BLOCKS];
static size_t cache_count;
static uint64_t cache_bytes;

/* A quarantined block, with what it must be checked against (kept here, not in the block). */
typedef struct fh_entry
{
    uint8_t *block;
    uint64_t bytes; /* its slot or its pages */
    uint32_t size, type;
    int64_t freed_ms;
    uint64_t order; /* when it went in, across both FIFOs */
} fh_entry;

typedef struct fifo
{
    fh_entry *ring;
    size_t capacity, head, count;
    uint64_t bytes, limit;
    fh_atomic age_ms; /* the age of the last block to leave */
} fifo;

static int initialized;
static uint64_t limit;
static fifo small_q, large_q;
static fh_mutex quarantine_lock;
static uint64_t quarantined_so_far; /* (under the lock) */

static fh_atomic live_bytes, live_blocks, peak_live, held_bytes, peak_held;
static fh_atomic slab_bytes, side_bytes, large_bytes;
static fh_atomic allocs, frees, reallocs, bytes_allocated, bytes_freed, checked_bytes, events, events_dropped;
static fh_atomic small_frees, small_bytes_freed;

static void peak(fh_atomic *at, int64_t value)
{
    int64_t seen = fh_load(at);
    while (value > seen && !fh_cas(at, seen, value)) seen = fh_load(at);
}

static void held(int64_t delta)
{
    if (delta > 0) peak(&peak_held, fh_add(&held_bytes, delta) + delta);
    else fh_add(&held_bytes, delta);
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

/* Bytes `at` against `want` repeated, as offsets from `base`. */
static void compare(moono_fmod_heap_event *e, const uint8_t *at, uint8_t want, size_t n, int64_t base)
{
    uint64_t word = want * 0x0101010101010101ULL;
    size_t i = 0;
    for (; i + 8 <= n; i += 8)
    {
        uint64_t got;
        memcpy(&got, at + i, 8);
        if (got == word) continue;
        for (size_t j = i; j < i + 8; j++)
            if (at[j] != want) event_byte(e, base + (int64_t)j, at[j], want);
    }
    for (; i < n; i++)
        if (at[i] != want) event_byte(e, base + (int64_t)i, at[i], want);
}

/* Whether `n` bytes at `at` are all `want` (the common case, fast). */
static int all(const uint8_t *at, uint64_t word, size_t n)
{
    size_t i = 0;
    for (; i + 32 <= n; i += 32)
    {
        uint64_t a, b, c, d;
        memcpy(&a, at + i, 8);
        memcpy(&b, at + i + 8, 8);
        memcpy(&c, at + i + 16, 8);
        memcpy(&d, at + i + 24, 8);
        if ((a ^ word) | (b ^ word) | (c ^ word) | (d ^ word)) return 0;
    }
    for (; i < n; i++)
        if (at[i] != (uint8_t)word) return 0;
    return 1;
}

/* A block's canary, from its end to its slot's: an event when it changed. */
static void check_canary(uint8_t *user, uint32_t size, uint64_t bytes, uint32_t type)
{
    if (all(user + size, CANARY_WORD, (size_t)(bytes - size))) return;
    moono_fmod_heap_event e;
    event_start(&e, FH_OVERRUN, user, size, type);
    compare(&e, user + size, CANARY, (size_t)(bytes - size), size);
    post(&e);
}

/* ---- Slabs ---- */

static void list_remove(uint32_t *head, uint32_t s)
{
    if (slabs[s].prev != NONE) slabs[slabs[s].prev].next = slabs[s].next;
    else *head = slabs[s].next;
    if (slabs[s].next != NONE) slabs[slabs[s].next].prev = slabs[s].prev;
    slabs[s].prev = slabs[s].next = NONE;
}

static void list_push(uint32_t *head, uint32_t s)
{
    slabs[s].prev = NONE;
    slabs[s].next = *head;
    if (*head != NONE) slabs[*head].prev = s;
    *head = s;
}

/* A slab for class `c`, all its slots free and filled (from the pool, or newly committed); NONE
 * when the arena is full or memory is out. Not yet in any list. */
static uint32_t new_slab(uint32_t c)
{
    uint32_t s;
    int pooled;
    size_t need = class_slots[c] * (sizeof(slot_info) + sizeof(uint16_t));
    mutex_lock(&arena_lock);
    s = pool;
    pooled = s != NONE;
    if (pooled) list_remove(&pool, s);
    else if (fh_load(&carved) < SLABS)
    {
        s = (uint32_t)fh_load(&carved);
        size_t at = (size_t)s * sizeof(slab);
        /* Its descriptor's page, then the slab. */
        if (!vm_commit((uint8_t *)slabs + (at & ~(size_t)(PAGE - 1)), ((at + sizeof(slab) + PAGE - 1) & ~(size_t)(PAGE - 1)) - (at & ~(size_t)(PAGE - 1)))
            || !vm_commit(arena + ((size_t)s << SLAB_SHIFT), SLAB))
        {
            mutex_unlock(&arena_lock);
            return NONE;
        }
        slabs[s].side_bytes = 0;
        fh_store(&carved, s + 1);
        fh_add(&slab_bytes, SLAB);
        held(SLAB);
    }
    mutex_unlock(&arena_lock);
    if (s == NONE) return NONE;
    need = (need + PAGE - 1) & ~(size_t)(PAGE - 1);
    if (need > slabs[s].side_bytes)
    {
        if (!vm_commit(infos_of(s), need))
        {
            /* Back to the pool, empty. */
            mutex_lock(&arena_lock);
            list_push(&pool, s);
            mutex_unlock(&arena_lock);
            return NONE;
        }
        fh_add(&side_bytes, need - slabs[s].side_bytes);
        held((int64_t)(need - slabs[s].side_bytes));
        slabs[s].side_bytes = (uint32_t)need;
    }
    slabs[s].slots = (uint16_t)class_slots[c];
    slabs[s].free = slabs[s].slots;
    slabs[s].partial = 0;
    memset(infos_of(s), 0, slabs[s].slots * sizeof(slot_info));
    {
        uint16_t *stack = stack_of(s);
        /* Popped from the end: slot 0 first. */
        for (uint32_t i = 0; i < slabs[s].slots; i++) stack[i] = (uint16_t)(slabs[s].slots - 1 - i);
    }
    /* An empty slab is all fill (its slots were checked, the rest carved so): a late write that
     * reached it while it was in the pool is found here. */
    if (pooled && !all(base_of(s), FILL_WORD, SLAB))
    {
        moono_fmod_heap_event e;
        event_start(&e, FH_WRITE_TO_FREE, base_of(s), SLAB, 0);
        compare(&e, base_of(s), FILL, SLAB, 0);
        e.age_ms = -1;
        post(&e);
    }
    memset(base_of(s), FILL, SLAB);
    return s;
}

static void *alloc_small(uint32_t size, uint32_t type)
{
    uint32_t c = class_of((size_t)size + TAIL), s, slot, bytes = class_slot[c];
    uint8_t *user;
    mutex_lock(&class_lock[c]);
    s = class_partial[c];
    if (s == NONE)
    {
        mutex_unlock(&class_lock[c]);
        s = new_slab(c);
        if (s == NONE) return NULL;
        mutex_lock(&class_lock[c]);
        fh_store(&slabs[s].cls, c);
        slabs[s].partial = 1;
        list_push(&class_partial[c], s);
    }
    slot = stack_of(s)[--slabs[s].free];
    infos_of(s)[slot].state = LIVE;
    infos_of(s)[slot].size = (uint16_t)size;
    infos_of(s)[slot].type = pack_type(type);
    if (slabs[s].free == 0)
    {
        list_remove(&class_partial[c], s);
        slabs[s].partial = 0;
    }
    mutex_unlock(&class_lock[c]);
    user = base_of(s) + (size_t)slot * bytes;
    /* Still all fill since it was checked (or carved): else written after its quarantine. */
    if (!all(user, FILL_WORD, bytes))
    {
        moono_fmod_heap_event e;
        event_start(&e, FH_WRITE_TO_FREE, user, bytes, 0);
        compare(&e, user, FILL, bytes, 0);
        e.age_ms = -1;
        post(&e);
    }
    memset(user + size, CANARY, bytes - size);
    return user;
}

/* A slot back to its slab's free ones (its fill checked); an empty slab to the pool, unless it is
 * its class's only one with free slots. */
static void free_slot(uint32_t s, uint32_t slot)
{
    uint32_t c = (uint32_t)fh_load(&slabs[s].cls);
    int empty = 0;
    mutex_lock(&class_lock[c]);
    infos_of(s)[slot].state = FREE;
    stack_of(s)[slabs[s].free++] = (uint16_t)slot;
    if (!slabs[s].partial)
    {
        list_push(&class_partial[c], s);
        slabs[s].partial = 1;
    }
    if (slabs[s].free == slabs[s].slots && (slabs[s].next != NONE || slabs[s].prev != NONE))
    {
        list_remove(&class_partial[c], s);
        slabs[s].partial = 0;
        fh_store(&slabs[s].cls, 0);
        empty = 1;
    }
    mutex_unlock(&class_lock[c]);
    if (empty)
    {
        mutex_lock(&arena_lock);
        list_push(&pool, s);
        mutex_unlock(&arena_lock);
    }
}

/* ---- Large blocks ---- */

static uint64_t hash(uint64_t key)
{
    key *= 0x9E3779B97F4A7C15ULL;
    return key ^ (key >> 29);
}

/* Its entry, or NULL (under large_lock). */
static large_entry *large_find(uint64_t key)
{
    if (!large) return NULL;
    for (size_t i = hash(key) & (large_capacity - 1);; i = (i + 1) & (large_capacity - 1))
    {
        if (large[i].key == key) return &large[i];
        if (large[i].key == 0) return NULL;
    }
}

/* Adds an entry (under large_lock); 0 when the table can't grow. */
static int large_add(const large_entry *entry)
{
    if ((large_used + 1) * 2 > large_capacity)
    {
        size_t capacity = large_capacity ? (large_count + 1) * 4 > large_capacity ? large_capacity * 2 : large_capacity : 1024;
        large_entry *table = (large_entry *)vm_alloc(capacity * sizeof *table);
        if (!table) return 0;
        for (size_t i = 0; i < large_capacity; i++)
            if (large[i].key > 1)
            {
                size_t j = hash(large[i].key) & (capacity - 1);
                while (table[j].key) j = (j + 1) & (capacity - 1);
                table[j] = large[i];
            }
        if (large) vm_free(large, large_capacity * sizeof *large);
        large = table;
        large_capacity = capacity;
        large_used = large_count;
    }
    size_t i = hash(entry->key) & (large_capacity - 1);
    while (large[i].key > 1) i = (i + 1) & (large_capacity - 1);
    if (large[i].key == 0) large_used++;
    large[i] = *entry;
    large_count++;
    return 1;
}

static void *alloc_large(uint32_t size, uint32_t type)
{
    uint64_t bytes = ((uint64_t)size + TAIL + PAGE - 1) & ~(uint64_t)(PAGE - 1);
    uint8_t *user = NULL;
    large_entry entry;
    int added;
    mutex_lock(&large_lock);
    for (size_t i = 0; i < cache_count; i++)
        if (cache[i].bytes == bytes)
        {
            large_entry *cached = large_find((uint64_t)(uintptr_t)cache[i].block >> 12);
            user = cache[i].block;
            cached->state = LIVE;
            cached->size = size;
            cached->type = type;
            cache_bytes -= bytes;
            memmove(&cache[i], &cache[i + 1], (cache_count - i - 1) * sizeof cache[0]);
            cache_count--;
            break;
        }
    mutex_unlock(&large_lock);
    if (user)
    {
        /* Still all fill since it was checked: else written after its quarantine. */
        if (!all(user, FILL_WORD, (size_t)bytes))
        {
            moono_fmod_heap_event e;
            event_start(&e, FH_WRITE_TO_FREE, user, (uint32_t)bytes, 0);
            compare(&e, user, FILL, (size_t)bytes, 0);
            e.age_ms = -1;
            post(&e);
        }
        memset(user + size, CANARY, (size_t)(bytes - size));
        return user;
    }
    user = (uint8_t *)vm_alloc((size_t)bytes);
    if (!user) return NULL;
    memset(&entry, 0, sizeof entry);
    entry.key = (uint64_t)(uintptr_t)user >> 12;
    entry.size = size;
    entry.type = type;
    entry.bytes = bytes;
    entry.state = LIVE;
    mutex_lock(&large_lock);
    added = large_add(&entry);
    mutex_unlock(&large_lock);
    if (!added)
    {
        vm_free(user, (size_t)bytes);
        return NULL;
    }
    fh_add(&large_bytes, bytes);
    held((int64_t)bytes);
    memset(user + size, CANARY, (size_t)(bytes - size));
    return user;
}

/* A large block out of its quarantine, all fill: into the cache, the oldest out of it unmapped
 * when it is full. */
static void unmap_large(uint8_t *user);
static void cache_large(uint8_t *user, uint64_t bytes)
{
    uint8_t *out[CACHE_BLOCKS];
    size_t n = 0;
    mutex_lock(&large_lock);
    large_entry *entry = large_find((uint64_t)(uintptr_t)user >> 12);
    if (entry) entry->state = FREE;
    cache[cache_count].block = user;
    cache[cache_count].bytes = bytes;
    cache_count++;
    cache_bytes += bytes;
    while (cache_count > 0 && (cache_bytes > CACHE_BYTES || cache_count == CACHE_BLOCKS))
    {
        out[n++] = cache[0].block;
        cache_bytes -= cache[0].bytes;
        memmove(&cache[0], &cache[1], (cache_count - 1) * sizeof cache[0]);
        cache_count--;
    }
    mutex_unlock(&large_lock);
    for (size_t i = 0; i < n; i++) unmap_large(out[i]);
}

static void unmap_large(uint8_t *user)
{
    uint64_t bytes = 0;
    mutex_lock(&large_lock);
    large_entry *entry = large_find((uint64_t)(uintptr_t)user >> 12);
    if (entry)
    {
        bytes = entry->bytes;
        entry->key = 1;
        large_count--;
    }
    mutex_unlock(&large_lock);
    if (!bytes) return;
    fh_add(&large_bytes, -(int64_t)bytes);
    held(-(int64_t)bytes);
    vm_free(user, (size_t)bytes);
}

/* ---- The quarantine ---- */

static int in_arena(const void *p) { return (const uint8_t *)p >= arena && (const uint8_t *)p < arena + ARENA; }

/* A quarantined block on its way out: checked whole, then freed. */
static void release(const fh_entry *q)
{
    fh_add(&checked_bytes, q->bytes);
    if (!all(q->block, FILL_WORD, (size_t)q->bytes))
    {
        moono_fmod_heap_event e;
        event_start(&e, FH_WRITE_AFTER_FREE, q->block, q->size, q->type);
        compare(&e, q->block, FILL, (size_t)q->bytes, 0);
        e.age_ms = ticks_ms() - q->freed_ms;
        post(&e);
        memset(q->block, FILL, (size_t)q->bytes);
    }
    if (in_arena(q->block))
    {
        uint32_t s = (uint32_t)((size_t)(q->block - arena) >> SLAB_SHIFT);
        uint32_t c = (uint32_t)fh_load(&slabs[s].cls);
        free_slot(s, (uint32_t)((size_t)(q->block - base_of(s)) / class_slot[c]));
    }
    else if (q->bytes <= CACHE_BYTES / 4) cache_large(q->block, q->bytes);
    else unmap_large(q->block);
}

/* Takes up to `most` of the oldest blocks out while the FIFO is over `keep` bytes (under the
 * lock). */
static size_t take_oldest(fifo *f, fh_entry *out, size_t most, uint64_t keep)
{
    size_t n = 0;
    while (f->count && f->bytes > keep && n < most)
    {
        out[n] = f->ring[f->head];
        f->head = (f->head + 1) % f->capacity;
        f->count--;
        f->bytes -= out[n].bytes;
        n++;
    }
    return n;
}

/* What the small blocks' FIFO has room for: the quarantine less what the large blocks' holds
 * (under the lock). */
static uint64_t small_keep(void) { return limit > large_q.bytes ? limit - large_q.bytes : 0; }

/* Releases a FIFO's oldest blocks until it holds `keep` bytes at most. */
static void evict(fifo *f, uint64_t keep)
{
    fh_entry out[64];
    for (;;)
    {
        mutex_lock(&quarantine_lock);
        size_t n = take_oldest(f, out, 64, keep);
        mutex_unlock(&quarantine_lock);
        if (n) fh_store(&f->age_ms, ticks_ms() - out[n - 1].freed_ms);
        for (size_t i = 0; i < n; i++) release(&out[i]);
        if (n < 64) return;
    }
}

/* Releases the oldest blocks of either FIFO until both hold `limit` bytes at most: one quarantine
 * in the order blocks were freed, of which the large blocks' cap takes its own. */
static void evict_oldest(void)
{
    fh_entry out[64];
    for (;;)
    {
        size_t n = 0;
        int64_t small_age = -1, large_age = -1, now;
        mutex_lock(&quarantine_lock);
        while (n < 64 && small_q.bytes + large_q.bytes > limit)
        {
            int large_first = large_q.count && (!small_q.count || large_q.ring[large_q.head].order < small_q.ring[small_q.head].order);
            fifo *f = large_first ? &large_q : &small_q;
            take_oldest(f, &out[n], 1, 0);
            if (large_first) large_age = out[n].freed_ms;
            else small_age = out[n].freed_ms;
            n++;
        }
        mutex_unlock(&quarantine_lock);
        now = ticks_ms();
        if (small_age >= 0) fh_store(&small_q.age_ms, now - small_age);
        if (large_age >= 0) fh_store(&large_q.age_ms, now - large_age);
        for (size_t i = 0; i < n; i++) release(&out[i]);
        if (n < 64) return;
    }
}

/* Adds a block to a FIFO (under the lock); 0 when it can't grow. */
static int push(fifo *f, const fh_entry *q)
{
    if (f->count == f->capacity)
    {
        size_t capacity = f->capacity ? f->capacity * 2 : 4096;
        fh_entry *grown = (fh_entry *)vm_alloc(capacity * sizeof *grown);
        if (!grown) return 0;
        for (size_t i = 0; i < f->count; i++) grown[i] = f->ring[(f->head + i) % f->capacity];
        if (f->ring) vm_free(f->ring, f->capacity * sizeof *f->ring);
        f->ring = grown;
        f->capacity = capacity;
        f->head = 0;
    }
    f->ring[(f->head + f->count) % f->capacity] = *q;
    f->ring[(f->head + f->count) % f->capacity].order = quarantined_so_far++;
    f->count++;
    f->bytes += q->bytes;
    return 1;
}

/* A freed block (already marked quarantined in its side table): filled, then into its FIFO, or
 * straight back when it has no room for it. */
static void quarantine(fifo *f, uint8_t *user, uint64_t bytes, uint32_t size, uint32_t type)
{
    fh_entry q;
    int queued = 0;
    q.block = user;
    q.bytes = bytes;
    q.size = size;
    q.type = type;
    q.freed_ms = ticks_ms();
    if (f->limit && bytes <= f->limit / 2)
    {
        memset(user, FILL, (size_t)bytes);
        mutex_lock(&quarantine_lock);
        queued = push(f, &q);
        mutex_unlock(&quarantine_lock);
    }
    if (!queued)
    {
        /* Back at once (a free slot is all fill: it is checked again when reused). */
        if (in_arena(user))
        {
            uint32_t s = (uint32_t)((size_t)(user - arena) >> SLAB_SHIFT);
            memset(user, FILL, (size_t)bytes);
            free_slot(s, (uint32_t)((size_t)(user - base_of(s)) / bytes));
        }
        else if (bytes <= CACHE_BYTES / 4)
        {
            memset(user, FILL, (size_t)bytes);
            cache_large(user, bytes);
        }
        else unmap_large(user);
        return;
    }
    /* The large blocks' FIFO to its quarter, then the oldest of either out to the limit. */
    if (f == &large_q) evict(&large_q, large_q.limit);
    evict_oldest();
}

/* ---- The callbacks ---- */

FH_EXPORT int moono_fmod_heap_init(uint64_t quarantine_bytes)
{
    if (initialized) return 1;
    arena = (uint8_t *)vm_reserve(ARENA);
    side = (slot_info *)vm_reserve((size_t)SLABS * SIDE);
    slabs = (slab *)vm_reserve((size_t)SLABS * sizeof(slab));
    if (!arena || !side || !slabs) return 0;
    for (uint32_t n = 1, c = 0; n <= MAX_SLOT; n++)
        if (class_of(n) != c)
        {
            c = class_of(n);
            /* The largest size of class c is where the next one starts, less one. */
            uint32_t top = n;
            while (top < MAX_SLOT && class_of(top + 1) == c) top++;
            class_slot[c] = top;
            class_slots[c] = SLAB / top;
            class_partial[c] = NONE;
            mutex_init(&class_lock[c]);
            n = top;
        }
    mutex_init(&arena_lock);
    mutex_init(&large_lock);
    mutex_init(&quarantine_lock);
    for (int i = 0; i < RING_EVENTS; i++) slots[i].sequence = i;
    limit = quarantine_bytes;
    /* Oldest out first across both FIFOs, with larger blocks (mostly sample data) capped at a
     * quarter: in play FMOD frees 4-12 MB/s of blocks up to 16 KB (event instances and the rest
     * of its objects) and under 1 MB/s of larger ones, so both stay as long; while it loads
     * sample data it frees tens of MB/s of larger ones, which then cannot push the small ones
     * below three quarters. */
    small_q.limit = quarantine_bytes;
    large_q.limit = quarantine_bytes / 4;
    initialized = 1;
    return 1;
}

static void *alloc(uint32_t size, uint32_t type)
{
    void *user;
    int small = (size_t)size + TAIL <= MAX_SLOT;
    if (!initialized) return NULL;
    user = small ? alloc_small(size, type) : alloc_large(size, type);
    if (!user)
    {
        moono_fmod_heap_flush();
        user = small ? alloc_small(size, type) : alloc_large(size, type);
        if (!user) return NULL;
    }
    peak(&peak_live, fh_add(&live_bytes, size) + size);
    fh_add(&live_blocks, 1);
    fh_add(&bytes_allocated, size);
    return user;
}

/* Where a live block is: its slab and slot, or (s == NONE) its large entry's size and pages. */
typedef struct place
{
    uint32_t s, slot, size, type;
    uint64_t bytes;
} place;

/* Finds the live block `user` and, with `mark`, marks it quarantined; 0 after posting why it
 * isn't one. With `resize` (and no mark), sets its size to that when it stays in its slot or
 * pages, and says so in place.s = NONE - 1. */
static int find_live(void *user, int mark, const uint32_t *resize, place *at)
{
    moono_fmod_heap_event e;
    uint8_t *u = (uint8_t *)user;
    if (in_arena(u))
    {
        uint32_t s = (uint32_t)((size_t)(u - arena) >> SLAB_SHIFT);
        for (;;)
        {
            uint32_t c;
            if (s >= (uint32_t)fh_load(&carved)) break;
            c = (uint32_t)fh_load(&slabs[s].cls);
            if (c == 0) break;
            mutex_lock(&class_lock[c]);
            if ((uint32_t)fh_load(&slabs[s].cls) != c)
            {
                mutex_unlock(&class_lock[c]);
                continue;
            }
            size_t offset = (size_t)(u - base_of(s));
            uint32_t slot = (uint32_t)(offset / class_slot[c]);
            if (offset % class_slot[c] || slot >= slabs[s].slots)
            {
                mutex_unlock(&class_lock[c]);
                break;
            }
            slot_info *info = &infos_of(s)[slot];
            at->s = s;
            at->slot = slot;
            at->size = info->size;
            at->type = unpack_type(info->type);
            at->bytes = class_slot[c];
            if (info->state != LIVE)
            {
                mutex_unlock(&class_lock[c]);
                event_start(&e, FH_DOUBLE_FREE, user, at->size, at->type);
                post(&e);
                return 0;
            }
            if (mark) info->state = QUARANTINED;
            else if (resize && class_of((size_t)*resize + TAIL) == c)
            {
                info->size = (uint16_t)*resize;
                at->s = NONE - 1;
            }
            mutex_unlock(&class_lock[c]);
            return 1;
        }
    }
    else
    {
        large_entry *entry;
        mutex_lock(&large_lock);
        entry = (((uintptr_t)u & (PAGE - 1)) == 0) ? large_find((uint64_t)(uintptr_t)u >> 12) : NULL;
        if (entry)
        {
            at->s = NONE;
            at->size = entry->size;
            at->type = entry->type;
            at->bytes = entry->bytes;
            if (entry->state != LIVE)
            {
                mutex_unlock(&large_lock);
                event_start(&e, FH_DOUBLE_FREE, user, at->size, at->type);
                post(&e);
                return 0;
            }
            if (mark) entry->state = QUARANTINED;
            else if (resize && ((uint64_t)*resize + TAIL + PAGE - 1) / PAGE * PAGE == entry->bytes)
            {
                entry->size = *resize;
                at->s = NONE - 1;
            }
            mutex_unlock(&large_lock);
            return 1;
        }
        mutex_unlock(&large_lock);
    }
    event_start(&e, FH_NOT_OURS, user, 0, 0);
    post(&e);
    return 0;
}

static void release_live(void *user)
{
    place at;
    if (!find_live(user, 1, NULL, &at)) return;
    check_canary((uint8_t *)user, at.size, at.bytes, at.type);
    fh_add(&live_bytes, -(int64_t)at.size);
    fh_add(&live_blocks, -1);
    fh_add(&bytes_freed, at.size);
    if (at.s != NONE)
    {
        fh_add(&small_frees, 1);
        fh_add(&small_bytes_freed, at.size);
    }
    quarantine(at.s != NONE ? &small_q : &large_q, (uint8_t *)user, at.bytes, at.size, at.type);
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

/* As C's realloc: NULL allocates; on failure NULL, and the old block is still FMOD's. A block
 * that stays in its slot (or its pages) keeps its address; else the old one goes through the
 * quarantine as a freed one. */
FH_EXPORT void *moono_fmod_realloc(void *ptr, unsigned int size, unsigned int type, const char *sourcestr)
{
    place at;
    void *grown;
    (void)sourcestr;
    if (!ptr) return moono_fmod_alloc(size, type, sourcestr);
    if (!initialized) return NULL;
    fh_add(&reallocs, 1);
    /* Its canary as it is, before a resize moves it. */
    if (!find_live(ptr, 0, NULL, &at)) return NULL;
    check_canary((uint8_t *)ptr, at.size, at.bytes, at.type);
    if (find_live(ptr, 0, &size, &at) && at.s == NONE - 1)
    {
        memset((uint8_t *)ptr + size, CANARY, (size_t)(at.bytes - size));
        if (size > at.size)
        {
            peak(&peak_live, fh_add(&live_bytes, size - at.size) + (size - at.size));
            fh_add(&bytes_allocated, size - at.size);
        }
        else fh_add(&live_bytes, -(int64_t)(at.size - size));
        return ptr;
    }
    grown = alloc(size, type);
    if (!grown) return NULL;
    memcpy(grown, ptr, at.size < size ? at.size : size);
    release_live(ptr);
    return grown;
}

FH_EXPORT void moono_fmod_heap_flush(void)
{
    if (!initialized) return;
    evict(&small_q, 0);
    evict(&large_q, 0);
    /* And the large blocks' cache (an allocation that failed wants their memory). */
    for (;;)
    {
        uint8_t *block = NULL;
        mutex_lock(&large_lock);
        if (cache_count)
        {
            block = cache[0].block;
            cache_bytes -= cache[0].bytes;
            memmove(&cache[0], &cache[1], (cache_count - 1) * sizeof cache[0]);
            cache_count--;
        }
        mutex_unlock(&large_lock);
        if (!block) break;
        unmap_large(block);
    }
}

FH_EXPORT void moono_fmod_heap_stats(moono_fmod_heap_counters *out)
{
    memset(out, 0, sizeof *out);
    out->live_bytes = (uint64_t)fh_load(&live_bytes);
    out->live_blocks = (uint64_t)fh_load(&live_blocks);
    out->peak_live_bytes = (uint64_t)fh_load(&peak_live);
    if (initialized)
    {
        mutex_lock(&quarantine_lock);
        out->quarantined_bytes = small_q.bytes + large_q.bytes;
        out->quarantined_blocks = small_q.count + large_q.count;
        out->small_quarantined_bytes = small_q.bytes;
        mutex_unlock(&quarantine_lock);
    }
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
    out->held_bytes = (uint64_t)fh_load(&held_bytes);
    out->slab_bytes = (uint64_t)fh_load(&slab_bytes);
    out->side_bytes = (uint64_t)fh_load(&side_bytes);
    out->large_bytes = (uint64_t)fh_load(&large_bytes);
    if (initialized)
    {
        mutex_lock(&quarantine_lock);
        out->small_quarantine_limit = small_keep();
        mutex_unlock(&quarantine_lock);
    }
    out->small_age_ms = (uint64_t)fh_load(&small_q.age_ms);
    out->large_age_ms = (uint64_t)fh_load(&large_q.age_ms);
    out->small_frees = (uint64_t)fh_load(&small_frees);
    out->small_bytes_freed = (uint64_t)fh_load(&small_bytes_freed);
}
