using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

namespace SanctuaryMapConverter.Core
{
    // tools/Test-Sanmap.ps1 in C#. The only reliable check on a generated map
    // is the one the game performs: Newtonsoft's JsonConvert.PopulateObject
    // against the real EM.Map.SanMap type, whose int fields will not accept
    // "128.0" - a failure there leaves the editor sitting at 0% forever. The
    // game's assemblies are loaded into a collectible context and that exact
    // call replayed; no game process, nothing written.
    //
    // The texture checks each exist because a fault shipped without them:
    // splat weight on a placeholder rendered as a featureless wash, DXT3
    // rendered as snow, and a wrong-size heightmap dropped most of the
    // terrain. LuaCheck replays the game's own json.lua via KeraLua, which is
    // stricter than Newtonsoft.
    public sealed class ValidateOptions
    {
        public string Managed;        // map-editor Managed dir (SanMap + Newtonsoft)
        public bool CheckTextures;
        public string GamedataDir;    // check referenced assets against *.sanpack
        public bool LuaCheck;
        public string GameRoot;       // needed by LuaCheck for KeraLua + json.lua
    }

    public static class Validator
    {
        public static bool Check(string path, ValidateOptions o, Action<string> log)
        {
            string name = Path.GetFileName(path);
            try
            {
                var ctx = new AssemblyLoadContext(Guid.NewGuid().ToString(), isCollectible: true);
                ctx.Resolving += (c, n) =>
                {
                    string dll = Path.Combine(o.Managed, n.Name + ".dll");
                    return File.Exists(dll) ? c.LoadFromAssemblyPath(dll) : null;
                };
                ctx.LoadFromAssemblyPath(Path.Combine(o.Managed, "UnityEngine.CoreModule.dll"));
                var nj = ctx.LoadFromAssemblyPath(Path.Combine(o.Managed, "Newtonsoft.Json.dll"));

                // The Playtest build merged the EM.* assemblies; SanMap moved
                // from EM.Map.dll into Trebuchet.dll with its namespace intact.
                Type t = null;
                foreach (var asmName in new[] { "EM.Map.dll", "Trebuchet.dll" })
                {
                    string dll = Path.Combine(o.Managed, asmName);
                    if (!File.Exists(dll)) continue;
                    t = ctx.LoadFromAssemblyPath(dll).GetType("EM.Map.SanMap");
                    if (t != null) break;
                }
                if (t == null) throw new InvalidOperationException(
                    "EM.Map.SanMap not found in EM.Map.dll or Trebuchet.dll under " + o.Managed);

                // SanMap's only constructor takes a path and would start loading.
                object map = RuntimeHelpers.GetUninitializedObject(t);
                var populate = nj.GetType("Newtonsoft.Json.JsonConvert").GetMethods()
                    .First(m => m.Name == "PopulateObject" && m.GetParameters().Length == 2);
                populate.Invoke(null, new[] { File.ReadAllText(path), map });

                int w = (int)Field(map, "width");
                int hmr = (int)Field(map, "heightmapResolution");
                if (hmr == 0) hmr = w + 1;

                int props = SumTransforms(Field(map, "props"));
                int decals = SumTransforms(Field(map, "decals"));

                // Buffered: nothing is reported as passing until every check has run.
                var detail = new List<string>
                {
                    string.Format("      {0}x{1} m, height {2}, hmRes {3}, water {4}, {5} stratums, {6} props, {7} decals",
                        w, Field(map, "length"), Field(map, "height"), hmr,
                        (bool)Field(map, "hasWater") ? Field(map, "waterLevel") : "none",
                        CountOf(Field(map, "stratumLayers")), props, decals),
                };

                string mapDir = Path.GetDirectoryName(path);
                if (o.CheckTextures) CheckTextureFiles(path, mapDir, hmr, map, detail);
                if (o.GamedataDir != null) CheckAssets(o.GamedataDir, mapDir, map, detail);
                if (o.LuaCheck && !LuaJsonCheck(path, o.GameRoot, log)) return false;

                log($"PASS  {name}");
                foreach (var d in detail) log(d);
                return true;
            }
            catch (Exception e)
            {
                log($"FAIL  {name}");
                log($"      {Base(e).Message}");
                return false;
            }
        }

