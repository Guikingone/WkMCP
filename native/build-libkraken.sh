#!/usr/bin/env bash
# Construit libkraken.dylib (arm64) pour macOS Apple Silicon.
#
# Le package NuGet WolvenKit.CLI ne fournit qu'une libkraken x86_64, et dont les
# symboles sont mangles C++ -- inutilisable par le P/Invoke .NET sur Apple Silicon.
# Cette reconstruction est en arm64 natif et exporte Kraken_Decompress ET
# Kraken_Compress en extern "C". Voir README.md pour le detail.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/ooz-rarten"
OUT="$HERE/build"
mkdir -p "$OUT"

echo "Compilation de libkraken.dylib (arm64, decompression + compression)..."
clang++ -std=c++17 -arch arm64 -O2 -w -dynamiclib \
  -install_name @rpath/libkraken.dylib \
  "$SRC/kraken.cpp" "$SRC/bitknit.cpp" "$SRC/lzna.cpp" \
  "$SRC/compress.cpp" "$SRC/compr_entropy.cpp" "$SRC/compr_kraken.cpp" \
  "$SRC/compr_leviathan.cpp" "$SRC/compr_match_finder.cpp" \
  "$SRC/compr_mermaid.cpp" "$SRC/compr_multiarray.cpp" "$SRC/compr_tans.cpp" \
  "$SRC/kraken_compress.cpp" \
  -o "$OUT/libkraken.dylib"

echo "OK: $OUT/libkraken.dylib"
file "$OUT/libkraken.dylib"
rc=0
for sym in _Kraken_Decompress _Kraken_Compress; do
  if nm -gU "$OUT/libkraken.dylib" | grep -q " $sym\$"; then
    echo "Symbole exporte: $sym (extern C) -- OK"
  else
    echo "ERREUR: $sym introuvable dans la dylib" >&2
    rc=1
  fi
done
exit $rc
