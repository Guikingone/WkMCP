# libkraken pour macOS arm64

Reconstruction de la bibliothèque native Oodle (Kraken) utilisée par WolvenKit —
**décompression et compression**.

## Pourquoi

Le package NuGet `WolvenKit.CLI` 8.18.0 livre une `libkraken.dylib` **x86_64
uniquement**, et dont les symboles sont **manglés C++** (`__Z17Kraken_Decompress...`).
Le P/Invoke .NET de WolvenKit (`[DllImport("kraken")]`) ne peut donc pas la
charger sur Apple Silicon — toute commande de `cp77tools` touchant à la
compression plante :

- en natif arm64 : `DllNotFoundException` (architecture incompatible) ;
- forcé en x86_64 sous Rosetta : `EntryPointNotFoundException` (symboles manglés).

Cette `libkraken.dylib` reconstruite est en **arm64 natif** et exporte
`Kraken_Decompress` et `Kraken_Compress` en **`extern "C"`** — le P/Invoke les
charge alors correctement.

## Source

`ooz-rarten/` = clone de https://github.com/rarten/ooz (fork de powzix/ooz).
Codec Kraken / Mermaid / Selkie / Leviathan / LZNA / Bitknit open-source —
décompresseur **et** compresseur natifs, sans dépendance propriétaire (la DLL
Oodle du jeu n'est pas nécessaire).

## Modifications apportées à la source vendorée

- **`ooz-rarten/sse2neon.h`** — ajouté (https://github.com/DLTcollab/sse2neon).
  Mappe les intrinsics SSE x86 vers NEON → rend possible le build arm64.
- **`ooz-rarten/stdafx.h`** — `#include <xmmintrin.h>` remplacé par un include
  conditionnel de `sse2neon.h` sur arm64 (le chemin x86 d'origine est conservé).
- **`ooz-rarten/kraken.cpp`** — `Kraken_Decompress` passé en `extern "C"` ;
  `main()` (l'outil CLI `ooz`) entouré de `#if 0` (non pertinent pour une lib).
- **`ooz-rarten/compr_leviathan.cpp`** — `std::auto_ptr` (supprimé en C++17)
  remplacé par `std::unique_ptr<uint8[]>`.
- **`ooz-rarten/kraken_compress.cpp`** — *fichier ajouté*. Wrapper
  `extern "C" Kraken_Compress` délégant à `CompressBlock` (codec Kraken).

## Build

```sh
./build-libkraken.sh
```

Produit `build/libkraken.dylib` (Mach-O arm64), exportant `Kraken_Decompress`
et `Kraken_Compress`.

## Déploiement

Copier la dylib à l'emplacement RID attendu par .NET, à côté de `WolvenKit.CLI.dll` :

```
<pkg>/tools/net8.0/any/runtimes/osx-arm64/native/libkraken.dylib
```

où `<pkg>` est p. ex. `~/.dotnet/tools/.store/wolvenkit.cli/<version>/wolvenkit.cli/<version>`.
Le binaire x86_64 cassé dans `tools/net8.0/any/` est laissé intact : le résolveur
.NET choisit `runtimes/osx-arm64/native/` en premier pour un process arm64.

## Validé

- Aller-retour `cp77tools oodle compress` + `oodle decompress` : 12 200 o → 106 o
  → 12 200 o, **SHA-256 identique**.
- `cp77tools pack` produit une `.archive` valide (compression Kraken native).

## Limites

- **Textures / audio** hors périmètre : `texconv` (textures) et l'audio Wwise
  dépendent de binaires natifs Windows.
- Le compresseur de `rarten/ooz` n'est pas garanti 100 % byte-identique à Oodle
  pour les très petits blocs Mermaid/Selkie — sans incidence pour Cyberpunk, le
  jeu décodant n'importe quel flux Kraken valide.
