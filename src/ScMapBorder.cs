// Authoring a border around a converted map.
//
// Sanctuary never shows a map edge. Beyond the terrain the engine's
// InfiniteTerrain component builds eight more terrains, each the heightmap
// mirrored across the edge it touches and flattened towards the mean height
// on its far side, and the terrain shader carries the splat and tint over
// them. On a map that fills its whole terrain that is eight more copies of
// the map: every cliff and every bay reflected back at the player from just
// outside the playable area. There is no map-file switch for it - the only
// way to turn it off is a developer quick-launch flag.
//
// The developers' own maps never look like that, because none of them fills
// its terrain. All four hand-made maps (The Forge, White Desert, Two Step
// Shuffle, There Is Time) put the playable rectangle in the centre of a
// terrain twice its size and fill the border with ground that continues the
// edge but carries none of the map's features: measured against The Forge,
// the border matches the interior to 0.14 m one cell out and has diverged by
// 16 m two hundred cells out. The engine then mirrors the border rather than
// the map, and the fog that starts at the PlayableArea rectangle has
// something bland to fade over.
//
// This file gives a converted map the same treatment. The heightmap stays
// byte-exact in the centre. The border is made of two things cross-faded:
//
//   * a thin band of the map's own edge reflected outward, blurred and
//     warped a little more with every metre, so the ground and the textures
//     are continuous across the playable edge - no crease, no seam;
//
//   * synthetic ground: the map's mean height around the point read from
//     (the pyramid's coarsest level, an eighth of the map wide) with rolling
//     hills on it whose amplitude follows the map's own relief. Nothing of
//     the map's shapes is left in it.
//
// The fade from the first to the second is complete by 15% of the border.
// That number is the lesson of the first attempt, which blurred and warped
// the reflection in proportion to distance and nothing else: continuous and
// unrecognisable far out, but on a map with 500 m landmasses the first
// couple of hundred metres were still a mirror, and every landmass on the
// edge read in-game as a butterfly folded over the playable boundary. The
// reflection is only there to make the join seamless; it has to be gone
// before it has room to be recognised. The splat weights are read the same
// way, so the textures follow the ground out.
//
// Twice the size is not a stylistic choice. Unity's terrain wants a
// heightmap resolution of 2^n + 1, so with the interior kept sample-exact the
// only extension is to the next power of two - which happens to be exactly
// the proportion the shipped maps use.

using System;

public static partial class MapGen
{
    /// Width of the border ExtendBorder added on each side, in metres. Zero
    /// until it has run; the converter offsets every world position by it.
    public static int Border;

    /// Reflect an index into [0, size]: the mirror image across whichever edge
    /// it crossed. One reflection suffices - callers never stray more than a
    /// full map width.
    static int Reflect(int i, int size)
    {
        if (i < 0) return -i;
        if (i > size) return 2 * size - i;
        return i;
    }

    /// Grow the map to twice its size with an authored border on every side.
    /// Expects the vertex-aligned state AdoptScMap and AdoptScSplat leave
    /// behind (one cell per metre, splat on the heightmap grid) and returns
    /// the border width. Slope and walkability are rebuilt for the new size.
    public static int ExtendBorder()
    {
        int size = (int)MapSize;
        int n = HRes;
        if (Height == null || n != size + 1 || SRes != n || Layers == null)
            throw new InvalidOperationException("ExtendBorder needs a vertex-aligned map straight from AdoptScMap and AdoptScSplat");

        int b = size / 2;
        int N = 2 * size + 1;

        // The coarsest level has eight cells across, so the far border is
        // blurred over an eighth of the map: 256 -> 5 levels, 1024 -> 7.
        int levels = 0;
        while ((size >> (levels + 1)) >= 8) levels++;

        // The synthetic ground's hills follow the map's own relief - its
        // standard deviation, not its range, so one peak does not set the
        // scale - within reason: a flat map stays flat, a mountain map does
        // not get a mountain range for a border.
        double sum = 0, sq = 0;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                float h = Height[r, c];
                sum += h; sq += (double)h * h;
            }
        double mean = sum / ((double)n * n);
        float sigma = (float)Math.Sqrt(Math.Max(0.0, sq / ((double)n * n) - mean * mean));
        float hillAmp = Math.Clamp(sigma, 2f, 20f);

