cmake_minimum_required(VERSION 3.20)
project(DirectXTexShim LANGUAGES CXX)

# The pinned DirectXTex source (see directxtex.pin). Fetched at configure time by commit hash so the
# build is reproducible and nothing third-party lives in this repository.
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/directxtex.pin" PIN_LINES REGEX "^[a-z]+=")
foreach(line IN LISTS PIN_LINES)
  string(REGEX MATCH "^([a-z]+)=(.*)$" _ "${line}")
  set(DXTEX_PIN_${CMAKE_MATCH_1} "${CMAKE_MATCH_2}")
endforeach()

include(FetchContent)
FetchContent_Declare(
  directxtex
  GIT_REPOSITORY https://github.com/microsoft/DirectXTex.git
  GIT_TAG ${DXTEX_PIN_commit}
  SOURCE_SUBDIR do-not-add-subdirectory   # download only; the sources are compiled below
)
FetchContent_MakeAvailable(directxtex)

set(DXTEX ${directxtex_SOURCE_DIR}/DirectXTex)
set(DXTEX_SOURCES
  BC.cpp BC4BC5.cpp BC6HBC7.cpp DirectXTexCompress.cpp DirectXTexConvert.cpp DirectXTexDDS.cpp
  DirectXTexFlipRotate.cpp DirectXTexHDR.cpp DirectXTexImage.cpp DirectXTexMipMaps.cpp DirectXTexMisc.cpp
  DirectXTexNormalMaps.cpp DirectXTexPMAlpha.cpp DirectXTexResize.cpp DirectXTexTGA.cpp DirectXTexUtil.cpp
  DirectXTexWIC.cpp)
list(TRANSFORM DXTEX_SOURCES PREPEND "${DXTEX}/")

add_library(DirectXTexShim SHARED src/shim.cpp ${DXTEX_SOURCES})
target_include_directories(DirectXTexShim PRIVATE ${DXTEX})
set_target_properties(DirectXTexShim PROPERTIES CXX_STANDARD 17 CXX_STANDARD_REQUIRED ON
  MSVC_RUNTIME_LIBRARY "MultiThreaded")   # static CRT: the DLL needs no Visual C++ redistributable

# Same switches as DirectXTexNet's own DirectXTex build (DirectXTex_Desktop_2022.vcxproj, Release|x64):
# /O2, /fp:fast, SSE2 (the x64 default), the same defines. OpenMP is left out: it would only add a
# dependency on vcomp140.dll and TEX_COMPRESS_PARALLEL then simply runs serially with identical output.
target_compile_definitions(DirectXTexShim PRIVATE
  _UNICODE UNICODE NDEBUG _WIN7_PLATFORM_UPDATE _WIN32_WINNT=0x0601 _CRT_STDIO_ARBITRARY_WIDE_SPECIFIERS
  DXS_SOURCE_TAG="${DXTEX_PIN_tag}" DXS_SOURCE_COMMIT="${DXTEX_PIN_commit}")
target_compile_options(DirectXTexShim PRIVATE /O2 /fp:fast /Zc:twoPhase- /Zc:__cplusplus /utf-8 /W3)
target_link_libraries(DirectXTexShim PRIVATE ole32 windowscodecs uuid)
set_target_properties(DirectXTexShim PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-x64")
