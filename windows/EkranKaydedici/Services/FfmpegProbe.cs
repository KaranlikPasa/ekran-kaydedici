using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EkranKaydedici.Services;

public sealed record CaptureProfile(
    string CaptureMethod,
    string EncoderName,
    bool SupportsDdagrab);

public static class FfmpegProbe
{
    private static readonly string[] PreferredEncoders =
    [
        "h264_nvenc",
        "h264_qsv",
        "h264_amf",
        "libx264"
    ];

    public static string? FindFfmpegExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static CaptureProfile DetectProfile(string ffmpegPath)
    {
        var availableEncoders = QueryEncoders(ffmpegPath);
        var chosenEncoder = "libx264";

        foreach (var enc in PreferredEncoders)
        {
            if (availableEncoders.Contains(enc) && TestEncoder(ffmpegPath, enc))
            {
                chosenEncoder = enc;
                break;
            }
        }

        var ddagrabWorks = TestDdagrab(ffmpegPath);
        return new CaptureProfile(
            CaptureMethod: ddagrabWorks ? "ddagrab" : "gdigrab",
            EncoderName: chosenEncoder,
            SupportsDdagrab: ddagrabWorks);
    }

    private static HashSet<string> QueryEncoders(string ffmpegPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
            {
                return result;
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            foreach (var enc in PreferredEncoders)
            {
                if (output.Contains(enc, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(enc);
                }
            }
        }
        catch
        {
            // Fall back to software x264.
        }
        return result;
    }

    private static bool TestEncoder(string ffmpegPath, string encoder)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.1 -c:v {encoder} -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
            {
                return false;
            }
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TestDdagrab(string ffmpegPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -f lavfi -i ddagrab=framerate=10:draw_mouse=1 -frames:v 1 -vf hwdownload,format=bgra -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
            {
                return false;
            }
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Rectangle AlignEvenRectangle(Rectangle raw)
    {
        var width = Math.Max(2, raw.Width & ~1);
        var height = Math.Max(2, raw.Height & ~1);
        return new Rectangle(raw.X, raw.Y, width, height);
    }

    public static List<string> BuildVideoInputArgs(
        CaptureProfile profile,
        int fps,
        Rectangle? region = null)
    {
        var args = new List<string>();
        if (profile.SupportsDdagrab && region is null)
        {
            args.AddRange(["-f", "lavfi", "-i", $"ddagrab=framerate={fps}:draw_mouse=1"]);
        }
        else
        {
            args.AddRange(["-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture)]);
            if (region is { } r)
            {
                var aligned = AlignEvenRectangle(r);
                args.AddRange([
                    "-offset_x", aligned.X.ToString(CultureInfo.InvariantCulture),
                    "-offset_y", aligned.Y.ToString(CultureInfo.InvariantCulture),
                    "-video_size", $"{aligned.Width}x{aligned.Height}",
                ]);
            }
            args.AddRange(["-i", "desktop"]);
        }
        return args;
    }

    public static List<string> BuildVideoOutputFormatArgs(CaptureProfile profile, Rectangle? region = null)
    {
        if (profile.SupportsDdagrab && region is null)
        {
            return ["-vf", "hwdownload,format=bgra,format=yuv420p"];
        }
        return ["-pix_fmt", "yuv420p"];
    }

    public static List<string> BuildEncoderArgs(string encoderName, int fps)
    {
        var gop = fps.ToString(CultureInfo.InvariantCulture);
        return encoderName switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "vbr", "-cq", "24", "-g", gop, "-bf", "0"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "24", "-g", gop, "-bf", "0"],
            "h264_amf" => ["-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "24", "-qp_p", "24", "-g", gop, "-bf", "0"],
            _ => ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "24", "-g", gop, "-bf", "0"],
        };
    }
}

public static class PlaylistHelper
{
    public static List<string> ParseM3u8Segments(string m3u8Content, int maxSeconds)
    {
        var entries = new List<(double Duration, string Name)>();
        var pendingDuration = 1.0;

        foreach (var rawLine in m3u8Content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var valuePart = line["#EXTINF:".Length..].Split(',', 2)[0];
                if (double.TryParse(valuePart, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    pendingDuration = Math.Max(0.2, parsed);
                }
                else
                {
                    pendingDuration = 1.0;
                }
            }
            else if (!line.StartsWith('#'))
            {
                entries.Add((pendingDuration, line));
                pendingDuration = 1.0;
            }
        }

        if (entries.Count == 0)
        {
            return [];
        }

        var selected = new List<string>();
        double total = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            selected.Add(entries[i].Name);
            total += entries[i].Duration;
            if (total >= maxSeconds)
            {
                break;
            }
        }

        selected.Reverse();
        return selected;
    }
}
