using System.IO.Compression;
using System.Linq;

namespace SanctuaryMapConverter.Core
{
    // Ports of tools/Export-ScTextures.ps1, Export-Cc0Textures.ps1 and
    // Write-NeutralMask.ps1 - the two texture modes.
    //
    // Source mode extracts the map's own textures from the user's env.scd.
    // The tool ships no Supreme Commander art: the user points at their own
    // Forged Alliance install and their licence does the licensing. CC0 mode
    // copies from the bundled ambientCG-derived library, which is
    // redistributable by construction.
    public sealed class ExportResult
    {
        public int Copied, Transcoded, Widened, Inexact;
        public readonly List<string> Missing = new();
        public readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Normals = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Masks = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double[]> Remaps = new(StringComparer.OrdinalIgnoreCase);
        public string MaskName = "sc_neutral_mask.tga";
    }

    public static class TextureExport
    {
        /// The shared neutral stratum mask. Not mid-grey: Sanctuary's mask is
        /// HDRP's mask map (R metallic, G ambient occlusion, B detail,
        /// A smoothness), and these are the means of the 127 masks the game
        /// ships. Mid-grey put a wet-plastic sheen over every converted map.
        public static void WriteNeutralMask(string path)
        {
            const int res = 4;
            var bytes = new byte[18 + res * res * 4];
            bytes[2] = 2;                          // uncompressed true-colour
            bytes[12] = res; bytes[14] = res;
            bytes[16] = 32;                        // bits per pixel
            bytes[17] = 0x28;                      // 8 alpha bits, top-left origin
            for (int i = 18; i < bytes.Length; i += 4)
            {
                bytes[i] = 150;                    // B - detail
                bytes[i + 1] = 219;                // G - ambient occlusion
                bytes[i + 2] = 0;                  // R - metallic
                bytes[i + 3] = 36;                 // A - smoothness
            }
            File.WriteAllBytes(path, bytes);
        }

        /// Raw bytes of one source texture, from the map's own folder for
        /// /maps/ paths or from env.scd otherwise. Null when unavailable.
        public static byte[] ReadSourceBytes(string scdPath, string sourcePath, string mapsRoot)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            string key = sourcePath.TrimStart('/');
            if (mapsRoot != null && key.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            {
                string cand = Path.Combine(mapsRoot, key.Substring(5).Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(cand) ? File.ReadAllBytes(cand) : null;
            }
            if (scdPath == null || !File.Exists(scdPath)) return null;
            using var zip = ZipFile.OpenRead(scdPath);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.TrimStart('/').Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                return ms.ToArray();
            }
            return null;
        }

        /// Source-texture mode: extract each referenced texture from env.scd,
        /// or from the map's own folder for /maps/ paths. DXT3 - a format
        /// Unity cannot load - is transcoded to DXT5 with the colour block
        /// copied bit-exact; 24-bit BGR - which the terrain samples with red
        /// and blue swapped - is widened to 32-bit BGRA.
        public static ExportResult ExportSource(
            string scdPath, string[] texturePaths, string[] normalPaths,
            string destDir, string mapsRoot, Action<string> log)
        {
            Directory.CreateDirectory(destDir);
            var r = new ExportResult();

            using var zip = ZipFile.OpenRead(scdPath);
            var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries) index[e.FullName.TrimStart('/')] = e;

            // Leaf name -> the source path that claimed it. Two different
            // textures can share a file name once map-local ones are in play,
            // and silently copying one over the other puts the wrong ground
            // on a layer without anything failing.
            var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in (texturePaths ?? Array.Empty<string>()).Concat(normalPaths ?? Array.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string key = p.TrimStart('/');
                string leaf = Path.GetFileName(key);
                if (claimed.TryGetValue(leaf, out var owner) && !owner.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    int n = 2;
                    string stem = Path.GetFileNameWithoutExtension(leaf), ext = Path.GetExtension(leaf);
                    while (claimed.ContainsKey($"{stem}_{n}{ext}")) n++;
                    leaf = $"{stem}_{n}{ext}";
                }

                byte[] bytes = null;
                if (index.TryGetValue(key, out var entry))
                {
                    using var ms = new MemoryStream();
                    using (var s = entry.Open()) s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                else if (mapsRoot != null && key.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                {
                    string cand = Path.Combine(mapsRoot, key.Substring(5).Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(cand)) bytes = File.ReadAllBytes(cand);
                }
                if (bytes == null) { r.Missing.Add(key); continue; }

                if (MapGen.TranscodeDxt3ToDxt5(bytes)) r.Transcoded++;
                var wide = MapGen.ExpandBgr24ToBgra32(bytes);
                if (wide != null) { bytes = wide; r.Widened++; }

                // Only recorded once the file is known to exist - a name here
                // with no file behind it becomes a layer pointing at nothing.
                r.Names[p] = leaf;
                claimed[leaf] = key;

                string outPath = Path.Combine(destDir, leaf);
                if (!File.Exists(outPath)) { File.WriteAllBytes(outPath, bytes); r.Copied++; }
            }

            // Per-role neutral masks: the same flat neutral in metallic, AO
            // and detail, the role's own smoothness in alpha - mud glistens,
            // grass does not. Unused slots keep the shared fallback below.
            foreach (var p in texturePaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p) || !r.Names.ContainsKey(p)) continue;
                string role = MapGen.ScTextureRole(p);
                string maskFile = $"sc_mask_{role}.tga";
                string maskPath = Path.Combine(destDir, maskFile);
                if (!File.Exists(maskPath)) MapGen.WriteMaskTga(maskPath, MapGen.RoleSmoothness(role));
                r.Masks[p] = maskFile;
            }

