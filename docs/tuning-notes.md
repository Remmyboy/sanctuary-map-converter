# Tuning notes

Dials whose current values are informed first guesses, not measured truths.
Each was calibrated against the shipped maps' numbers or the corpus, but none
has been iterated against the screen more than once. When a converted map
looks wrong in a way that is even, global and non-crashing, suspect this list
first. Values live where the **file** column says; change them there and both
pipelines pick them up (everything below `src/` is shared engine code).

## What the engine code settles

Checked 2026-09-28 against the decompiled Playtest build 25474094
(`ilspycmd` on `Trebuchet.dll`, plus the shipped Lua under `engine/LJ/lua`).
This pins down what each map field *means* to the game; it does not pick
our values, which are still calibrated against the shipped maps. The terrain
shader itself is compiled, so for its inputs the engine shows the wiring
and not the maths.

| field | what the engine does with it | where |
|---|---|---|
| `sunDA`, `sunRA` | sun light rotation `Quaternion.Euler(sunDA, sunRA, 0)`, exactly as assumed below | `MapManager.SetLightToMap` |
| `sunIntensity` | HDRP light intensity in the directional light's native unit, **lux** | same |
| `sunTint`, `sunTemperature` | HDRP colour + colour temperature (K) | same |
| `skylightIntensity` | the sky light is switched **off below 1** (lux); every shipped map and ours use 0 | same |
| `fogAttenuationDistance` | HDRP Fog `meanFreePath`, the 1/e distance | `MapManager.SetMapVolume` |
| `fogBaseHeight`, `fogMaximumHeight` | HDRP Fog base / maximum height | same |
| `fogMaximumDistance` | HDRP Fog `maxFogDistance`; every shipped map uses 1500 | same |
| `exposure`, `exposureCompensation` | HDRP Exposure `fixedExposure` (EV) and compensation | same |
| `background*`, `heightFog*`, `linearFog*` | a custom `PlayableAreaFog` volume, rect from `areas["PlayableArea"]` | same |
| `windSpeed`, `windDirection` | foliage wind only: strength `lerp(0, 4, windSpeed)` so the field is **0–1**; direction in degrees about Y. Water waves use the separate `waterWind*` fields | `MapManager.UpdateSceneWind` |
| `waterDepth` | **rendering only**: the water/shore shader's `_WaterLevel.y`. Nothing in gameplay reads it; the AI path map classifies water from `terrain − waterLevel` with fixed thresholds (≥ −1 m beach, ≥ −24.9 m seabed, deeper is abyss and blocked) | `MapManager.UpdateSceneWater`, `AI/AIMarkerGenerator.lua` |
| `heightTransition` | **squared** before it reaches the shader (`_HeightTransition`); shipped 1.5–2.5, engine default 0.5 | `MapManager.SetTerrainMaterialValues` |
| `fadeDistance`, `fadeStartDistance` | passed straight to the terrain shader | same |
| stratum `tileSize` | shader scale is `1 / tileSize`, so the value is **metres per repeat** | `MapManager.SetMaterialStratumValues` |
| stratum `diffuseRemap` | `_DiffuseRemapScale`, a per-channel multiplier on the albedo | same |
| stratum `farColorRemap` | `_DiffuseRemapScale_Far`, converted to linear first. What alpha does is in the shader; shipped rock layers use `(1,1,1,0)` as we do | same |
| stratum `maskRemapMin` / `maskRemapMax` | `_MaskMapRemapOffset` / `_MaskMapRemapScale`: HDRP's `offset + mask × scale` by their names, so "max" would be a scale. **But scaling `maskRemapMax.w` did not change smoothness in game** (Sung Island A/B, 2026-09-28), so do not rely on it: change the mask pixels instead. Identity is `(0,0,0,0)` / `(1,1,1,1)`, which we write | same |
| stratum mask texture | `_LayerHasMask.x` is 1 when the layer has one, 0 otherwise | same |
| stratum textures (all three) | bound with plain `SetTexture`, so the loader's blue/red flag is lost; 24-bit BGR DDS renders channel-swapped (see `src/Dxt.cs`) | `MapManager.UpdateStratumLayerMaterials` |

The engine's own defaults (`SanMap.Clear()`) are what a map gets for a field
it omits, and they are not the shipped values: sun 15000 lux / 6300 K,
exposure 12 + 2.5, fog 200 m mean free path, `heightTransition` 0.5,
`waterDepth` 0. Third-party generators that start from these
(tgorzney/Sanctuary_MapGen does) produce maps that look nothing like the
developers' own.