        static Exception Base(Exception e) => e is TargetInvocationException ti && ti.InnerException != null ? Base(ti.InnerException) : e.GetBaseException();

        // SanMap uses public fields, but its member types mix fields and
        // properties; resolve either, the way PowerShell's binder did.
        static object Field(object obj, string name)
        {
            var t = obj.GetType();
            var f = t.GetField(name);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(name);
            if (p != null) return p.GetValue(obj);
            throw new MissingMemberException(t.Name, name);
        }

        // SanMap mixes arrays and List<T> across versions; PowerShell's
        // adapter blurred that, so the port counts either.
        static int CountOf(object o) => ((System.Collections.ICollection)o).Count;

        static int SumTransforms(object arr)
        {
            int sum = 0;
            foreach (var e in (System.Collections.IEnumerable)arr)
                sum += CountOf(Field(e, "transforms"));
            return sum;
        }

        static void CheckTextureFiles(string sanmapPath, string mapDir, int hmr, object map, List<string> detail)
        {
            string tex = Path.Combine(mapDir, "Textures");
            string raw = Path.Combine(tex, "heightmap.raw");
            if (!File.Exists(raw)) throw new Exception(@"missing Textures\heightmap.raw");
            long want = (long)hmr * hmr * 2;
            long got = new FileInfo(raw).Length;
            if (got != want) throw new Exception($"heightmap.raw is {got} bytes, heightmapResolution {hmr} needs {want}");

            foreach (var f in new[] { "stratums_1_4", "stratums_5_8", "tint_colors", "tint_geometry" })
            {
                string tp = Path.Combine(tex, f + ".tga");
                if (!File.Exists(tp)) throw new Exception($@"missing Textures\{f}.tga");
                var h = new byte[18];
                using (var fs = File.OpenRead(tp)) fs.ReadExactly(h, 0, 18);
                int tw = BitConverter.ToUInt16(h, 12), th = BitConverter.ToUInt16(h, 14);
                if (h[2] != 2 || h[16] != 32) throw new Exception($"{f}.tga is not uncompressed 32bpp true-colour");
                long expect = 18L + (long)tw * th * 4;
                long len = new FileInfo(tp).Length;
                if (len != expect) throw new Exception($"{f}.tga header says {tw}x{th} but the file is {len} bytes, not {expect}");
                detail.Add(string.Format("      {0,-14} {1}x{2}", f, tw, th));
            }

            // Sanctuary ships no DXT3 at all: Unity has TextureFormat.DXT1 and
            // DXT5 and nothing for BC2, so a DXT3 texture is not an error, it
            // is a blank white surface.
            if (Directory.Exists(tex))
                foreach (var dd in Directory.EnumerateFiles(tex, "*.dds"))
                {
                    var hb = new byte[96];
                    using var hs = File.OpenRead(dd);
                    int read = hs.Read(hb, 0, 96);
                    if (read < 96 || Encoding.ASCII.GetString(hb, 0, 4) != "DDS ") continue;
                    if (Encoding.ASCII.GetString(hb, 84, 4) == "DXT3")
                        throw new Exception($"{Path.GetFileName(dd)} is DXT3 - Unity has no format for BC2, so it loads as a blank white surface");
                    // See src/Dxt.cs: the terrain ignores the loader's
                    // blue/red flag, so 24-bit BGR renders channel-swapped.
                    if ((BitConverter.ToInt32(hb, 80) & 0x4) == 0 && BitConverter.ToInt32(hb, 88) == 24 && BitConverter.ToUInt32(hb, 92) == 0xff0000u)
                        throw new Exception($"{Path.GetFileName(dd)} is 24-bit BGR - the terrain samples it with red and blue swapped (a normal map renders black)");
                }

            // A stratum slot that carries splat weight has to be painted by
            // something that can actually be ground. A converted map once
            // pointed unused slots at a 4x4 placeholder while handing them the
            // weights of the used ones; every other check here passed it.
            var w8 = new double[9];
            foreach (var (file, baseL) in new[] { ("stratums_1_4", 1), ("stratums_5_8", 5) })
            {
                byte[] sb = File.ReadAllBytes(Path.Combine(tex, file + ".tga"));
                long n = 0;
                // BGRA on disk is [L3,L2,L1,L4] relative to the pair base.
                for (int k = 18; k + 3 < sb.Length; k += 4)
                {
                    w8[baseL + 2] += sb[k]; w8[baseL + 1] += sb[k + 1];
                    w8[baseL] += sb[k + 2]; w8[baseL + 3] += sb[k + 3];
                    n++;
                }
                if (n > 0) for (int li = baseL; li <= baseL + 3; li++) w8[li] /= n;
            }
            var layers = (System.Collections.IList)Field(map, "stratumLayers");
            for (int li = 1; li <= 8 && li < layers.Count; li++)
            {
                if (w8[li] < 1.0) continue;
                string ap = (string)Field(Field(layers[li], "albedo"), "path");
                if (ap == null || !ap.StartsWith("map/", StringComparison.OrdinalIgnoreCase)) continue;
                string af = Path.Combine(mapDir, ap.Substring(4));
                if (!File.Exists(af))
                {
                    string stem = Path.ChangeExtension(af, null);
                    af = new[] { ".dds", ".tga", ".png" }.Select(e => stem + e).FirstOrDefault(File.Exists);
                    if (af == null) continue;
                }
                var ab = new byte[32];
                using (var afs = File.OpenRead(af)) afs.ReadExactly(ab, 0, 32);
                int aw = Encoding.ASCII.GetString(ab, 0, 4) == "DDS "
                    ? BitConverter.ToInt32(ab, 16)
                    : BitConverter.ToUInt16(ab, 12);
                if (aw < 64)
                    throw new Exception(string.Format(
                        "stratum layer {0} carries splat weight (mean {1:n0}/255) but its albedo {2} is only {3}px - that is a placeholder, not a ground texture",
                        li, w8[li], Path.GetFileName(af), aw));
            }
        }