        // One pyramid per field. Heights stay float (they are sampled with a
        // cubic and creases would show); splat weights are bytes to begin
        // with and are read bilinearly, so they can stay bytes.
        var hp = BuildPyramid(Height, size, levels);
        var lp = new byte[9][][,];
        var tmp = new float[n, n];
        for (int li = 1; li <= 8; li++)
        {
            var src = Layers[li];
            bool any = false;
            for (int r = 0; r < n && !any; r++)
                for (int c = 0; c < n; c++)
                    if (src[r, c] != 0) { any = true; break; }
            if (!any) continue;      // a dropped layer stays empty
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    tmp[r, c] = src[r, c];
            lp[li] = ToBytes(BuildPyramid(tmp, size, levels));
        }

        var newH = new float[N, N];
        var newLayers = new byte[9][,];
        for (int li = 1; li <= 8; li++) newLayers[li] = new byte[N, N];

        float warpScale = size * 0.14f;
        float fadeBand = 0.15f * b;
        for (int R = 0; R < N; R++)
        {
            int r = R - b;
            int dr = r < 0 ? -r : (r > size ? r - size : 0);
            for (int C = 0; C < N; C++)
            {
                int c = C - b;
                int dc = c < 0 ? -c : (c > size ? c - size : 0);
                int d = Math.Max(dr, dc);
                if (d == 0)
                {
                    newH[R, C] = Height[r, c];
                    for (int li = 1; li <= 8; li++) newLayers[li][R, C] = Layers[li][r, c];
                    continue;
                }

                float t = d / (float)b;
                float L = levels * (float)Math.Sqrt(t);

                // Where this cell reads from: the reflection, pushed around by
                // a warp that is zero at the edge and up to 0.8 of the
                // distance out. Two noise fields, one per axis.
                float warp = 0.8f * d;
                float wu = (Fbm(C, R, 2731, 3, warpScale) - 0.5f) * 2f * warp;
                float wv = (Fbm(C, R, 6089, 3, warpScale) - 0.5f) * 2f * warp;
                float u = Reflect(c, size) + wu;
                float v = Reflect(r, size) + wv;

                // The near band: the reflection, blurred. Continuous with the
                // edge, but for a big feature still recognisably its mirror.
                float near = SamplePyramid(hp, L, u, v);

                // The far ground: the mean height of the map's edge nearest
                // this cell (the coarsest level, an eighth of the map wide,
                // read at the edge itself rather than through the reflection
                // - a blob the size of an island survives that blur, and
                // reflected it would be stamped again at every terrain edge),
                // easing towards the map's overall mean further out, with
                // rolling hills on it. Nothing of the map's shapes is left in
                // it, so it is what most of the border is made of; the
                // cross-fade from the reflection is done by 15% of the
                // border, before the mirror has room to read as one.
                // Clamped from the cell's own coordinates, not the reflected
                // ones: reflected, a cell half a map out reads the map's
                // centre, and the far ground would carry the map again.
                float eu = Math.Clamp(c + wu, 0f, size), ev = Math.Clamp(r + wv, 0f, size);
                float edgeMean = SamplePyramid(hp, levels, eu, ev);
                float far = Lerp(edgeMean, (float)mean, Smooth(t))
                          + (Fbm(C, R, 7331, 4, size * 0.2f) - 0.5f) * 2f * hillAmp;
                float s = Smooth(d / fadeBand);
                newH[R, C] = Math.Clamp(Lerp(near, far, s), 0f, MaxHeight);

                for (int li = 1; li <= 8; li++)
                {
                    if (lp[li] == null) continue;
                    float wn = SamplePyramid(lp[li], L, u, v);
                    float wf = SamplePyramid(lp[li], levels, eu, ev);
                    newLayers[li][R, C] = (byte)Math.Round(Math.Clamp(Lerp(wn, wf, s), 0f, 255f));
                }
            }
        }