The developers' own generator (the one behind the shipped `~TEAM`/`~FFA` maps;
source in Bentu999's Triple-S repo, `Sanctuary-Map-Generation-develop/`)
computes none of the lighting, fog, water or stratum values: each biome is one
of the four hand-made maps' settings copied verbatim. It writes `tint_colors`
as a flat 127 in every channel, and `tint_geometry` as a detail normal
(noise-roughened minus smooth terrain normals, not the terrain's own). White
Desert ships a flat `tint_geometry`, as we do.

## The one assumption, not a number

| what | current | file | how to falsify |
|---|---|---|---|
| Sun azimuth convention | light rotation `Euler(sunDA, sunRA, 0)` — **confirmed** in the engine (`MapManager.SetLightToMap`) — so direction-to-sun = `(-cos DA sin RA, sin DA, -cos DA cos RA)`. What remains an assumption is only our side: the z-negation of the FA sun vector that goes with the flipped terrain | `src/ScMapEnvironment.cs` `ScSunAngles` | Loki (azimuth +70°) and Seton's (−48°) are deployed side by side: their shadows must fall on opposite sides. If ridges are lit from the wrong side vs the FA original, flip the sign of `sunRA` |

## Lighting (`src/ScMapEnvironment.cs`)

| dial | current | why this value | watch for |
|---|---|---|---|
| `sunDA` clamp | 15–30° | shipped band exactly | flat noon look = raise ceiling; everything in shadow = raise floor |
| Sun temperature | `6500 − 5000·log2(r/b)`, clamp 5000–9800 K | neutral white → 6500; shipped band as clamp | most FA maps are warm and land near 5000 — if everything reads orange, soften the 5000 multiplier |
| Intensity pivot `ScLightPivot` | 1.94, clamp 25000–60000 lux | corpus median of `lightingMultiplier × sun luminance`; median map keeps shipped 60000 | dark maps (White Fire: 34k lux) — if they read murky rather than moody, raise the 25000 floor |
| Fog attenuation | source fog band × 0.63, clamp 24–500 m | the field is HDRP's `meanFreePath` (confirmed), the 1/e distance; 0.63 maps SupCom's saturation distance onto it | Canis River lands at 113 m — the mistiest deployed test. Too thick → raise the 0.63; fog invisible everywhere → lower it |

## Tint noise (`src/ScMapEnvironment.cs` tables, loop in `src/MapGen.cs` WriteTintColors)

| dial | current | watch for |
|---|---|---|
| `TintNoiseLum` per role | veg .09, dirt .07, mud .06, sand/gravel .05, snow .04, rock .03 | speckle at commander zoom = too high; ground still reads flat = too low |
| `TintNoiseWarm` per role | veg .05, dirt/sand .04, mud/gravel .02, rock/snow .01 | green/magenta cast on open ground = too high |
| Mottle scales | 28 m (3 oct) mixed 0.7 with 9 m (2 oct) at 0.3; warm noise 44 m | fixed metres by design — change the metres, not to map-relative |
| Pre-existing wash | broad 0.10 @ 0.55×map, fine 0.055 @ 0.11×map, height lift 0.07 | untouched by this work; listed because it stacks with the mottle |

## Wet shoreline (`src/MapGen.cs` WriteTintColors)

| dial | current | watch for |
|---|---|---|
| Max darkening | 13% | reads as a dirty ring rather than damp ground = too strong |
| Band height | 2.5 m above waterline | band ends mid-beach = raise; climbs hills = lower. Height-based by design (beach wide, cliff thin) — a horizontal distance transform is the upgrade path if height ever misbehaves |

## Macro overlay bake (`src/ScMapEnvironment.cs` SampleMacro / AdoptScMacro)

| dial | current | why |
|---|---|---|
| Albedo stand-in | 0.37 / 0.35 / 0.32 per channel | the default `diffuseRemap` mid tone (the remap is a plain per-channel multiplier, confirmed); the true blend needs the albedo under each texel, which the bake cannot know |
| Factor clamp | 0.55–1.45 | keeps a near-black overlay pixel (lava sets) from deleting the ground |
| Invisible-skip | mean alpha < 2/255 | below this the overlay never showed in FA either |
| Scale sanity | 8–4096 m per repeat | corpus min is a degenerate 1.0 on a few maps |

## Mask smoothness per role (`src/ScMapEnvironment.cs` RoleSmoothness)

Banded around the shipped mean of 36.4 — the dial with the wet-plastic
history. Judge on Seton's mud flats vs its rock. The values themselves are
not something the engine can settle. Scaling through `maskRemapMax.w`
instead of the mask pixels was tried and did nothing in game.

**CC0 mode uses the same targets.** The CC0 pack's masks carry each material's
real smoothness (from ambientCG roughness): 63–229 across the 30 materials,
Sung Island's grass at 115. In game that read as the whole map being wet. On
export each CC0 mask's alpha is now scaled so its mean lands on the role
target below (`MapGen.ScaleDxt5AlphaMean`, role from the CC0 material name),
keeping the material's own variation. Confirmed by eye on Sung Island.

| role | value | | role | value |
|---|---|---|---|---|
| mud | 55 | | sand | 30 |
| snow | 50 | | dirt | 27 |
| rock | 45 | | veg | 24 |
| gravel | 38 | | | |

## Wreckage import (`src/ScWrecks.cs`)

| dial | current | watch for |
|---|---|---|
| `ScWreckMinMass` | 30 (walls cost 2, a T1 tank 56) | reclaim fields feeling empty = corpus maps lean on mid-value debris just under it |
| Size ladder | hitbox area ≤ 0.5 / 2.5 / 9 / 30 / else, aspect > 1.3 splits the two mid meshes | wrecks visually too big or small for what they were — judge on The_Dark_Heart's debris field |
| Economy | every SSS wreck blueprint is worth 100 alloys / 10 s (dev placeholder values) | positions and silhouettes are faithful, per-wreck value is not — revisit when the devs tune their wreck blueprints or ship a wider set |
| Harvesting itself | **reclaim shipped on 24 Sep 2026, but not for map props.** The `RECLAIM` order (`common/orders/definitions/reclaim.lua`) works on Lua host props, which in practice means wrecks spawned by dying units (`CreateWreckageFromUnit`); those take `economy.harvest` / `harvestTime` from their template and **self-delete after 180 s** (`host/props/propsClasses/wreckageClass.lua`). Props placed by the map file load as vegetation instances instead: `VegetationInstance.reclaimTime` / `isWreckage` and the `PropHarvest` component are declared but nothing reads them | wrecks and props we place are still visual and blocking only. If the devs route map props through the reclaim path, check whether they inherit the 180 s timeout — that would clear a whole map's debris field three minutes in |

## Playable-area guards (`src/ScMapEnvironment.cs` ScPlayableArea)

Behavioral rather than cosmetic; less likely to need touching, but if a map's
border comes out wrong, these are the gates: ≥ 16 m a side, ≥ 25% of map
area, every spawn inside with 1 m slack.

## Pre-existing dials already known uncertain

Inherited, documented here so the list is in one place:

| dial | current | where | note |
|---|---|---|---|
| Stratum tile scale | carried 1:1 from SupCom `textureScale` | both stratum builders | the Sanctuary half is settled: `tileSize` is metres per repeat (the shader gets `1 / tileSize`). 1:1 is right if SupCom's value is also world units per repeat — the half still unverified. "If the ground reads too coarse or too fine this is the number to change" |
| `tileSizeFar` | `tileSize × 6` | same | shipped far/near ratios run 1.6–13.8, mostly 3.2–6.4, so ×6 sits inside the band |
| `-Cc0TileMult` | 2.5 | ConvertOptions / Convert-ScMap.ps1 | photo features are cm-scale vs FA's 4–10 m repeat |
| `-Cc0NormalScale` | 0.45 | same | photogrammetry normals are strong |
| `fogMaximumDistance` | 1500 (was 1800) | converter JSON constants | HDRP `maxFogDistance`; every shipped map, hand-made and generated, uses 1500, and nothing justified 1800, so it now matches. Settled |
| `windDirection` | 160 (shipped: 100) | same | foliage sway direction only. The hand-made maps use 100, the developers' generated `~TEAM`/`~FFA` maps use 160, so both are shipped values — nothing to fix |
| `waterDepth` clamp | 1–8 m | same | source deep-water elevation, clamped. Rendering only (water/shore shader), so it cannot break pathing; shipped maps use 1–2 |
| Prop scale clamp | 0.5–2.0, tree groups ×1.35 | Converter props section | tree-group size is one number standing in for a whole mesh |
| `-MaxProps` | 20000 | ConvertOptions / Convert-ScMap.ps1 | every placed prop carries harvest values (5 alloys + 20 plasma, from the shipped blueprints). Reclaim has shipped but does not reach map props yet (see Wreckage import), so today thinning only changes looks; once it does, thinning the densest maps also trims their total reclaim |