            WriteNeutralMask(Path.Combine(destDir, r.MaskName));
            return r;
        }

        /// CC0 mode: look each source texture up in the substitution table and
        /// copy the matched material's albedo, normal and mask from the
        /// bundled pack, with the solved per-channel tone correction.
        public static ExportResult ExportCc0(
            string[] texturePaths, string destDir, string packDir, string tableCsv, Action<string> log)
        {
            Directory.CreateDirectory(destDir);
            var r = new ExportResult();
            var table = SubstitutionTable.Load(tableCsv);

            foreach (var p in texturePaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!table.TryGetValue(p.ToLowerInvariant(), out var row)) { r.Missing.Add(p); continue; }

                string albedoSrc = Path.Combine(packDir, row.Cc0 + "_albedo.dds");
                if (!File.Exists(albedoSrc)) { r.Missing.Add(p); continue; }

                foreach (var (src, kind) in new[]
                {
                    (albedoSrc, 'a'),
                    (Path.Combine(packDir, row.Cc0 + "_normal.dds"), 'n'),
                    (Path.Combine(packDir, row.Cc0 + "_mask.dds"), 'm'),
                })
                {
                    if (!File.Exists(src)) continue;
                    string leaf = Path.GetFileName(src);
                    string outPath = Path.Combine(destDir, leaf);
                    if (!File.Exists(outPath)) { File.Copy(src, outPath); r.Copied++; }
                    switch (kind)
                    {
                        case 'a': r.Names[p] = leaf; break;
                        case 'n': r.Normals[p] = leaf; break;
                        case 'm': r.Masks[p] = leaf; break;
                    }
                }
                r.Remaps[p] = new[] { row.RemapR, row.RemapG, row.RemapB };
                if (!row.Exact) r.Inexact++;
            }

            WriteNeutralMask(Path.Combine(destDir, r.MaskName));
            return r;
        }
    }

    /// docs/texture-map.csv: the solved mapping from every corpus texture to
    /// its CC0 substitute, with per-channel tone correction. Generated by the
    /// matching pipeline and shipped with the app as data.
    public sealed class SubstitutionRow
    {
        public string Cc0;
        public double RemapR, RemapG, RemapB;
        public bool Exact;
    }

    public static class SubstitutionTable
    {
        public static Dictionary<string, SubstitutionRow> Load(string csvPath)
        {
            var map = new Dictionary<string, SubstitutionRow>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2) return map;

            var header = ParseCsvLine(lines[0]);
            int iPath = Array.IndexOf(header, "ScPath");
            int iCc0 = Array.IndexOf(header, "Cc0");
            int iR = Array.IndexOf(header, "RemapR");
            int iG = Array.IndexOf(header, "RemapG");
            int iB = Array.IndexOf(header, "RemapB");
            int iEx = Array.IndexOf(header, "Exact");

            for (int i = 1; i < lines.Length; i++)
            {
                var f = ParseCsvLine(lines[i]);
                if (f.Length <= Math.Max(iB, iCc0)) continue;
                map[f[iPath].ToLowerInvariant()] = new SubstitutionRow
                {
                    Cc0 = f[iCc0],
                    RemapR = double.Parse(f[iR], System.Globalization.CultureInfo.InvariantCulture),
                    RemapG = double.Parse(f[iG], System.Globalization.CultureInfo.InvariantCulture),
                    RemapB = double.Parse(f[iB], System.Globalization.CultureInfo.InvariantCulture),
                    Exact = f[iEx].Equals("True", StringComparison.OrdinalIgnoreCase),
                };
            }
            return map;
        }

        static string[] ParseCsvLine(string line)
        {
            var outp = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool q = false;
            foreach (char c in line)
            {
                if (c == '"') q = !q;
                else if (c == ',' && !q) { outp.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            outp.Add(sb.ToString());
            return outp.ToArray();
        }
    }
}