        Height = newH;
        Layers = newLayers;
        HRes = N;
        SRes = N;
        MapSize = 2 * size;
        Border = b;
        RebuildSlope();
        BuildWalkable();
        return b;
    }

    /// A blur pyramid over a vertex-aligned grid. Level k has size >> k cells
    /// (one more point) over the same extent, each level a 1-2-1 binomial of
    /// the one below with the boundary reflected - so a blur of the mirrored
    /// map is the mirror of the blurred map, and the border can be sampled
    /// from the interior's pyramid alone.
    static float[][,] BuildPyramid(float[,] level0, int size, int levels)
    {
        var pyr = new float[levels + 1][,];
        pyr[0] = level0;
        int s = size;
        for (int k = 1; k <= levels; k++)
        {
            var src = pyr[k - 1];
            int m = s / 2;                // cells at this level
            var rows = new float[s + 1, m + 1];
            for (int r = 0; r <= s; r++)
                for (int c = 0; c <= m; c++)
                {
                    int cc = 2 * c;
                    rows[r, c] = 0.25f * src[r, Reflect(cc - 1, s)] + 0.5f * src[r, cc] + 0.25f * src[r, Reflect(cc + 1, s)];
                }
            var dst = new float[m + 1, m + 1];
            for (int r = 0; r <= m; r++)
            {
                int rr = 2 * r;
                for (int c = 0; c <= m; c++)
                    dst[r, c] = 0.25f * rows[Reflect(rr - 1, s), c] + 0.5f * rows[rr, c] + 0.25f * rows[Reflect(rr + 1, s), c];
            }
            pyr[k] = dst;
            s = m;
        }
        return pyr;
    }

    static byte[][,] ToBytes(float[][,] pyr)
    {
        var outp = new byte[pyr.Length][,];
        for (int k = 0; k < pyr.Length; k++)
        {
            var src = pyr[k];
            int m = src.GetLength(0);
            var dst = new byte[m, m];
            for (int r = 0; r < m; r++)
                for (int c = 0; c < m; c++)
                    dst[r, c] = (byte)Math.Round(Math.Clamp(src[r, c], 0f, 255f));
            outp[k] = dst;
        }
        return outp;
    }

    /// Sample a pyramid at a fractional level, blending the two nearest
    /// levels. Coordinates are in level-0 cells; taps outside the grid are
    /// reflected back in, like the pyramid itself.
    static float SamplePyramid(float[][,] pyr, float level, float x, float y)
    {
        int top = pyr.Length - 1;
        level = Math.Clamp(level, 0f, top);
        int k0 = (int)Math.Floor(level);
        int k1 = Math.Min(top, k0 + 1);
        float f = level - k0;
        float a = SampleCubic(pyr[k0], x / (1 << k0), y / (1 << k0));
        if (f <= 0.001f || k1 == k0) return a;
        float bb = SampleCubic(pyr[k1], x / (1 << k1), y / (1 << k1));
        return Lerp(a, bb, f);
    }

    static float SamplePyramid(byte[][,] pyr, float level, float x, float y)
    {
        int top = pyr.Length - 1;
        level = Math.Clamp(level, 0f, top);
        int k0 = (int)Math.Floor(level);
        int k1 = Math.Min(top, k0 + 1);
        float f = level - k0;
        float a = SampleLinear(pyr[k0], x / (1 << k0), y / (1 << k0));
        if (f <= 0.001f || k1 == k0) return a;
        float bb = SampleLinear(pyr[k1], x / (1 << k1), y / (1 << k1));
        return Lerp(a, bb, f);
    }

    /// Catmull-Rom on a float grid: a bilinear tent would crease the
    /// dissolved slopes at every coarse-level cell boundary.
    static float SampleCubic(float[,] g, float x, float y)
    {
        int m = g.GetLength(0) - 1;       // cells
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float tx = x - x0, ty = y - y0;
        float acc = 0f;
        for (int j = -1; j <= 2; j++)
        {
            float wy = CatmullRom(ty - j);
            int ry = Reflect(y0 + j, m);
            float row = 0f;
            for (int i = -1; i <= 2; i++)
                row += CatmullRom(tx - i) * g[ry, Reflect(x0 + i, m)];
            acc += wy * row;
        }
        return acc;
    }

    /// Bilinear on a byte grid: plenty for splat weights.
    static float SampleLinear(byte[,] g, float x, float y)
    {
        int m = g.GetLength(0) - 1;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float tx = x - x0, ty = y - y0;
        int xa = Reflect(x0, m), xb = Reflect(x0 + 1, m);
        int ya = Reflect(y0, m), yb = Reflect(y0 + 1, m);
        float r0 = Lerp(g[ya, xa], g[ya, xb], tx);
        float r1 = Lerp(g[yb, xa], g[yb, xb], tx);
        return Lerp(r0, r1, ty);
    }

    static float CatmullRom(float t)
    {
        t = Math.Abs(t);
        if (t < 1f) return 1.5f * t * t * t - 2.5f * t * t + 1f;
        if (t < 2f) return -0.5f * t * t * t + 2.5f * t * t - 4f * t + 2f;
        return 0f;
    }
}
