using System.Drawing;
using System.IO;
using EkranKaydedici.Models;
using EkranKaydedici.Services;
using Xunit;

namespace EkranKaydedici.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ConfigRoundTripAndValidation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ekran_cfg_{Guid.NewGuid():N}.json");
        try
        {
            var cfg = new AppConfig
            {
                ReplayEnabled = false,
                ReplaySeconds = 60,
                SystemAudio = false,
                MicAudio = true,
                Fps = 60,
                RunAtStartup = false,
            };
            cfg.Save(tempFile);

            var loaded = AppConfig.Load(tempFile);
            Assert.False(loaded.ReplayEnabled);
            Assert.Equal(60, loaded.ReplaySeconds);
            Assert.False(loaded.SystemAudio);
            Assert.True(loaded.MicAudio);
            Assert.Equal(60, loaded.Fps);
            Assert.False(loaded.RunAtStartup);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ParseM3u8SegmentsReturnsNewestUpToMaxSeconds()
    {
        var m3u8 = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:1
        #EXTINF:1.000,
        stream0.ts
        #EXTINF:1.000,
        stream1.ts
        #EXTINF:1.000,
        stream2.ts
        #EXTINF:1.000,
        stream3.ts
        #EXTINF:1.000,
        stream4.ts
        """;

        var segs = PlaylistHelper.ParseM3u8Segments(m3u8, 3);
        Assert.Equal(["stream2.ts", "stream3.ts", "stream4.ts"], segs);
    }

    [Fact]
    public void AlignEvenRectangleFloorsOddDimensions()
    {
        var aligned = FfmpegProbe.AlignEvenRectangle(new Rectangle(15, 25, 301, 203));
        Assert.Equal(new Rectangle(15, 25, 300, 202), aligned);
    }

    [Fact]
    public void BuildVideoInputArgsHandlesDdagrabAndRegionFallback()
    {
        var profile = new CaptureProfile("ddagrab", "h264_nvenc", SupportsDdagrab: true);

        var fullArgs = FfmpegProbe.BuildVideoInputArgs(profile, 30, region: null);
        Assert.Contains("ddagrab=framerate=30:draw_mouse=1", fullArgs);

        var regionArgs = FfmpegProbe.BuildVideoInputArgs(profile, 30, new Rectangle(10, 20, 401, 301));
        Assert.Contains("gdigrab", regionArgs);
        Assert.Contains("400x300", regionArgs);
    }
}
