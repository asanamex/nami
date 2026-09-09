#pragma once

#include <cstdint>

namespace nami::tide {

// ---------------------------------------------------------------------------
// Member resolution cache.
//
// Tide's typed ops resolve classes/fields/methods BY NAME on every request
// (mono_class_from_name / class_get_field_from_name / overload scoring). For a
// mod reading five fields per tick that is five identical lookups per tick,
// on the game main thread. Metadata in Mono and IL2CPP lives for the whole
// process (classes, methods, fields are never unloaded), so resolution results
// are cacheable for the loader's lifetime - including NOT-FOUND results
// (negative caching), so probing an optional member costs once, not per call.
//
// Key material per entry:
//   kind_tag  caller discriminator (class / method-plain / method-typed / field)
//   k1, k2    caller key integers (klass pointer, argc, arg-type mask, ...)
//   up to 4 NUL-terminated strings (assembly/ns/klass/member)
//
// Two typed-method entries with the same name+argc but different argument
// type masks get different keys (k2), so overload scoring results are cached
// per exact argument shape. All lookups run on the game main thread (both
// backends funnel through the drain); a lock is held anyway so a future
// multi-thread executor stays correct, and so resolve-under-lock is safe
// (nothing else takes this lock).
// ---------------------------------------------------------------------------
class MemberCache {
public:
    struct ResolveResult {
        void* value;
        bool found;
    };
    // Performs the actual (uncached) resolution. Runs under the cache lock.
    using ResolveFn = ResolveResult (*)(void* user);

    MemberCache() = default;
    ~MemberCache();
    MemberCache(const MemberCache&) = delete;
    MemberCache& operator=(const MemberCache&) = delete;

    // Returns the cached value (nullptr when a not-found result is cached) and
    // sets *found. On a miss, calls fn(user) and stores the result.
    void* LookupOrResolve(uint64_t kind_tag, uint64_t k1, uint64_t k2,
                          const char* s1, const char* s2, const char* s3,
                          const char* s4, ResolveFn fn, void* user, bool* found);

private:
    struct Entry {
        uint64_t hash;
        uint64_t kind_tag;
        uint64_t k1;
        uint64_t k2;
        char* keys;      // packed "s1\0s2\0s3\0s4\0" (only the non-null parts)
        void* value;
        bool found;
        uint64_t last_used;
        bool occupied;
    };

    Entry* table_ = nullptr;
    int capacity_ = 0;      // power of two
    int count_ = 0;
    uint64_t clock_ = 0;

    static uint64_t HashMix(uint64_t h, uint64_t v);
    static uint64_t HashString(uint64_t h, const char* s);
    bool KeysEqual(const Entry& e, const char* s1, const char* s2, const char* s3,
                   const char* s4) const;
    Entry* FindSlot(uint64_t hash, uint64_t kind_tag, uint64_t k1, uint64_t k2,
                    const char* s1, const char* s2, const char* s3, const char* s4) const;
    void Grow();
    void Insert(uint64_t hash, uint64_t kind_tag, uint64_t k1, uint64_t k2,
                const char* s1, const char* s2, const char* s3, const char* s4,
                void* value, bool found);
};

// Free-function front end over a process-lifetime cache instance. `fn` runs ONLY
// on a miss, under the cache lock - it must not call back into this cache
// (resolvers use the *_uncached lookups), and must only touch process-lifetime
// metadata. Mono and IL2CPP backends share the instance; kind_tag values are
// namespaced per backend so keys can never collide.
void* MemberCacheLookup(uint64_t kind_tag, uint64_t k1, uint64_t k2,
                        const char* s1, const char* s2, const char* s3, const char* s4,
                        MemberCache::ResolveFn fn, void* user, bool* found);

}  // namespace nami::tide
