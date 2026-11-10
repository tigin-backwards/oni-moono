/* FMOD's allocator with a quarantine (fmod_heap.c; game tweak fmod-quarantine,
 * src/OniMoono.Host/GameTweaks/FmodQuarantine.cs). Plain C, no Windows types: the player builds
 * it into OniMoono.exe, the unit test (fmod_heap_test.c) into its own program. */
#ifndef MOONO_FMOD_HEAP_H
#define MOONO_FMOD_HEAP_H

#include <stdint.h>

#ifdef _WIN32
#define FH_EXPORT __declspec(dllexport)
#else
#define FH_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* What an event reports (moono_fmod_heap_event.kind). */
enum
{
    FH_WRITE_AFTER_FREE = 1, /* a quarantined block's fill changed before it was really freed */
    FH_DOUBLE_FREE = 2,      /* a block freed (or reallocated) while already free */
    FH_OVERRUN = 3,          /* the canary after a block's end changed while it was in use */
    FH_NOT_OURS = 4,         /* a pointer this allocator never returned (no header of ours) */
    FH_HEADER_DAMAGED = 5    /* a live block's header changed: written before its start */
};

#define FH_EVENT_BYTES 8

/* One event, for the player to log (moono_fmod_heap_events). Offsets are from the pointer FMOD
 * was given: negative ones are in the block's header, `size` and up in its tail canary (a write
 * after free can reach both: the whole quarantined block is checked). */
typedef struct moono_fmod_heap_event
{
    uint32_t kind;
    uint32_t type;            /* FMOD_MEMORY_TYPE the block was allocated as */
    uint32_t size;            /* the block's size as FMOD asked for it */
    uint32_t changed;         /* bytes that differ from what they must be */
    int64_t first_offset;     /* the first such byte's offset */
    int64_t last_offset;      /* the last one's */
    int64_t time_ms;          /* when it was found: milliseconds since 1970 (UTC) */
    int64_t age_ms;           /* write after free: how long the block had been free */
    uint64_t address;         /* the pointer FMOD was given */
    uint32_t count;           /* the entries in the arrays below (the first changed bytes) */
    uint32_t reserved;
    int32_t offsets[FH_EVENT_BYTES];
    uint8_t values[FH_EVENT_BYTES];   /* what each byte is */
    uint8_t expected[FH_EVENT_BYTES]; /* what it should be */
} moono_fmod_heap_event;

/* The allocator's counters (moono_fmod_heap_stats). "Held": what the private heap holds for FMOD,
 * live blocks and quarantined ones with their headers and canaries. */
typedef struct moono_fmod_heap_counters
{
    uint64_t live_bytes, live_blocks, peak_live_bytes;
    uint64_t quarantined_bytes, quarantined_blocks, peak_held_bytes;
    uint64_t allocs, frees, reallocs, bytes_allocated, bytes_freed;
    uint64_t checked_bytes;   /* quarantined bytes checked on their way out */
    uint64_t events, events_dropped;
    uint64_t quarantine_limit;
} moono_fmod_heap_counters;

/* Once, before FMOD_Memory_Initialize is given the three callbacks: the private heap and the
 * quarantine's size in bytes (0: no quarantine, blocks are freed at once). 1 when ready. */
FH_EXPORT int moono_fmod_heap_init(uint64_t quarantine_bytes);

/* FMOD_MEMORY_ALLOC_CALLBACK, FMOD_MEMORY_REALLOC_CALLBACK and FMOD_MEMORY_FREE_CALLBACK (F_CALL
 * is the one x64 convention). Any thread. */
FH_EXPORT void *moono_fmod_alloc(unsigned int size, unsigned int type, const char *sourcestr);
FH_EXPORT void *moono_fmod_realloc(void *ptr, unsigned int size, unsigned int type, const char *sourcestr);
FH_EXPORT void moono_fmod_free(void *ptr, unsigned int type, const char *sourcestr);

/* The counters, as of now (each read on its own). */
FH_EXPORT void moono_fmod_heap_stats(moono_fmod_heap_counters *out);

/* Takes up to `most` events found since the last call, oldest first; how many it took. */
FH_EXPORT int moono_fmod_heap_events(moono_fmod_heap_event *out, int most);

/* Moves every quarantined block out (checking it) and frees it: an allocation that failed does
 * it before trying again; the tests too. */
FH_EXPORT void moono_fmod_heap_flush(void);

#ifdef __cplusplus
}
#endif

#endif