        static void CheckAssets(string gamedataDir, string mapDir, object map, List<string> detail)
        {
            var packIndex = new HashSet<string>();
            foreach (var pack in Directory.EnumerateFiles(gamedataDir, "*.sanpack"))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(pack);
                foreach (var e in zip.Entries) packIndex.Add(e.FullName);
            }

            // Texture lookups are extension-agnostic - Load.cs strips the
            // extension and probes .dds first. Blueprints are NOT: the path
            // has to match exactly or the map breaks at runtime. "map/" paths
            // resolve against the map's own folder.
            bool Resolve(string p, bool exact)
            {
                if (p.StartsWith("map/", StringComparison.OrdinalIgnoreCase))
                {
                    string full = Path.Combine(mapDir, p.Substring(4));
                    if (File.Exists(full)) return true;
                    if (exact) return false;
                    string s = Path.ChangeExtension(full, null);
                    return new[] { ".dds", ".png", ".tga", ".jpg", ".bmp", ".exr" }.Any(e => File.Exists(s + e));
                }
                if (packIndex.Contains(p)) return true;
                if (exact) return false;
                string stem = Path.ChangeExtension(p, null);
                return new[] { ".dds", ".png", ".tga", ".jpg", ".bmp", ".exr" }.Any(e => packIndex.Contains(stem + e));
            }

