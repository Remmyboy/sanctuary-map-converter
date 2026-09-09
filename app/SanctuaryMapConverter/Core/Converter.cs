using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctuaryMapConverter.Core
{
    public sealed class ConvertOptions
    {
        public string Source;                    // folder holding the .scmap
        public string OutputMapsRoot;            // where the map folder is written
        public string ScdPath;                   // env.scd in the user's FA install (source-texture mode)
        public string PackDir;                   // bundled CC0 texture library
        public string TableCsv;                  // the solved substitution table
        public bool Cc0Textures;
        public string Name;                      // folder override; the map keeps its own display name
        public string Biome = "Tropical";        // base for lighting/fog; the source's own lighting overrides what it can
        public string PropExtension = ".santp";
        public double VerticalScale = 1.0;
        public bool NoProps;
        public bool Border = true;               // author a border around the map, as the shipped maps do
        public int MaxProps = 20000;
        public double Cc0TileMult = 2.5;
        public double Cc0NormalScale = 0.45;
    }

    public sealed class ConvertResult
    {
        public string MapDir;
        public string Folder;
        public string DisplayName;
        public int Spawns, Alloys, Props;
    }

    // The port of Convert-ScMap.ps1: same steps, same guards, same messages,
    // driving the same engine code the PowerShell pipeline compiles at run
    // time. Proven by golden-master: the same source converted through both
    // pipelines produces byte-identical binary files and semantically
    // identical JSON.
    public sealed class Converter
    {
        readonly ConvertOptions O;
        readonly Action<string> Log;

        public Converter(ConvertOptions options, Action<string> log)
        {
            O = options;
            Log = log ?? (_ => { });
        }

        public ConvertResult Run()
        {
            EngineState.Reset();   // fresh MapGen statics, like a fresh PS process

            // ---- source --------------------------------------------------
            string scmapFile = Directory.EnumerateFiles(O.Source, "*.scmap").FirstOrDefault()
                ?? throw new InvalidOperationException($"no .scmap in '{O.Source}'");
            string saveFile = Directory.EnumerateFiles(O.Source, "*_save.lua").FirstOrDefault()
                ?? throw new InvalidOperationException($"no _save.lua in '{O.Source}' - markers come from there");
            string scenarioFile = Directory.EnumerateFiles(O.Source, "*_scenario.lua").FirstOrDefault();

            var sc = MapGen.ReadScMap(scmapFile);
            var markers = MapGen.ReadScMarkers(saveFile);

            // The scenario file carries the human-readable name. Anchored to
            // ScenarioInfo's own indentation: a scenario has a name per team
            // as well, often first, and 22 corpus maps converted as "FFA"
            // before the anchor.
            string srcName = Path.GetFileNameWithoutExtension(scmapFile);
            if (scenarioFile != null)
            {
                var m = Regex.Match(File.ReadAllText(scenarioFile),
                    "(?m)^\\s{0,6}name\\s*=\\s*([\"'])(.*?)\\1");
                if (m.Success && m.Groups[2].Value.Trim().Length > 0)
                    srcName = m.Groups[2].Value.Trim();
            }

            sc.RowZeroIsNorth = MapGen.ResolveScRowOrder(sc, markers, out float eN, out float eS);

            Log($"Source: {srcName}  ({Path.GetFileName(scmapFile)})");
            Log($"  {sc.Size}x{sc.Size} cells, water {(sc.HasWater ? sc.WaterElevation.ToString("N1") + " m" : "none")}, version {sc.VersionMinor}");
            Log($"  row order {(sc.RowZeroIsNorth ? "north-first" : "south-first")} (mean marker error {eN:N2} m vs {eS:N2} m the other way)");

            // ---- adoption ------------------------------------------------
            // 512 makes Sanctuary's raw encoding identical to SupCom's:
            // 65535/512 = 128, and SupCom's height scale is 1/128 everywhere.
            const int MapHeight = 512;
            MapGen.MaxHeight = MapHeight;
            MapGen.AdoptScMap(sc, (float)O.VerticalScale);
            MapGen.LandBase = MapGen.WaterLevel + 5.0f;
            int size = (int)MapGen.MapSize;
            MapGen.SRes = MapGen.HRes;
            Log($"  imported {size} m square, relief {MapGen.HeightMax():N1} m, water level {MapGen.WaterLevel:N1} m");

            // ---- markers -------------------------------------------------
            var mexX = new List<double>();
            var mexZ = new List<double>();
            var spawns = new List<(string Name, double X, double Z)>();
            foreach (var k in markers)
            {
                double x = k.X, z = MapGen.ScMarkerZ(sc, k.Z);
                if (x < 0 || x > size || z < 0 || z > size) continue;
                if (Regex.IsMatch(k.Name, "^ARMY_\\d+$")) spawns.Add((k.Name, x, z));
                else if (k.Type == "Mass" || k.Type == "Hydrocarbon") { mexX.Add(x); mexZ.Add(z); }
            }
            spawns = spawns.OrderBy(s => int.Parse(Regex.Replace(s.Name, "\\D", ""))).ToList();
            Log($"  markers: {spawns.Count} spawns, {mexX.Count} resource spots");

            // The author's playable area, where one is defined, sane and
            // respected by the spawns - see ScPlayableArea for the guards.
            float[] playable = MapGen.ScPlayableArea(sc, MapGen.ReadScAreas(saveFile), markers);
            if (playable != null)
                Log($"  playable area: {playable[2]:N0}x{playable[3]:N0} at ({playable[0]:N0}, {playable[1]:N0}) - the source insets its border");
            if (spawns.Count < 2)
                throw new InvalidOperationException($"only {spawns.Count} spawn marker(s); not a skirmish map");

            // A mirrored import is self-consistent everywhere else, so the
            // marker fit is the only cheap way to catch it. 6.0 m: community
            // maps carry stale marker heights (3-5 m of noise); a genuine
            // mirror measures 12 m and up.
            float fit = MapGen.ScMarkerFit(sc, markers);
            Log($"  marker fit against imported terrain: {fit:N2} m mean error");
            if (fit > 6.0f)
                throw new InvalidOperationException(
                    $"markers sit {fit:N1} m off the terrain on average - the heightmap fold and the marker mapping disagree");

            // ---- validation ----------------------------------------------
            Log("Checking pathability against the 30-degree land limit...");
            MapGen.BuildWalkable();
            int walk = MapGen.WalkableCount();
            var ts = MapGen.TerrainStats();
            double steep = (ts[5] + ts[6]) / Math.Max(1.0, ts[2]);
            var og = MapGen.OpenGroundStats(6.0f);
            int hres = MapGen.HRes;
            double landFrac = ts[2] / (double)(hres * hres);

            // Group the spawns by landmass so an archipelago reads as what it
            // is, not as a broken import.
            var groups = new List<(bool[,] Mask, int Count, List<string> Names)>();
            double cellStep = size / (double)(hres - 1);
            foreach (var s in spawns)
            {
                int r = Math.Clamp((int)Math.Round((size - s.Z) / cellStep), 0, hres - 1);
                int c = Math.Clamp((int)Math.Round(s.X / cellStep), 0, hres - 1);
                var hit = groups.FirstOrDefault(g => g.Mask[r, c]);
                if (hit.Mask != null) { hit.Names.Add(s.Name); continue; }
                var mask = MapGen.Reachable((float)s.X, (float)s.Z);
                groups.Add((mask, MapGen.CountTrue(mask), new List<string> { s.Name }));
            }

            Log($"  land {landFrac:P0} of map;  over the slope limit {steep:P0} of land;  open ground {og[0] / Math.Max(1.0, og[1]):P0}");
            if (groups.Count == 1)
                Log($"  all {spawns.Count} spawns share one landmass ({groups[0].Count / Math.Max(1.0, walk):P0} of walkable ground)");
            else
                Log($"  archipelago: {spawns.Count} spawns across {groups.Count} landmasses ({string.Join(", ", groups.Select(g => (g.Count / Math.Max(1.0, walk)).ToString("P0")))})");

            int offMass = 0;
            if (groups.Count >= 1)
            {
                var main = groups.OrderByDescending(g => g.Count).First().Mask;
                for (int i = 0; i < mexX.Count; i++)
                {
                    int r = Math.Clamp((int)Math.Round((size - mexZ[i]) / cellStep), 0, hres - 1);
                    int c = Math.Clamp((int)Math.Round(mexX[i] / cellStep), 0, hres - 1);
                    if (!main[r, c]) offMass++;
                }
            }
            if (offMass > 0)
                Log($"  note: {offMass} of {mexX.Count} resource spots sit off the main landmass (islands or water)");

            // ---- output folder -------------------------------------------
            // The map's own name, cleaned into a folder name. No "~SC-"
            // prefix: the tilde sorts converted maps away from the rest of
            // the list, which is not where anyone looks for them.
            string folder = O.Name ?? Regex.Replace(srcName, "[^\\w]+", "_").Trim('_');
            string mapDir = Path.Combine(O.OutputMapsRoot, folder);
            string texDir = Path.Combine(mapDir, "Textures");
            if (Directory.Exists(mapDir)) Directory.Delete(mapDir, true);
            Directory.CreateDirectory(texDir);

            // ---- textures ------------------------------------------------
            byte[] scBytes = File.ReadAllBytes(scmapFile);
            var texSet = MapGen.ScanScTextures(scBytes, sc.Size);
            if (texSet == null || !MapGen.AdoptScSplat(scBytes, texSet))
                throw new InvalidOperationException(
                    "the texture block could not be scanned - this is one of the two known pre-Forged Alliance format maps, which are not supported");
            MapGen.SetTintNoiseFromScTextures(texSet);

            ExportResult exp;
            if (O.Cc0Textures)
            {
                exp = TextureExport.ExportCc0(texSet.Paths, texDir, O.PackDir, O.TableCsv, Log);
                Log($"  textures: {texSet.UsedLayers} source layers, {exp.Copied} files copied, splat {texSet.MaskSize} -> {MapGen.SRes}" +
                    (MapGen.DroppedLayers > 0 ? $", {MapGen.DroppedLayers} unassigned layer(s) zeroed" : "") +
                    $", CC0 substitutes ({exp.Inexact} inexact role)");
            }
            else
            {
                exp = TextureExport.ExportSource(O.ScdPath, texSet.Paths, texSet.NormalPaths,
                    texDir, Path.GetDirectoryName(O.Source), Log);
                Log($"  textures: {texSet.UsedLayers} source layers, {exp.Copied} files copied, splat {texSet.MaskSize} -> {MapGen.SRes}" +
                    (MapGen.DroppedLayers > 0 ? $", {MapGen.DroppedLayers} unassigned layer(s) zeroed" : "") +
                    (exp.Transcoded > 0 ? $", {exp.Transcoded} DXT3 -> DXT5" : ""));
            }
            if (exp.Missing.Count > 0)
                Log($"  textures not found: {string.Join(", ", exp.Missing.Take(3))}");

            // The UpperStratum macro overlay, baked into tint_colors. Source
            // mode only - the bake copies GPG pixels, and a CC0 build ships
            // none. AdoptScMacro rejects the degenerate and the invisible.
            if (!O.Cc0Textures && !string.IsNullOrEmpty(texSet.Paths[9]))
            {
                byte[] mb = TextureExport.ReadSourceBytes(O.ScdPath, texSet.Paths[9], Path.GetDirectoryName(O.Source));
                if (mb != null && MapGen.AdoptScMacro(mb, texSet.Scales[9]))
                    Log($"  macro overlay: {Path.GetFileName(texSet.Paths[9])} baked into the tint at {texSet.Scales[9]:N0} m repeat");
            }

            // Slot badges on the preview, in spawn order - the same order the
            // Spawn markers are written in below.
            MapGen.PreviewSpawnX = spawns.Select(s => (float)s.X).ToArray();
            MapGen.PreviewSpawnZ = spawns.Select(s => (float)s.Z).ToArray();

            // The preview draws the author's ground, not our biome's: mean
            // colour of each exported albedo, times the diffuseRemap the map
            // will render it through. See SetPreviewLayerColors.
            var prevFiles = new string[9];
            var prevRemaps = new double[9][];
            for (int li = 0; li <= 8; li++)
            {
                string sp = texSet.Paths[li];
                if (string.IsNullOrEmpty(sp) || !exp.Names.TryGetValue(sp, out var leaf)) continue;
                prevFiles[li] = Path.Combine(texDir, leaf);
                prevRemaps[li] = exp.Remaps.TryGetValue(sp, out var rm) ? rm : new[] { 0.37, 0.35, 0.32 };
            }
            MapGen.SetPreviewLayerColors(prevFiles, prevRemaps);

            // The preview is the map, not the terrain: drawn before the
            // border goes on, like the shipped maps' own previews.
            MapGen.WritePreview(Path.Combine(texDir, "preview.png"), 512, false, null, null, null);
            File.Copy(Path.Combine(texDir, "preview.png"), Path.Combine(mapDir, "preview.png"), true);

            // ---- border --------------------------------------------------
            // The engine mirrors whatever reaches the terrain edge - see
            // src/ScMapBorder.cs. Everything positioned from here on is
            // offset by `off` into the centre of the larger terrain.
            int off = 0;
            if (O.Border)
            {
                off = MapGen.ExtendBorder();
                Log($"  border: {off} m of dissolved edge terrain on every side; terrain {(int)MapGen.MapSize} m, playable {size} m in the centre");
            }

            MapGen.WriteHeightmap(Path.Combine(texDir, "heightmap.raw"));
            MapGen.WriteStratums(texDir);
            MapGen.WriteTints(texDir, 2048);

            // ---- props ---------------------------------------------------
            var propGroups = new List<JObj>();
            int placedProps = 0;
            if (!O.NoProps)
            {
                var scProps = MapGen.ScanScProps(scBytes);
                if (scProps != null && scProps.Count > 0)
                {
                    var conv = MapGen.ConvertScProps(scProps, sc, (float)O.VerticalScale);
                    int found = conv.Count;
                    var kept = MapGen.ThinProps(conv, O.MaxProps);
                    PropPalettes.ResetRoundRobin();

                    var buckets = new SortedDictionary<string, List<JObj>>(StringComparer.Ordinal);
                    int skipped = 0, treeGroups = 0;
                    foreach (var p in kept)
                    {
                        if (p.Kind == 3) { skipped++; continue; }
                        string bp = PropPalettes.Pick(p);
                        if (!buckets.TryGetValue(bp, out var list)) buckets[bp] = list = new List<JObj>();

                        // A SupCom "group" prop is one object whose mesh holds
                        // several trees; it becomes one tree scaled up rather
                        // than inventing positions.
                        double gs = p.Kind == 1 ? 1.35 : 1.0;
                        if (p.Kind == 1) treeGroups++;
                        double sx = Math.Clamp(p.ScaleX, 0.5, 2.0) * gs;
                        double sy = Math.Clamp(p.ScaleY, 0.5, 2.0) * gs;
                        double sz = Math.Clamp(p.ScaleZ, 0.5, 2.0) * gs;

                        list.Add(Json.Obj(
                            ("position", Json.Vec3(Math.Round(p.X + off, 3), Math.Round(p.Y, 3), Math.Round(p.Z + off, 3))),
                            ("rotation", Json.Quat(0.0, Math.Round(Math.Sin(p.Yaw / 2), 7), 0.0, Math.Round(Math.Cos(p.Yaw / 2), 7))),
                            ("scale", Json.Vec3(Math.Round(sx, 4), Math.Round(sy, 4), Math.Round(sz, 4)))));
                    }

                    placedProps = buckets.Values.Sum(l => l.Count);
                    foreach (var kv in buckets)
                    {
                        if (kv.Value.Count == 0) continue;
                        propGroups.Add(Json.Obj(
                            ("blueprintPath", $"{PropPalettes.EnvOf(kv.Key)}/Props/{kv.Key}/{kv.Key}{O.PropExtension}"),
                            ("transforms", kv.Value)));
                    }
                    Log($"  props: {placedProps:n0} of {found:n0} source props placed" +
                        (kept.Count < found ? $", thinned from {found:n0} by MaxProps" : "") +
                        (skipped > 0 ? $", {skipped:n0} unclassified skipped" : "") +
                        (treeGroups > 0 ? $", {treeGroups:n0} tree groups became single trees" : ""));
                }
                else Log("  props: source prop table unreadable; map converts without props");
            }

            // ---- wreckage ------------------------------------------------
            // Starting reclaim, mapped onto the Playtest build's six wreck
            // meshes by unit size - see src/ScWrecks.cs for the ladder and
            // the wall filter. Every wreck blueprint is worth the same 100
            // alloys (dev placeholder values), so fidelity is in the
            // positions and silhouettes, not yet the economy.
            int placedWrecks = 0;
            var wrecks = MapGen.ReadScWrecks(saveFile);
            if (wrecks.Count > 0)
            {
                string unitCsv = O.TableCsv != null
                    ? Path.Combine(Path.GetDirectoryName(O.TableCsv), "unit-wrecks.csv")
                    : null;
                if (unitCsv == null || !File.Exists(unitCsv))
                    Log($"  wreckage: {wrecks.Count:n0} source wrecks skipped - unit-wrecks.csv not found");
                else
                {
                    var wtable = MapGen.LoadScUnitTable(unitCsv);
                    var wbuckets = new SortedDictionary<string, List<JObj>>(StringComparer.Ordinal);
                    int wskipped = 0;
                    foreach (var w in wrecks)
                    {
                        double x = w.X, z = MapGen.ScMarkerZ(sc, w.Z);
                        if (x < 0 || x > size || z < 0 || z > size) { wskipped++; continue; }
                        string mesh = MapGen.ScWreckBlueprint(w.Type, wtable);
                        if (mesh == null) { wskipped++; continue; }
                        x += off; z += off;
                        double yaw = Math.PI - w.Yaw;        // the z negation mirrors the heading
                        if (!wbuckets.TryGetValue(mesh, out var list)) wbuckets[mesh] = list = new List<JObj>();
                        list.Add(Json.Obj(
                            ("position", Json.Vec3(Math.Round(x, 3), Math.Round(MapGen.HeightAtWorld((float)x, (float)z), 2), Math.Round(z, 3))),
                            ("rotation", Json.Quat(0.0, Math.Round(Math.Sin(yaw / 2), 7), 0.0, Math.Round(Math.Cos(yaw / 2), 7))),
                            ("scale", Json.Vec3(1.0, 1.0, 1.0))));
                    }
                    placedWrecks = wbuckets.Values.Sum(l => l.Count);
                    foreach (var kv in wbuckets)
                        propGroups.Add(Json.Obj(
                            ("blueprintPath", $"Environment/Dev/Props/Units/{kv.Key}/{kv.Key}_wreckage{O.PropExtension}"),
                            ("transforms", kv.Value)));
                    if (placedWrecks > 0)
                        Log($"  wreckage: {placedWrecks:n0} of {wrecks.Count:n0} source wrecks placed as harvestable wreck props" +
                            (wskipped > 0 ? $", {wskipped:n0} skipped (walls and other sub-{MapGen.ScWreckMinMass:N0}-mass debris)" : ""));
                }
            }

            // ---- stratum layers ------------------------------------------
            var stratums = BuildStratums(texSet, exp);

            // ---- json ----------------------------------------------------
            // The biome is the base; the source's own lighting overrides the
            // quantities that translate (sun direction, warmth, brightness,
            // fog thickness). Clamps live in the Sc* helpers.
            var bio = Biome.Get(O.Biome);
            MapGen.ScSunAngles(sc, out double sunRA, out double sunDA);
            double sunTemp = MapGen.ScSunTemperature(sc);
            if (sunTemp < 0) sunTemp = bio.SunTemp;
            double sunIntensity = MapGen.ScSunIntensity(sc);
            double fogAtt = MapGen.ScFogAttenuation(sc, bio.Fog);
            Log($"  lighting: sun azimuth {sunRA:N0}°, altitude {sunDA:N0}°, {sunTemp:N0} K, {sunIntensity:N0} lux, fog {fogAtt:N0} m ({bio.Name} biome base)");
            var spawnT = new JObj();
            var armies = new JObj();
            for (int i = 0; i < spawns.Count; i++)
            {
                double ax = MapGen.SnapBuild((float)spawns[i].X) + off;
                double az = MapGen.SnapBuild((float)spawns[i].Z) + off;
                string key = $"ARMY_{i + 1}";
                spawnT.Add(key, Transform(ax, Math.Round(MapGen.HeightAtWorld((float)ax, (float)az), 2), az));
                armies.Add(key, Json.Obj(("faction", 0), ("alloys", 500.0), ("energy", 500.0), ("groups", Json.Obj())));
            }
            var alloyT = new JObj();
            for (int i = 0; i < mexX.Count; i++)
            {
                double px = MapGen.SnapBuild((float)mexX[i]) + off;
                double pz = MapGen.SnapBuild((float)mexZ[i]) + off;
                alloyT.Add($"Alloys_{i + 1:D3}", Transform(px, Math.Round(MapGen.HeightAtWorld((float)px, (float)pz), 2), pz));
            }

            var map = Json.Obj(
                ("fileVersion", 3), ("mapVersion", 1),
                ("name", srcName),
                ("credits", $"Converted from Supreme Commander: Forged Alliance - {Path.GetFileName(scmapFile)}"),
                ("width", (int)MapGen.MapSize), ("length", (int)MapGen.MapSize),
                ("height", MapHeight),
                ("heightmapResolution", MapGen.HRes),
                ("hasWater", sc.HasWater),
                ("waterLevel", (double)MapGen.WaterLevel),
                ("waterDepth", Math.Round(Math.Clamp(sc.WaterElevation - sc.WaterElevationDeep, 1.0, 8.0), 2)),
                ("waterWindSpeed", 0.06), ("waterWindDirection", 100.0),
                ("waterShoreDepthOffset", 8.0), ("waterShoreDepthStrength", 0.7),
                ("waterShoreDistanceOffset", 0.0), ("waterShoreDistanceStrength", 2.0),
                ("shader", "RTS/TerrainLit"),
                ("heightTransition", 2.0), ("fadeDistance", 55.0), ("fadeStartDistance", 32.0),
                ("stratumLayers", stratums),
                ("sunRA", sunRA), ("sunDA", sunDA), ("sunIntensity", sunIntensity),
                ("sunTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("sunTemperature", sunTemp),
                ("sunAngularDiameter", 0.5), ("sunVolumetricsMultiplier", 6.7), ("sunVolumetricsShadowDimer", 0.5),
                ("skylightIntensity", 0.0),
                ("skylightTint", Json.Rgba(1.0, 1.0, 1.0, 1.0)),
                ("skylightTemperature", bio.Sky),
                ("exposure", bio.Exposure), ("exposureCompensation", 0.0), ("skyboxExposure", 12.0),
                ("fogAttenuationDistance", fogAtt),
                ("fogBaseHeight", 6.0), ("fogMaximumHeight", 140.0), ("fogMaximumDistance", 1800.0), ("fogAnisotropy", 0.0),
                ("skybox", Json.Obj(("path", "Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr"))),
                // "PlayableArea" is the key the engine looks up (it falls back
                // to the first area, which is how the old "Playable" worked).
                // The rectangle drives the out-of-bounds fog; the border sits
                // outside it.
                ("areas", Json.Obj(("PlayableArea", playable != null
                    ? Json.Obj(("x", playable[0] + (double)off), ("y", playable[1] + (double)off),
                               ("width", (double)playable[2]), ("height", (double)playable[3]))
                    : Json.Obj(("x", (double)off), ("y", (double)off), ("width", (double)size), ("height", (double)size))))),
                ("armies", armies),
                ("chains", Json.Obj()),
                ("markers", Json.Obj(
                    ("Spawn", Json.Obj(("resource", false), ("transforms", spawnT))),
                    ("Alloys", Json.Obj(("resource", true), ("transforms", alloyT))))),
                ("decals", new List<JObj>()),
                ("windSpeed", 0.25), ("windDirection", 160.0),
                ("props", propGroups));

            Biome.AddEnvironment(map, MapGen.WaterLevel);

            string sanmapPath = Path.Combine(mapDir, folder + ".sanmap");
            File.WriteAllText(sanmapPath, Json.Write(map), new UTF8Encoding(false));
            Log($"  -> {mapDir}");

            return new ConvertResult
            {
                MapDir = mapDir,
                Folder = folder,
                DisplayName = srcName,
                Spawns = spawns.Count,
                Alloys = mexX.Count,
                Props = placedProps,
            };
        }

        static JObj Transform(double x, double y, double z) => Json.Obj(
            ("position", Json.Vec3(x, y, z)),
            ("rotation", Json.Quat(0.0, 0.0, 0.0, 1.0)),
            ("scale", Json.Vec3(1.0, 1.0, 1.0)));

        List<JObj> BuildStratums(MapGen.ScTextureSet set, ExportResult exp)
        {
            string maskRef = $"map/Textures/{exp.MaskName}";

            // Shared normal fallback: the first entry that is actually a
            // normal map, or the base layer's own normal when it exists.
            string normalRef = maskRef;
            foreach (var n in set.NormalPaths)
                if (!string.IsNullOrEmpty(n) && n.Contains("_normal", StringComparison.OrdinalIgnoreCase)
                    && exp.Names.ContainsKey(n))
                { normalRef = $"map/Textures/{exp.Names[n]}"; break; }
            if (set.Paths[0] != null && exp.Normals.TryGetValue(set.Paths[0], out var baseNormal))
                normalRef = $"map/Textures/{baseNormal}";

            string baseRef = null;
            if (!string.IsNullOrEmpty(set.Paths[0]) && exp.Names.TryGetValue(set.Paths[0], out var b))
                baseRef = $"map/Textures/{b}";
            double[] baseRemap = null;
            if (!string.IsNullOrEmpty(set.Paths[0])) exp.Remaps.TryGetValue(set.Paths[0], out baseRemap);

            var stratums = new List<JObj>();
            for (int li = 0; li < 9; li++)
            {
                // Paths carries the file's true layout: 0 = LowerStratum (the
                // base), 1..8 the masked strata.
                string p = li <= 8 ? set.Paths[li] : null;
                string albedoRef;
                double tile;
                if (string.IsNullOrEmpty(p) || !exp.Names.ContainsKey(p))
                {
                    // An unused slot still needs an entry; AdoptScSplat has
                    // zeroed its weight, so it is never drawn - but pointing
                    // it at the base means any surviving weight renders as
                    // plausible ground rather than a grey wash.
                    albedoRef = baseRef ?? maskRef;
                    tile = 10.0;
                    p = null;
                }
                else
                {
                    albedoRef = $"map/Textures/{exp.Names[p]}";
                    tile = set.Scales[li];
                    if (tile < 1.0) tile = 10.0;
                    if (O.Cc0Textures) tile *= O.Cc0TileMult;
                }

                string layerNormal = normalRef;
                if (p != null && exp.Normals.TryGetValue(p, out var ln))
                    layerNormal = $"map/Textures/{ln}";
                else if (li <= 8 && !string.IsNullOrEmpty(set.NormalPaths[li])
                    && set.NormalPaths[li].Contains("_normal", StringComparison.OrdinalIgnoreCase)
                    && exp.Names.TryGetValue(set.NormalPaths[li], out var srcN))
                    layerNormal = $"map/Textures/{srcN}";

                string layerMask = maskRef;
                if (p != null && exp.Masks.TryGetValue(p, out var lm))
                    layerMask = $"map/Textures/{lm}";

                double[] remap = { 0.37, 0.35, 0.32 };
                if (p != null && exp.Remaps.TryGetValue(p, out var lr)) remap = lr;
                else if (p == null && baseRemap != null) remap = baseRemap;

                double ns = O.Cc0Textures ? O.Cc0NormalScale : 1.0;
                stratums.Add(Json.Obj(
                    ("name", null),
                    ("albedo", Json.Obj(("path", albedoRef))),
                    ("normal", Json.Obj(("path", layerNormal))),
                    ("mask", Json.Obj(("path", layerMask))),
                    ("tileSize", Json.Obj(("x", tile), ("y", tile))),
                    ("tileSizeFar", Json.Obj(("x", tile * 6.0), ("y", tile * 6.0))),
                    ("tileSizeTriplanar", 12.0),
                    ("tileSizeFarTriplanar", 36.0),
                    ("normalScale", ns), ("normalScaleFar", ns),
                    ("normalFarNearBlend", 0.3), ("heightFarNearBlend", 0.5),
                    ("diffuseRemap", Json.Rgba(remap[0], remap[1], remap[2], 1.0)),
                    ("farColorRemap", Json.Rgba(1.0, 1.0, 1.0, 0.0)),
                    ("maskRemapMin", Json.Obj(("x", 0.0), ("y", 0.0), ("z", 0.0), ("w", 0.0))),
                    ("maskRemapMax", Json.Obj(("x", 1.0), ("y", 1.0), ("z", 1.0), ("w", 1.0)))));
            }
            return stratums;
        }
    }
}
