# SCFA > Sanctuary Map Converter

Rebuilds **Supreme Commander: Forged Alliance** maps as native **Sanctuary:
Shattered Sun** maps - terrain, textures, markers, lighting, props and
wreckage - and checks each result against the game's own parsers before it
ships.

It ships as one self-contained Windows exe: no .NET install, no PowerShell, no
setup. Download it, run it, point it at your maps.

**289 of a 299-map corpus convert (97%).** Of the stock set specifically, all
51 convertible skirmish maps convert and validate against the game's own
parsers. What is refused is refused for a reason: campaign maps with no
skirmish spawns, one non-square map, and three that use a pre-Forged-Alliance
texture-block format.

---
## Quick start

**Download the [latest release](../../releases/latest)**, unzip it anywhere,
and run `SanctuaryMapConverter.exe`. It is a single self-contained Windows
build — no .NET install, no PowerShell, no setup — and it finds your Supreme
Commander and Sanctuary installs on its own. If it cannot (a portable copy, a
second drive, a network share), point it at them with the Browse buttons and
it remembers.

The window does two things: convert one Supreme Commander map, or convert
every map in a folder - then deploy them into the game. Conversion offers two
texture modes:

- **Original FA textures** — enabled when the app finds `env.scd` in *your*
  Forged Alliance install. The exe ships zero Gas Powered Games art; the
  textures come from the copy of the game you own, and the result is for
  **local play only**.
