// DXT3 is a format Sanctuary cannot load.
//
// Unity has TextureFormat.DXT1 and TextureFormat.DXT5 and nothing for BC2, so
// a DXT3 texture arrives as no texture at all - which reads on screen as a
// clean white surface, not as an error. Seton's Clutch spent two rounds
// looking like snow because evgrass005_albedo.dds, carrying 80% of the ground
// across two layers, is DXT3.
//
// The tally says this is worth handling rather than skipping: Sanctuary ships
// zero DXT3 across 470 textures, Supreme Commander has 221 of them out of
// 2,462. Roughly one texture in eleven over there is unloadable over here.
//
// The transcode is close to free. Both formats are 16 bytes per 4x4 block and
// both put an 8-byte alpha block first and the same 8-byte colour block
// second, so the colour data - all of it, every mip - copies bit for bit and
// only the alpha block is rebuilt. Because the layout matches, the whole file
// walks as 16-byte strides from the end of the header without needing to know
// where one mip ends and the next begins.
public static partial class MapGen
{
    /// True if this DDS declares DXT3/BC2.
    public static bool IsDxt3(byte[] dds)
    {
        return dds != null && dds.Length >= 88 &&
               dds[0] == 0x44 && dds[1] == 0x44 && dds[2] == 0x53 && dds[3] == 0x20 &&
               (BitConverter.ToInt32(dds, 80) & 0x4) != 0 &&
               dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'T' && dds[87] == (byte)'3';
    }

    /// Rewrite a DXT3 DDS as DXT5 in place. Returns false and leaves the buffer
    /// untouched if it is not DXT3 or the block data is not a whole number of
    /// blocks, so a surprising file falls through rather than being corrupted.
    public static bool TranscodeDxt3ToDxt5(byte[] dds)
    {
        if (!IsDxt3(dds)) return false;
        int start = 128;
        int len = dds.Length - start;
        if (len <= 0 || len % 16 != 0) return false;

        var a8 = new int[16];
        for (int p = start; p < dds.Length; p += 16)
        {
            // DXT3 alpha: sixteen 4-bit values, two per byte, low nibble first.
            // Replicate the nibble rather than shifting, so 15 becomes 255 and
            // not 240 - an opaque texel has to stay opaque.
            for (int i = 0; i < 8; i++)
            {
                int by = dds[p + i];
                int v0 = by & 0x0f, v1 = (by >> 4) & 0x0f;
                a8[i * 2] = (v0 << 4) | v0;
                a8[i * 2 + 1] = (v1 << 4) | v1;
            }
            // The source has only sixteen distinct levels to begin with, so
            // eight well-placed ones lose very little.
            EncodeDxt5Alpha(dds, p, a8);
        }

        dds[87] = (byte)'5';
        return true;
    }

