// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Community.HfAmp.Amp;

/// <summary>
/// Maps a radio frequency to the amplifier's filter band (1-8, the same index
/// the "7n" command and the status field use). Frequencies outside the amateur
/// allocations return null so that tuning around the dial never moves the amp.
/// Edges are deliberately the widest allocation in use worldwide.
/// </summary>
internal static class BandPlan
{
    private static readonly (long Lo, long Hi, int Band)[] Edges =
    [
        (1_800_000, 2_000_000, 1),     // 160 m
        (3_500_000, 4_000_000, 2),     // 80 m
        (5_250_000, 5_450_000, 3),     // 60 m  (amp band 3 = 60-40 m)
        (7_000_000, 7_300_000, 3),     // 40 m
        (10_100_000, 10_150_000, 4),   // 30 m
        (14_000_000, 14_350_000, 5),   // 20 m
        (18_068_000, 18_168_000, 6),   // 17 m  (amp band 6 = 17-15 m)
        (21_000_000, 21_450_000, 6),   // 15 m
        (24_890_000, 24_990_000, 7),   // 12 m  (amp band 7 = 12-10 m)
        (28_000_000, 29_700_000, 7),   // 10 m
        (50_000_000, 54_000_000, 8),   // 6 m
    ];

    public static int? FromHz(long hz)
    {
        foreach (var (lo, hi, band) in Edges)
        {
            if (hz >= lo && hz <= hi) return band;
        }
        return null;
    }
}
