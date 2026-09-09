#include "tide_member_cache.h"

#include <windows.h>

#include <cstring>
#include <cstdlib>
#include <new>

namespace nami::tide {

// ---------------------------------------------------------------------------
// Open-addressing hash map (linear probing), process-lifetime. Both Tide
// backends call it from the game main thread only; the SRWLOCK guards
// initialization/growth and future-proof correctness.
// ---------------------------------------------------------------------------

namespace {

SRWLOCK g_cache_lock = SRWLOCK_INIT;

// Pack four NUL-separated key strings into one buffer: "s1\0s2\0s3\0s4\0"
// (null parts are omitted; the terminator still lands, so "a",null → "a\0\0").
int PackKeys(char* dst, int cap, const char* s1, const char* s2, const char* s3,
             const char* s4) {
    int off = 0;
    const char* parts[4] = {s1, s2, s3, s4};
    for (const char* p : parts) {
        if (p == nullptr) {
            p = "";
        }
        int len = static_cast<int>(std::strlen(p));
        if (off + len + 1 > cap) {
            len = cap - off - 1;
            if (len < 0) {
                break;
            }
        }
        std::memcpy(dst + off, p, static_cast<size_t>(len));
        off += len;
        dst[off++] = '\0';
    }
    return off;
}

constexpr int kMaxKeyBytes = 640;  // 4 x 160-byte name buffers + slack

}  // namespace

uint64_t MemberCache::HashMix(uint64_t h, uint64_t v) {
    h ^= v + 0x9e3779b97f4a7c15ULL + (h << 6) + (h >> 2);
    return h;
}

uint64_t MemberCache::HashString(uint64_t h, const char* s) {
    if (s == nullptr) {
        return HashMix(h, 0x1234567);
    }
    uint64_t x = h;
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(s); *p; ++p) {
        x = (x ^ *p) * 0x100000001b3ULL;
    }
    return x;
}

MemberCache::~MemberCache() {
    for (int i = 0; i < capacity_; i++) {
        if (table_[i].occupied) {
            std::free(table_[i].keys);
        }
    }
    std::free(table_);
}

MemberCache::Entry* MemberCache::FindSlot(uint64_t hash, uint64_t kind_tag, uint64_t k1,
                                          uint64_t k2, const char* s1, const char* s2,
                                          const char* s3, const char* s4) const {
    if (capacity_ == 0) {
        return nullptr;
    }
    const uint64_t mask = static_cast<uint64_t>(capacity_ - 1);
    uint64_t idx = hash & mask;
    while (true) {
        Entry& e = table_[idx];
        if (!e.occupied) {
            return nullptr;
        }
        if (e.hash == hash && e.kind_tag == kind_tag && e.k1 == k1 && e.k2 == k2 &&
            KeysEqual(e, s1, s2, s3, s4)) {
            return &e;
        }
        idx = (idx + 1) & mask;
    }
}

bool MemberCache::KeysEqual(const Entry& e, const char* s1, const char* s2, const char* s3,
                            const char* s4) const {
    const char* parts[4] = {s1, s2, s3, s4};
    const char* p = e.keys;
    for (const char* want : parts) {
        if (want == nullptr) {
            want = "";
        }
        const size_t len = std::strlen(want);
        if (std::memcmp(p, want, len) != 0 || p[len] != '\0') {
            return false;
        }
        p += len + 1;
    }
    return true;
}

void MemberCache::Grow() {
    const int old_cap = capacity_;
    Entry* old_table = table_;
    const int new_cap = old_cap == 0 ? 256 : old_cap * 2;
    table_ = static_cast<Entry*>(std::calloc(static_cast<size_t>(new_cap), sizeof(Entry)));
    if (table_ == nullptr) {
        table_ = old_table;  // OOM: keep the old table, stop growing
        return;
    }
    capacity_ = new_cap;
    count_ = 0;
    for (int i = 0; i < old_cap; i++) {
        Entry& e = old_table[i];
        if (!e.occupied) {
            continue;
        }
        // Re-insert with the stored hash. FindSlot needs the key strings for
        // collision verification, but rehashing uses hash-only placement on a
        // fresh table; a full-table collision chain would be wrong, so keep a
        // conservative load factor instead (Grow is called at 3/4 full).
        uint64_t idx = e.hash & static_cast<uint64_t>(capacity_ - 1);
        while (table_[idx].occupied) {
            idx = (idx + 1) & static_cast<uint64_t>(capacity_ - 1);
        }
        table_[idx] = e;
        count_++;
    }
    std::free(old_table);
}

void MemberCache::Insert(uint64_t hash, uint64_t kind_tag, uint64_t k1, uint64_t k2,
                         const char* s1, const char* s2, const char* s3, const char* s4,
                         void* value, bool found) {
    if (capacity_ == 0 || count_ * 4 >= capacity_ * 3) {
        Grow();
        if (capacity_ == 0) {
            return;  // OOM: cache disabled; resolve uncached next time
        }
    }
    const uint64_t mask = static_cast<uint64_t>(capacity_ - 1);
    uint64_t idx = hash & mask;
    while (table_[idx].occupied) {
        idx = (idx + 1) & mask;
    }
    Entry& e = table_[idx];
    e.hash = hash;
    e.kind_tag = kind_tag;
    e.k1 = k1;
    e.k2 = k2;
    e.keys = static_cast<char*>(std::malloc(kMaxKeyBytes));
    if (e.keys == nullptr) {
        e.occupied = false;
        return;  // OOM: entry dropped; next call resolves uncached
    }
    PackKeys(e.keys, kMaxKeyBytes, s1, s2, s3, s4);
    e.value = value;
    e.found = found;
    e.last_used = ++clock_;
    e.occupied = true;
    count_++;
}

void* MemberCache::LookupOrResolve(uint64_t kind_tag, uint64_t k1, uint64_t k2,
                                   const char* s1, const char* s2, const char* s3,
                                   const char* s4, ResolveFn fn, void* user, bool* found) {
    const uint64_t hash =
        HashMix(HashMix(HashString(HashString(0x515, s1), s2), HashString(0x777, s3)),
                HashString(0x999, s4) ^ HashMix(kind_tag, k1 ^ (k2 * 0x2545F4914F6CDD1DULL)));

    AcquireSRWLockExclusive(&g_cache_lock);

    if (Entry* hit = FindSlot(hash, kind_tag, k1, k2, s1, s2, s3, s4)) {
        hit->last_used = ++clock_;
        void* value = hit->value;
        *found = hit->found;
        ReleaseSRWLockExclusive(&g_cache_lock);
        return value;
    }

    // Miss: run the resolver under the lock. Resolution touches only process-
    // lifetime metadata and never calls back into the cache (no recursion).
    ResolveResult r = fn(user);

    if (count_ * 4 >= capacity_ * 3) {
        Grow();
    }
    Insert(hash, kind_tag, k1, k2, s1, s2, s3, s4, r.value, r.found);

    ReleaseSRWLockExclusive(&g_cache_lock);
    *found = r.found;
    return r.value;
}

// Process-lifetime cache instance shared by both backends.
void* MemberCacheLookup(uint64_t kind_tag, uint64_t k1, uint64_t k2,
                        const char* s1, const char* s2, const char* s3, const char* s4,
                        MemberCache::ResolveFn fn, void* user, bool* found) {
    static MemberCache g_cache;
    return g_cache.LookupOrResolve(kind_tag, k1, k2, s1, s2, s3, s4, fn, user, found);
}

}  // namespace nami::tide