            var tex = new List<string>();
            var bp = new List<string>();
            foreach (var s in (System.Collections.IEnumerable)Field(map, "stratumLayers"))
                foreach (var m in new[] { "albedo", "normal", "mask" })
                {
                    var mv = Field(s, m);
                    string p = mv == null ? null : (string)Field(mv, "path");
                    if (!string.IsNullOrEmpty(p)) tex.Add(p);
                }
            foreach (var d in (System.Collections.IEnumerable)Field(map, "decals"))
            {
                string p = (string)Field(d, "blueprintPath");
                if (!string.IsNullOrEmpty(p)) bp.Add(p);
            }
            foreach (var pr in (System.Collections.IEnumerable)Field(map, "props"))
            {
                string p = (string)Field(pr, "blueprintPath");
                if (!string.IsNullOrEmpty(p)) bp.Add(p);
            }
            var sky = Field(map, "skybox");
            string skyPath = sky == null ? null : (string)Field(sky, "path");
            if (!string.IsNullOrEmpty(skyPath) && skyPath != "empty") tex.Add(skyPath);

            var texU = tex.Distinct().OrderBy(x => x).ToList();
            var bpU = bp.Distinct().OrderBy(x => x).ToList();
            var missing = texU.Where(p => !Resolve(p, false)).Concat(bpU.Where(p => !Resolve(p, true))).ToList();
            if (missing.Count > 0)
                throw new Exception($"{missing.Count} referenced asset(s) missing from this build's gamedata:\n        "
                    + string.Join("\n        ", missing));
            detail.Add($"      assets        {texU.Count} texture + {bpU.Count} blueprint references, all resolve");
        }

        // Newtonsoft is not the only parser the map must satisfy: mapUtils.lua
        // decodes it with the game's own json.lua, and if that returns nil the
        // commander lands in the water with no spawn markers at all. KeraLua
        // and lua54.dll ship with the game's own map generator.
        static Assembly _keraLua;
        static bool LuaJsonCheck(string path, string gameRoot, Action<string> log)
        {
            string name = Path.GetFileName(path);
            string gen = Path.Combine(gameRoot, "engine", "Sanctuary-Map-Generation");
            if (_keraLua == null)
            {
                // lua54.dll resolves off PATH when KeraLua first P/Invokes it.
                Environment.SetEnvironmentVariable("PATH", gen + ";" + Environment.GetEnvironmentVariable("PATH"));
                _keraLua = Assembly.LoadFrom(Path.Combine(gen, "KeraLua.dll"));
            }
            string jsonLua = Path.Combine(gameRoot, "engine", "LJ", "lua", "common", "systems", "json.lua");

            dynamic L = Activator.CreateInstance(_keraLua.GetType("KeraLua.Lua"), new object[] { true });
            try
            {
                // json.lua ends in `return json`; load it as a chunk and call it.
                if (L.DoString("json = (function() " + File.ReadAllText(jsonLua) + " end)()"))
                    throw new Exception("could not load json.lua: " + L.ToString(-1, false));
                L.PushString(File.ReadAllText(path));
                L.SetGlobal("MAPTEXT");
                if (L.DoString("DECODED, ERRPOS, ERRMSG = json.decode(MAPTEXT)"))
                    throw new Exception("decode threw: " + L.ToString(-1, false));
                L.GetGlobal("DECODED");
                bool isNil = L.IsNil(-1);
                L.Pop(1);
                if (isNil)
                {
                    L.GetGlobal("ERRMSG"); string msg = L.ToString(-1, false); L.Pop(1);
                    L.GetGlobal("ERRPOS"); double pos = L.ToNumber(-1); L.Pop(1);
                    throw new Exception($"json.decode returned nil at byte {pos}: {msg}");
                }
                // Spot-check the fields LoadMapData reads first.
                L.DoString("CHK = tostring(DECODED.mapVersion) .. \"/\" .. tostring(DECODED.name) .. \"/\" .. tostring(DECODED.width)");
                L.GetGlobal("CHK"); string chk = L.ToString(-1, false); L.Pop(1);
                log($"LUA-OK   {name}   (mapVersion/name/width = {chk})");
                return true;
            }
            catch (Exception e)
            {
                log($"LUA-FAIL {name}");
                log($"         {e.GetBaseException().Message}");
                return false;
            }
            finally { L.Close(); }
        }
    }
}
