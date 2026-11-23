using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.DvrLive.Multiview;

/// <summary>Where one picture sits in the combined frame, in pixels of that frame.</summary>
public sealed record TileRect(int X, int Y, int W, int H);

/// <summary>
/// The combined frame for 1–4 pictures. "grid": equal pictures (two side by side; three as one
/// large and two small; four as 2×2). "big": the first picture large, the others in a column on
/// its right. The frame is only as tall as its pictures need (the TV letterboxes it).
/// </summary>
public static class MosaicLayout
{
    public const string Grid = "grid";
    public const string Big = "big";

    public static (int Width, int Height, IReadOnlyList<TileRect> Tiles) For(string layout, int count)
    {
        if (count < 1 || count > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Multiview takes 1 to 4 pictures.");
        }

        if (count == 1)
        {
            return (1920, 1080, new[] { new TileRect(0, 0, 1920, 1080) });
        }

        if (string.Equals(layout, Big, StringComparison.OrdinalIgnoreCase))
        {
            var tiles = new List<TileRect> { new(0, 0, 1440, 810) };
            for (var i = 1; i < count; i++)
            {
                tiles.Add(new TileRect(1440, (i - 1) * 270, 480, 270));
            }

            return (1920, 810, tiles);
        }

        return count switch
        {
            2 => (1920, 540, new[] { new TileRect(0, 0, 960, 540), new TileRect(960, 0, 960, 540) }),
            3 => (1920, 720, new[] { new TileRect(0, 0, 1280, 720), new TileRect(1280, 0, 640, 360), new TileRect(1280, 360, 640, 360) }),
            _ => (1920, 1080, new[] { new TileRect(0, 0, 960, 540), new TileRect(960, 0, 960, 540), new TileRect(0, 540, 960, 540), new TileRect(960, 540, 960, 540) })
        };
    }

    /// <summary>The ffmpeg filter graph: each input deinterlaced and scaled on the GPU, then tiled.</summary>
    public static string Filter(string layout, int count)
    {
        var (_, _, tiles) = For(layout, count);
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var t = tiles[i];
            parts.Add(string.Format(
                CultureInfo.InvariantCulture,
                "[{0}:v:0]hwmap=derive_device=qsv,format=qsv,vpp_qsv=deinterlace=2:w={1}:h={2}:framerate=60000/1001[v{0}]",
                i,
                t.W,
                t.H));
        }

        if (count == 1)
        {
            parts[0] = parts[0].Replace("[v0]", "[out]", StringComparison.Ordinal);
            return string.Join(';', parts);
        }

        var inputs = string.Concat(Enumerable.Range(0, count).Select(i => $"[v{i}]"));
        var positions = string.Join('|', tiles.Select(t => string.Format(CultureInfo.InvariantCulture, "{0}_{1}", t.X, t.Y)));
        parts.Add($"{inputs}xstack_qsv=inputs={count}:layout={positions}[out]");
        return string.Join(';', parts);
    }
}
