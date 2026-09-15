# PatcherFixtures

Fixture matrix for [`ResoniteModding/BepisLoader` PR #39](https://github.com/ResoniteModding/BepisLoader/pull/39) (`inheritPatcherTypes`): the new loader discovers inherited `BasePatcher` types, derived patcher attributes, and renamed assemblies; the old loader misses them.

## Run

```powershell
# Everything: build both loaders, fetch mods, run the full matrix (old + new).
dotnet run --file tools/patcher-env.cs
# One case, new loader only, reusing existing clones:
dotnet run --file tools/patcher-env.cs -- --only new --case UniExample --skip-loader-build --skip-mods
# Tester alone against a profile (default: Gale `Test` profile):
dotnet run --file tools/patcher-tests.cs -- --expect new
```

`--expect old` flips the new-only cases to assert absence.

## Cases

New-only (the old loader misses them): `DerivedAttributePatcher`, `FrameworkPatcher_MiniFramework`, `UniExample` (a consumer of the real `UniModFramework`, built from a pinned clone and auto-skipped until `patcher-env.cs` builds it), `zzIndirectBase_IndirectPatcher` (renamed base file).

Shared behavior: `Base`, `DirectPatcher_IndirectPatcher`, `IndirectBase_IndirectPatcher`, `IndirectPatcherAlone`, `PlainPlugin`, `EarlyPatcher_LateBase`, `Mods` (Clover 3.0.1, DeleagateRefEditing 1.0.1, Shim 0.9.3; asserts loader-phase lines only).

## Results (old run 2026-09-15 18:59 UTC, new run 2026-09-15 19:05 UTC)

| Case | old loader (`d6daa2f1`) | new loader (`e8e40b55`) |
|---|---|---|
| Base | PASS | PASS |
| DirectPatcher_IndirectPatcher | PASS | PASS |
| IndirectBase_IndirectPatcher | PASS | PASS |
| DerivedAttributePatcher | PASS (failed to load) | PASS (loaded) |
| FrameworkPatcher_MiniFramework | PASS (failed to load) | PASS (loaded) |
| UniExample | PASS (failed to load) | PASS (loaded) |
| zzIndirectBase_IndirectPatcher | PASS (failed to load) | PASS (loaded) |
| IndirectPatcherAlone | PASS | PASS |
| PlainPlugin | PASS | PASS |
| EarlyPatcher_LateBase | PASS | PASS |
| Mods | PASS | PASS |

New-only rows: old-side PASS asserts the patcher failed to load (the bug); new-side PASS asserts it is loaded (the fix).

## Pins

Loader PR #39 (`git ls-remote`; `--loader-old/new` and `--loader-base` override), UniModFramework `main` @ `fb84fff9` (`--unimod-sha` overrides), Thunderstore mods (see `Mods` above). Stock bootstrap is restored after each run; each case gets 30 s (loader lines land in the first seconds; 20 s proven), then the game and Renderite processes are killed.

Created with assistance of AI.
