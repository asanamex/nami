cmake_minimum_required(VERSION 3.20)
project(NamiNative VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_compile_options(-Wall -Wextra)

# ---------------------------------------------------------------------------
# Nami native components (Windows x64 first).
#
#   nami_loader.dll  - injected into the game process; waits for Mono, then
#                      hosts the bundled CoreCLR and calls Nami.Runtime.Boot.
#   nami_boot.exe    - launcher: starts the game suspended, injects the loader
#                      via a remote thread, resumes.
# ---------------------------------------------------------------------------

add_library(nami_core STATIC
  core/runtime_host.cpp
  core/bootguard.cpp
)
target_include_directories(nami_core PUBLIC core)

add_library(nami_loader SHARED
  loader/loader_main.cpp
  loader/loader_exports.cpp
  loader/inex_bootstrap.cpp
  loader/tide_pump.cpp
  loader/tide_ops.cpp
  loader/tide_member_cache.cpp
  loader/tide_objects.cpp
  loader/tide_il2cpp.cpp
  loader/tide_il2cpp_ops.cpp
  loader/tide_il2cpp_patch.cpp
  loader/native_stub.cpp
)
set_target_properties(nami_loader PROPERTIES
  OUTPUT_NAME "nami_loader"
  PREFIX ""
)
# Fully static link (libgcc/libstdc++/libwinpthread) so the injected DLL has NO
# external DLL dependencies - the game process cannot resolve MinGW runtime DLLs.
target_link_options(nami_loader PRIVATE -static -static-libgcc -static-libstdc++)
target_link_libraries(nami_loader PRIVATE nami_core)
target_include_directories(nami_loader PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

add_executable(nami_boot
  injector/injector_main.cpp
  injector/injector_exe.cpp
)
target_link_libraries(nami_boot PRIVATE nami_core)
target_link_options(nami_boot PRIVATE -municode)

# Smoke test: prove the toolchain/link works, version macros are sane, and the
# dispatch-stub detour machinery (native_stub.cpp + the shared tide toolkit) is correct.
add_executable(nami_smoke
  smoke/smoke_main.cpp
  loader/native_stub.cpp
  loader/tide_pump.cpp
)
target_link_libraries(nami_smoke PRIVATE nami_core)
target_include_directories(nami_smoke PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
# Static CRT link (like nami_loader): ctest may run with no MinGW bin dir on PATH,
# and the smoke exe must not depend on libstdc++-6.dll / libgcc_s_seh-1.dll.
target_link_options(nami_smoke PRIVATE -static -static-libgcc -static-libstdc++)

enable_testing()
add_test(NAME smoke COMMAND nami_smoke)