    /// Sixteen alpha values of one DXT5 block, in texel order.
    static void DecodeDxt5Alpha(byte[] dds, int p, int[] a8)
    {
        int a0 = dds[p], a1 = dds[p + 1];
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)dds[p + 2 + i] << (i * 8);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((bits >> (i * 3)) & 7);
            a8[i] = k == 0 ? a0 : k == 1 ? a1
                  : a0 > a1 ? ((8 - k) * a0 + (k - 1) * a1) / 7
                  : k == 6 ? 0 : k == 7 ? 255 : ((6 - k) * a0 + (k - 1) * a1) / 5;
        }
    }

    /// Write sixteen alpha values as a DXT5 alpha block. With a0 > a1 the six
    /// interior values are evenly spaced between the endpoints, so putting
    /// them at the block's own max and min spans exactly the range present.
    static void EncodeDxt5Alpha(byte[] dds, int p, int[] a8)
    {
        int lo = 255, hi = 0;
        for (int i = 0; i < 16; i++) { if (a8[i] < lo) lo = a8[i]; if (a8[i] > hi) hi = a8[i]; }

        dds[p] = (byte)hi;
        dds[p + 1] = (byte)lo;
        for (int i = 2; i < 8; i++) dds[p + i] = 0;

        if (hi == lo) return;                       // flat block: all index 0

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            // Nearest of the eight representable values.
            int best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int val = k == 0 ? hi : k == 1 ? lo : ((8 - k) * hi + (k - 1) * lo) / 7;
                int err = a8[i] - val; if (err < 0) err = -err;
                if (err < bestErr) { bestErr = err; best = k; }
            }
            bits |= (ulong)best << (i * 3);
        }
        for (int i = 0; i < 6; i++) dds[p + 2 + i] = (byte)(bits >> (i * 8));
    }

    // CC0 masks are too glossy for Sanctuary.
    //
    // The pack's masks carry each material's real smoothness in alpha, taken
    // from ambientCG's roughness maps: mean 63-229 of 255 across the 30
    // materials. The shipped maps' masks average 36. Physically fair, but it
    // reads in game as the whole map being wet - Sung Island's grass sat at
    // 115. Scaling the stratum's maskRemapMax.w did nothing on the Playtest
    // build, so the alpha itself is scaled, to the same per-role targets the
    // source-texture mode writes (RoleSmoothness).

    /// Scale a DXT5 texture's alpha so its top-mip mean lands on `target`,
    /// every mip alike, keeping the texture's own variation. Only ever
    /// lowers: returns false and leaves the buffer untouched if it is not
    /// DXT5, is not whole blocks, or is already at or below the target.
    public static bool ScaleDxt5AlphaMean(byte[] dds, double target)
    {
        if (dds == null || dds.Length < 128 ||
            dds[0] != 0x44 || dds[1] != 0x44 || dds[2] != 0x53 || dds[3] != 0x20 ||
            (BitConverter.ToInt32(dds, 80) & 0x4) == 0 ||
            dds[84] != (byte)'D' || dds[85] != (byte)'X' || dds[86] != (byte)'T' || dds[87] != (byte)'5')
            return false;
        int start = 128;
        if ((dds.Length - start) % 16 != 0) return false;

        int w = BitConverter.ToInt32(dds, 16), h = BitConverter.ToInt32(dds, 12);
        int topBlocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
        if (start + topBlocks * 16 > dds.Length) return false;

        var a8 = new int[16];
        double sum = 0;
        for (int b = 0; b < topBlocks; b++)
        {
            DecodeDxt5Alpha(dds, start + b * 16, a8);
            for (int i = 0; i < 16; i++) sum += a8[i];
        }
        double mean = sum / (topBlocks * 16.0);
        if (mean <= target) return false;

        double k = target / mean;
        for (int p = start; p < dds.Length; p += 16)
        {
            DecodeDxt5Alpha(dds, p, a8);
            for (int i = 0; i < 16; i++) a8[i] = (int)Math.Round(a8[i] * k);
            EncodeDxt5Alpha(dds, p, a8);
        }
        return true;
    }

    // 24-bit BGR is loadable but not renderable on terrain.
    //
    // The engine's DDSLoader reads a 24-bit DDS as R8G8B8 and, when the masks
    // say the bytes are really B,G,R, raises a flipBlueRed flag instead of
    // swapping them. Only Data.SetMaterialTexture honours that flag (as a
    // per-material "_brs" float); MapManager binds stratum layers with a plain
    // SetTexture, so the terrain shader samples red and blue swapped. On a
    // normal map that trades X for Z: every texel lies on its side, and the
    // layer renders near-black from above. Saltrock Colony's rock trench and
    // the rock on Fields of Isis went black this way - des_rock01_normal and
    // des_rock03a_normal are the uncompressed ones.
    //
    // 32-bit BGRA loads as B8G8R8A8 with no flag involved, so widening each
    // pixel by an opaque alpha byte fixes it losslessly. Every mip is the same
    // 3-byte stride, so the whole payload converts in one walk.

    /// True if this DDS is uncompressed 24-bit with the usual B,G,R byte order.
    public static bool IsBgr24(byte[] dds)
    {
        return dds != null && dds.Length >= 128 &&
               dds[0] == 0x44 && dds[1] == 0x44 && dds[2] == 0x53 && dds[3] == 0x20 &&
               (BitConverter.ToInt32(dds, 80) & 0x4) == 0 &&
               BitConverter.ToInt32(dds, 88) == 24 &&
               BitConverter.ToUInt32(dds, 92) == 0xff0000u &&
               BitConverter.ToUInt32(dds, 96) == 0x00ff00u &&
               BitConverter.ToUInt32(dds, 100) == 0x0000ffu;
    }

    /// A 24-bit BGR DDS rewritten as 32-bit BGRA with alpha 255, or null if
    /// this is not one (or its payload is not whole pixels), so a surprising
    /// file falls through untouched.
    public static byte[] ExpandBgr24ToBgra32(byte[] dds)
    {
        if (!IsBgr24(dds)) return null;
        int start = 128;
        int len = dds.Length - start;
        if (len <= 0 || len % 3 != 0) return null;

        int pixels = len / 3;
        var outb = new byte[start + pixels * 4];
        Buffer.BlockCopy(dds, 0, outb, 0, start);
        for (int i = 0, s = start, d = start; i < pixels; i++, s += 3, d += 4)
        {
            outb[d] = dds[s];
            outb[d + 1] = dds[s + 1];
            outb[d + 2] = dds[s + 2];
            outb[d + 3] = 255;
        }

        const int DDPF_ALPHAPIXELS = 0x1, DDSD_PITCH = 0x8, DDSD_LINEARSIZE = 0x80000;
        int flags = BitConverter.ToInt32(outb, 8);
        int width = BitConverter.ToInt32(outb, 16), height = BitConverter.ToInt32(outb, 12);
        if ((flags & DDSD_PITCH) != 0)
            BitConverter.GetBytes(width * 4).CopyTo(outb, 20);
        else if ((flags & DDSD_LINEARSIZE) != 0)
            BitConverter.GetBytes(width * height * 4).CopyTo(outb, 20);
        BitConverter.GetBytes(BitConverter.ToInt32(outb, 80) | DDPF_ALPHAPIXELS).CopyTo(outb, 80);
        BitConverter.GetBytes(32).CopyTo(outb, 88);
        BitConverter.GetBytes(0xff000000u).CopyTo(outb, 104);
        return outb;
    }
}