- **CC0 substitutes** — shareable, because nothing in the result is anyone
  else's art. Needs `data\texturepack\` beside the exe: download
  `texturepack.zip` from the release, or build it once yourself with
  `SanctuaryMapConverter.exe --tool build-texturepack`. It is 325 MB of
  [ambientCG](https://ambientcg.com) material, which is why it is a separate
  download rather than part of the repo.

**Restart the game after deploying.** The engine snapshots map files at load
and will not notice changes underneath it.

Headless verbs, for scripting a batch:

```
SanctuaryMapConverter.exe --convert "C:\...\Maps\SCMP_009" --cc0
SanctuaryMapConverter.exe --convert "C:\...\Maps\SCMP_009" --biome Winter --deploy
SanctuaryMapConverter.exe --validate "...\Maps\X\X.sanmap" --check-textures --lua
SanctuaryMapConverter.exe --check-deployed
```

---

## Building it yourself

```
dotnet publish app/SanctuaryMapConverter/SanctuaryMapConverter.csproj ^
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

`PublishSingleFile` cannot embed the WinForms native libraries, so the handful
of DLLs next to the exe in `publish\` are part of the build — ship the folder,
not just the .exe. The engine sources in `src/*.cs` compile straight into it,
and `docs\texture-map.csv` and `docs\unit-wrecks.csv` are copied to `data\`
automatically. Pushing a `v*` tag runs `.github/workflows/release.yml`, which
does all of the above and attaches the zip to a GitHub release.

The four validators run against the game's own code — Newtonsoft into
`EM.Map.SanMap`, and the engine's `json.lua` through KeraLua — so a map that
passes has been parsed by the same libraries the game will use on it.

One behavioural note for anyone extending the app: `MapGen` keeps its state in
statics, and a batch run converts many maps in one process. `EngineState.Reset()`
at the top of every run restores the compiled defaults; anything new that
drives `MapGen` must do the same.

---
## What transfers

Everything the source map is, short of decals:

|  |  |
|---|---|
| heightmap | copied byte-exact, zero-error round trip asserted |
| water | level copied; depth from the source's own deep-water elevation |
| Mass / Hydrocarbon markers | alloy spots |
| `ARMY_n` markers | spawns, one army each |
| playable area | the author's `AREA_1` rectangle, guarded (see below), centred in a terrain twice the map's size |
| border | authored around the map, as the shipped maps do: the edge terrain reflected outward, then warped and dissolved with distance so the engine mirrors bland ground rather than the map (see below) |
| lighting | sun azimuth/altitude, warmth, brightness and fog thickness from the source's lighting block, clamped to the shipped ranges; the biome (the GUI's Lighting biome, or `--biome`; default Tropical) fills in the rest |
| stratum textures | carried (default) or substituted with CC0 (option) |
| splat weights | the author's own masks, resampled to `heightmapResolution` |
| normal maps | the author's true normal **per layer** |
| macro overlay | the UpperStratum macrotexture, baked into `tint_colors` at its own repeat (source-texture mode only — the bake copies GPG pixels) |
| mask maps | per-role smoothness in source mode — mud glistens, rock sheds light, grass stays matte (CC0 mode already carries each material's real mask) |
| props | the author's placements — trees, groups, rocks — onto a biome-matched Sanctuary palette |
| wreckage | `WRECKAGE`-group wrecks as harvestable wreck props, size-matched onto the Playtest build's six wreck meshes; walls and sub-30-mass debris skipped (every wreck blueprint is worth the same placeholder 100 alloys, so a wall would be a goldmine). `docs/unit-wrecks.csv` carries each FA unit's mass and hitbox (regenerate with `tools\Measure-ScUnits.ps1`) |
| preview | drawn from the map's own textures, with numbered spawn badges in the palette the developers use |
| decals | parked; see below |

**The playable area is adopted only when it can be trusted.** 28 of 299 corpus
maps inset `AREA_1` to make an out-of-bounds border (bluelands, Dual Gap's dead
bands). But adaptive_corona writes `RECTANGLE(0,0,0,0)` and final_rush defines
a 50 m starting box that a script grows at run time, so a rectangle is adopted
only if it is at least 16 m a side, covers ≥ 25% of the map, and contains every
spawn. Anything else falls back to the full map.

**The map gets a border, because the engine never shows an edge.** Beyond the
terrain, Sanctuary's `InfiniteTerrain` builds eight more terrains - the
heightmap mirrored across each edge and flattened towards the mean height on
the far side - and the terrain shader carries the textures over them. There is
no map-file switch for it. A map that fills its whole terrain therefore
appears surrounded by eight reflected copies of itself, which is what an early
conversion looked like. The developers' own maps never fill their terrain:
all four hand-made ones (The Forge, White Desert, Two Step Shuffle, There Is
Time) centre the playable rectangle in a terrain twice its size, with a border
that continues the edge but carries none of the map's features - The Forge's
border matches its interior to 0.14 m one cell out and has diverged by 16 m
two hundred cells out. The converter does the same: the heightmap and splat
stay byte-exact in the centre, and the border is two things cross-faded. A
thin band of the map's own edge reflected outward, blurred and warped a
little more with every metre, makes the join seamless. Beyond it is
synthetic ground: the mean height of the nearest map edge, easing towards
the map's overall mean further out, with rolling hills scaled to the map's
own relief. The fade between them is complete by 15% of the border, because
a reflection that lingers any longer reads as a mirror on any map with big
features - the first attempt let it linger, and every landmass on Seton's
Clutch came out as a butterfly folded over the playable edge. Nothing of the
map's shapes survives into the far ground. Twice the size is forced, not chosen: Unity wants a
heightmap resolution of 2^n + 1, and with the interior kept sample-exact the
next power of two is the only extension. Every world position - spawns,
resource spots, props, wreckage, the `PlayableArea` rectangle - is offset
into the centre. `--no-border` (or the GUI checkbox) writes the old
edge-to-edge map instead.

**Lighting crosses two renderers**, so only quantities with a physical meaning
on both sides transfer: sun azimuth (which side of a ridge holds the shadow —
z-negated with the terrain), altitude (clamped to the shipped 15–30° band),
colour temperature from the sun colour's red/blue balance, intensity from
`lightingMultiplier` × sun luminance against the corpus median, and fog
attenuation from the source's fog band. `sunDA` incidentally now always sits
inside the shipped range; the old fixed 34° was just outside it.

**The tint carries material-aware detail.** `tint_colors` gets noise weighted by each layer's visible splat
share and its role: vegetation mottles (with a warm–cool hue tilt), sand bands
warm, mud darkens in patches, rock stays nearly clean. Roles come from texture
names, the same signal the CC0 substitution table was built from; noise scales
are fixed metres, so grain is the same physical size on a 256 as on a 1024.
Ground within 2.5 m of the waterline also darkens up to 13% — height above
water rather than horizontal distance, so a beach gets a wide damp band and a
sea cliff a thin one. Splat weights are layout data, not GPG pixels, so all of
this is CC0-clean and serves as the CC0 stand-in for the macro overlay that
legally cannot be baked there. Amplitudes are deliberately subtle: The_Forge's
hand-made tint is the reference for how much variation a shipped map carries.

**The heightmap is byte-exact.** SupCom stores uint16 at a height scale of
1/128 on every map ever shipped; Sanctuary stores uint16 scaled by
`height/65535`. Set `height` to 512 and `65535/512 = 128` — the same
fixed-point encoding. The converter asserts a zero-error round trip. The
border added around the map is new ground; the interior is the source's own
samples.

**z runs the opposite way.** SupCom draws heightmap row 0 at the top, so its z
grows southward; Sanctuary's grows northward. The import negates z on the
terrain, the markers, and the props together. Doing it to only one mirrors the
spawns off the terrain they were authored on, which is why `ScMarkerFit`
asserts terrain and markers agree (tolerance 6 m — community maps carry stale
marker heights; a genuine mirror measures 12 m and up).

---

## Two texture modes

**Default: the source map's own textures.** Extracted from `env.scd` (or the
map's own folder — about one community map in ten ships its own art), carried
in the map folder and referenced as `map/Textures/...`. DXT3 textures — one in
eleven of SupCom's, a format Unity cannot load — are transcoded to DXT5 with
the colour block copied bit-exact. The result looks closest to the original
and is **local-play only**: the folder contains someone else's art.

**CC0 mode: substitutes from a CC0 library** (ambientCG; ~30 materials,
built by `--tool build-texturepack`). The result is yours to share. Each of
the 312 corpus textures maps onto a same-role material via measured statistics:

- **chroma** (mean colour direction),
- **contrast** (luma standard deviation),
- **feature size** (contrast after 8× downsample — what separates fine
  confetti gravel from bold pebbles when their plain contrast is identical).

A per-channel `diffuseRemap` is then solved so the substitute renders the
exact average colour the original renders (mean error 0.01/255). Sand, gravel
and dirt share one candidate pool because **FA's texture names lie** — its
desert "gravels" are sand in all but name. A small `$eyeOverrides` table in
the substitution tool holds the few calls no metric can make ("soft", "mossy"),
each backed by an in-game comparison.

Two dials tame the photographic sources: `-Cc0TileMult` (default 2.5 — photo
features are centimetre-scale where FA paints for a 4–10 m repeat) and
`-Cc0NormalScale` (default 0.45 — photogrammetry normals are strong). CC0
layers also get real per-material mask maps built from the sources' AO and
roughness. `--tool compare-textures` renders any two deployed maps'
layers side by side, as configured, for auditing pairs.

---

## Previews

`The_Forge` is the only map the developers ship a `preview.png` with, and it
bakes numbered, colour-coded spawn discs into the image — the lobby does not
overlay them, so a map without them in its own preview shows none. Converted
maps get the same treatment: the badge palette was sampled from
that file, and sampling it *at the spawn positions its own `.sanmap` records*
is also what confirmed the world-to-pixel mapping, since the saturated pixel
sits under the marker only one way up (mean saturation 0.75 against 0.30
inverted).

The ground under the badges is the map's own. Each layer's albedo is measured
and multiplied by the `diffuseRemap` it will render through, which is the
honest colour — and in CC0 mode it is the point of that remap, solved so the
substitute renders the tone the original rendered. The product is dark in
absolute terms because in game it is lit, so the set is rescaled to the mean
luminance of the old fixed table; every relationship between the layers
survives. A map painted from a Sanctuary biome instead reads those textures out of
`Environment.sanpack` for the same treatment, so a Winter map no longer
previews in Highlands green.

---
## Props

The corpus's 299 maps place **1,685,924 props** between them — 6,716 per map
on average. All of it transfers: position, yaw (rebuilt from SupCom's basis
vectors, negated with the z flip), and bounded scale. Classification by
blueprint path covers **98.7% of every instance** as tree, tree-group or rock.

The Sanctuary side was catalogued by rendering **every shipped prop model as a
silhouette** — `.sanmodel` is name + vertex count + packed xyz floats — and
classifying the pictures, since the blueprints are opaquely named and all call
themselves "Harvestable prop". The naming decodes as `edb*` broadleaf / `edm*`
mineral with `s/m/l` sizes. The converter maps source environment families
onto matched palettes: tundra→Baikal conifers, desert/redrocks→dead snag and
gnarly trees with dark rocks, tropical→olive rocks, temperate→an 18-tree mix.
63 of 94 shipped props are usable (present in both game builds); the
WhiteDesert set (real desert trees, chalk hoodoos) is engine-only and waits on
the devs.

A SupCom "tree group" is one object whose mesh holds several trees; it becomes
one tree at 1.35× rather than inventing positions. `--no-props` turns them off entirely; the internal cap (default
20,000) thins evenly and reports what it dropped.

---

## Decals — parked at 90%

Everything works except the last step. The scanner reads 290 of 299 maps
(284,626 instances); transforms land in bounds with the projector rotation
decoded from shipped data (`Ry(yaw)·Rx(90°)`); map-local `.sandecal` /
`.sanmaterial` blueprints are authored per source texture and load in-game.
But `RTS/Decals/Default` renders the result invisibly, with no errors and no
shader source to iterate against. `-Decals` keeps the machinery available;
revisit when the developers ship a map that carries its own decals.

Two engine facts from that work matter beyond it:

- **Lua's `Engine.GetFileContent` cannot see map folders.** It reads
  `EM.Lua.FilesCache`, a startup-time dictionary (LJ/lua, the `.sanmap`s,
  `Environment.sanpack`). The `map/` path rewriting belongs to the asset
  pipeline only. The SanctuaryHud mod carries a Harmony fallback that serves
  `map/` paths from the loaded map's folder on a cache miss — decal-carrying
  maps **hard-require the mod on every machine**.
- A missing blueprint reaches Lua as an **empty string, not nil**, so loader
  error branches don't fire and `json.decode("")` aborts `RunMapSetup`
  downstream instead.

---

## Validation

Every check exists because something shipped looking plausible and wrong. The
recurring fault in this project is not code that crashes — it is a value that
renders: a placeholder with splat weight, a texture in an unloadable format, a
lighting field an order of magnitude out. None fail, none log, all look like
terrain.

| check | what it catches |
|---|---|
| `--validate` | the game's own Newtonsoft parse into `EM.Map.SanMap`; asset resolution per build tree; splat weight on placeholder textures; DXT3 in the map folder |
| `--validate --lua` | the game's own `json.lua` (stricter than Newtonsoft) |
| `--deploy-all` | all of the above against every deployed tree, after mirroring |
| `--tool test-environment` | the ~30 lighting/fog fields against the range the shipped maps use |
| `--tool test-biome-textures` | every biome-table texture has albedo, normal and mask |

Playability is measured, not assumed, and reported in terms that distinguish a
naval map from a broken one:

```
land 74% of map;  over the slope limit 42% of land;  open ground 1%
archipelago: 6 spawns across 6 landmasses (9%, 9%, 9%, 9%, 9%, 9%)
```

(That map spent weeks looking broken at "9% reachable". It is Crossfire
Canal.)

Reachability reimplements the game's Land nav layer: `maxSlope = 30°`, a cell
blocked if any neighbour in its 3×3 exceeds it.

---

## Layout

```
app/SanctuaryMapConverter/  the whole toolchain: GUI + headless CLI
        Gui/MainForm.cs   convert one map, convert a folder, deploy
        Core/Converter.cs SupCom map -> Sanctuary map (the main event)
        Core/RandomMap.cs random maps by style and biome
        Core/NamedMaps.*  four hand-tuned named maps
        Core/DeployAll.cs mirror to the game trees + validate
        Core/Validator.cs the game's own parsers, run against our output
        Core/GamePaths.cs finds both installs: registry, Steam libraries, drives
        Tools/            the measurement and texture-pack tools (--tool ...)

src/    MapGen.cs         heightfield, stratum weights, file writers
        Generator.cs      symmetry, spawn placement, scoring
        PathedMesas.cs    mask library and the plateau pipeline, after Neroxis
        Resources.cs      alloy budget, base rings, expansion clusters
        Terrain.cs        route finding, clearance, chokepoints, overlook
        ScMap.cs          .scmap and _save.lua reader
        ScMapEnvironment.cs  playable area, lighting adoption, macro-overlay bake
        ScWrecks.cs       WRECKAGE groups -> Sanctuary wreck props
        ScMapTextures.cs  anchored texture-block scanner (10 albedos + 9 normals)
        ScMapSplat.cs     splat adoption, incl. DXT5-compressed masks
        ScMapPropScan.cs  self-locating prop-table scanner
        ScPropImport.cs   prop classification and frame conversion
        ScMapDecalScan.cs self-locating decal-table scanner
        Bc7.cs / Dxt.cs / Bc3.cs / DdsMean.cs / DdsDecode.cs / DdsWrite.cs
                          DDS decode (BC1/2/3/7 means, full BC1/2/3 pixels)
                          and encode (DXT1/DXT5 with mip chains)
        Biomes.ps1 / GamePaths.ps1 / Import-MapGen.ps1
                          support for the tools/ dev scripts below

tools/  dev scripts, not needed to use the converter. Most are ported into
        the exe's --tool verbs; these remain for the jobs that are still
        one-offs - Measure-ScUnits.ps1 (regenerates docs/unit-wrecks.csv),
        Grab-Editor.ps1, New-MaskProbe.ps1, and the decal work that is parked.

texturepack/   not in the repo: 325 MB of CC0 DDS + manifest, built by
               `--tool build-texturepack` (downloads cache locally) or
               downloaded from the release
docs/          generated measurement CSVs, the substitution table, the unit
               table, and tuning-notes.md - the dials chosen from statistics
               rather than from the screen
```

---

## Format notes

Facts that cost real debugging time, recorded so they only cost it once.

**`.sanmap` is JSON** with strict int fields: `width`, `length`, `height`,
`heightmapResolution`, `Army.faction` — Newtonsoft rejects `128.0`, and a map
editor stuck at 0% is usually that. `heightmapResolution` must be a power of
two plus one: Unity rounds invalid values up and `SetHeights` writes into the
corner, leaving up to 44% of the terrain silently at height zero.

**The scmap texture block is 10 + 9**, not the 8 + 4 an obvious reading
suggests: LowerStratum + Strata 1–8 + UpperStratum albedos, then nine normals
that pair with layers 0–8. Modelling it as 8+4 *appears* to work on most maps
while silently dropping stratum 8 and mistaking the upper macrotexture for a
normal map. Ten fully-populated community maps refused to parse at all, which
is what gave it away. Two pre-FA maps use an older interleaved layout and are
not handled.

**Sanctuary's stratum `_mask` is Unity HDRP's mask map** — the engine binary
names the channels (`_MaskmapMetal`, `_MaskmapAO`, `_MaskmapSmoothness`):

| channel | meaning | shipped mean |
|---|---|---|
| R | metallic | 7.5 |
| G | ambient occlusion | 218.5 |
| B | detail mask | 149.5 |
| **A** | **smoothness** | **36.4** |

A flat mid-grey placeholder — the obvious "safe middle" — is wrong in every
channel and put a wet-plastic sheen over every converted map. So did
`skylightIntensity` 6000, where every shipped map uses exactly 0.
`--tool test-environment` exists so the next out-of-range value is caught by a
tool instead of an eye.

**Stratum TGAs** are 18-byte header, type 2, 32bpp BGRA, rows bottom-up;
`stratums_1_4` holds `[L3, L2, L1, L4]`, `stratums_5_8` holds `[L7, L6, L5,
L8]`. Splat resolution equals `heightmapResolution` — vertex-aligned, not a
power-of-two at texel centres.

**Texture lookups are extension-agnostic** (`Load.cs` strips the extension and
probes `.dds` first) — which also means files sharing a stem collide, so a
map-local blueprint must not share its stem with a texture. **Blueprints are
not** extension-agnostic: `.santp` in the engine build, `.sanprop` in the map
editor, hence `-PropExtension` and the double deploy.

**A smoothstep's steepest gradient is 1.5×H/d, not H/d.** Every impassable
ramp in this project traced back to that. Height profiles that must stay under
the nav limit are linear, not eased.

---

## Measuring the corpus

The converter's judgement calls are settled against measurement, not taste:
291 Supreme Commander maps and 47 shipped Sanctuary maps were measured to work
out what a normal map looks like, and the `--tool measure-*` verbs regenerate
those numbers. That is where the thresholds in the playability report come
from, and how the texture substitution table was solved.

This repo used to include a procedural map generator built on the same
measurements. It has been removed: other people are building better generators,
and carrying one here meant every converter change had to be made twice. The
terrain analysis it was built on stays, because the converter's own playability
report and the measurement tools use it.

## Deploying

Maps go in `<install>\engine\Sanctuary_Data\Maps\` for play, and in
`<install>\map-editor\SanctuaryMapEditor_Data\Maps\` for the editor when the
build ships one — same content, different prop extension (`.santp` for the
game, `.sanprop` for the editor; blueprint paths are *not*
extension-agnostic, which is why both copies exist). The Playtest build
dropped the map editor, so that tree is a bonus rather than a requirement and
is skipped when it is missing. `--deploy-all` does whichever trees exist and
validates everything it deployed.

**Restart the game afterwards**; it caches map files at load.

---

## Attribution and licensing

Terrain generation follows the approach of
[Neroxis](https://github.com/FAForever/Neroxis-Map-Generator), the Forged
Alliance Forever map generator.

The CC0 texture library is built from [ambientCG](https://ambientcg.com)
materials (CC0 — no attribution required, included here with thanks anyway).

Converted maps remain the work of their original authors. A map converted with
its source textures contains Gas Powered Games / Square Enix art and is for
**local play only**; the CC0 mode exists so a conversion can be
shared. Either way, credit the mapper.
