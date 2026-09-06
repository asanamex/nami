#include "core/nami_common.h"
#include "core/runtime_host.h"

#include <cstdio>

int main() {
    const bool version_ok = nami::version_string() == "0.1.0";
    std::printf("nami_smoke: %s (version=%s)\n", version_ok ? "PASS" : "FAIL",
                nami::version_string().c_str());
    return version_ok ? 0 : 1;
}
