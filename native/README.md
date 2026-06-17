# libkraken for macOS arm64

Rebuild of the native Oodle (Kraken) library used by WolvenKit —
**decompression and compression**.

## Why

The `WolvenKit.CLI` 8.18.0 NuGet package ships a `libkraken.dylib` that is
**x86_64 only**, with **mangled C++ symbols** (`__Z17Kraken_Decompress...`).
WolvenKit's .NET P/Invoke (`[DllImport("kraken")]`) therefore cannot load it on
Apple Silicon — any `cp77tools` command that touches compression crashes:

- in native arm64: `DllNotFoundException` (incompatible architecture);
- forced into x86_64 under Rosetta: `EntryPointNotFoundException` (mangled symbols).

This rebuilt `libkraken.dylib` is **native arm64** and exports
`Kraken_Decompress` and `Kraken_Compress` as **`extern "C"`** — the P/Invoke then
loads them correctly.

## Source

`ooz-rarten/` = a clone of https://github.com/rarten/ooz (a fork of powzix/ooz).
Open-source Kraken / Mermaid / Selkie / Leviathan / LZNA / Bitknit codec — native
**decompressor and** compressor, with no proprietary dependency (the game's Oodle
DLL is not required).

## Modifications to the vendored source

- **`ooz-rarten/sse2neon.h`** — added (https://github.com/DLTcollab/sse2neon).
  Maps x86 SSE intrinsics to NEON → makes the arm64 build possible.
- **`ooz-rarten/stdafx.h`** — `#include <xmmintrin.h>` replaced by a conditional
  include of `sse2neon.h` on arm64 (the original x86 path is preserved).
- **`ooz-rarten/kraken.cpp`** — `Kraken_Decompress` made `extern "C"`; `main()`
  (the `ooz` CLI tool) wrapped in `#if 0` (irrelevant for a library).
- **`ooz-rarten/compr_leviathan.cpp`** — `std::auto_ptr` (removed in C++17)
  replaced with `std::unique_ptr<uint8[]>`.
- **`ooz-rarten/kraken_compress.cpp`** — *added file*. `extern "C" Kraken_Compress`
  wrapper delegating to `CompressBlock` (Kraken codec).

## Build

```sh
./build-libkraken.sh
```

Produces `build/libkraken.dylib` (Mach-O arm64), exporting `Kraken_Decompress`
and `Kraken_Compress`.

## Deploy

Copy the dylib to the RID location expected by .NET, next to `WolvenKit.CLI.dll`:

```
<pkg>/tools/net8.0/any/runtimes/osx-arm64/native/libkraken.dylib
```

where `<pkg>` is e.g. `~/.dotnet/tools/.store/wolvenkit.cli/<version>/wolvenkit.cli/<version>`.
The broken x86_64 binary in `tools/net8.0/any/` is left untouched: the .NET resolver
picks `runtimes/osx-arm64/native/` first for an arm64 process.

## Validated

- Round-trip `cp77tools oodle compress` + `oodle decompress`: 12,200 B → 106 B →
  12,200 B, **identical SHA-256**.
- `cp77tools pack` produces a valid `.archive` (native Kraken compression).

## Limits

- **Textures / audio** out of scope: `texconv` (textures) and Wwise audio depend on
  Windows native binaries.
- The `rarten/ooz` compressor is not guaranteed 100% byte-identical to Oodle for
  very small Mermaid/Selkie blocks — no impact for Cyberpunk, the game decodes any
  valid Kraken stream.