// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Encoders;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Совместимостные тесты видеоэнкодеров на реальном FFmpeg: аргументы, собранные продакшен-кодом
/// (<see cref="CpuEncoder"/>, <see cref="NvencEncoder"/>), прогоняются через настоящий энкодер,
/// результат проверяется полным декодированием и зондированием через ffprobe.
/// Ловят сюрпризы, невидимые на уровне строк: неверные имена параметров x264/x265,
/// несовместимые связки tune/profile/pix_fmt, тег hvc1 не с тем кодеком, флаги NVENC.
/// Категория RequiresFfmpeg: без рабочего FFmpeg тесты завершаются Inconclusive (пропуск),
/// поэтому набор безопасен для CI. Локальный прогон — фильтром RequiresFfmpeg.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
[TestCategory("RequiresFfmpeg")]
public partial class EncoderFfmpegCompatibilityTests
{
    private static string _ffmpeg = null!;
    private static string? _ffprobe;
    private static string _workDir = null!;
    private static string _source = null!;

    /// <summary>
    /// Глобальный замок сериализации NVENC-запусков (см. EncodeWithNvenc).
    /// </summary>
    private static readonly object NvencEncodeLock = new();

    private readonly CpuEncoder _cpu = new();
    private readonly NvencEncoder _nvenc = new();

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        string? ffmpeg = FfmpegTestHost.ResolveFfmpeg();
        if (ffmpeg == null)
        {
            Assert.Inconclusive("Рабочий FFmpeg не найден (PATH, KTools, bin репозитория): совместимостные тесты пропущены.");
        }
        _ffmpeg = ffmpeg;
        _ffprobe = FfmpegTestHost.ResolveFfprobe(ffmpeg);

        _workDir = Path.Combine(Path.GetTempPath(), "ktools_ffcompat_" + Path.GetRandomFileName().Replace(".", ""));
        Directory.CreateDirectory(_workDir);
        _source = Path.Combine(_workDir, "source.mkv");

        // Эталон: секунда несжатого видео 320x240 без зависимости от кодеков сборки.
        var gen = FfmpegTestHost.Run(
            _ffmpeg,
            $"-y -f lavfi -i \"testsrc2=size=320x240:rate=15:duration=1\" -c:v rawvideo -pix_fmt yuv420p \"{_source}\"",
            120000);
        if (gen.ExitCode != 0 || !File.Exists(_source))
        {
            Assert.Inconclusive($"Не удалось сгенерировать эталонный источник: {gen.ErrorTail}");
        }
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        try
        {
            if (_workDir != null && Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // Очистка best-effort: мусор в %TEMP% не должен ронять набор.
        }
    }

    [TestMethod]
    public void Encode_CpuLibx264CrfMp4_ProducesDecodableH264()
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 20 }
        });
        var context = Ctx(container: ".mp4");

        // Act
        var (run, output, command) = EncodeWithCpu(settings, context, ".mp4", "x264_crf");

        // Assert
        command.Should().NotContain("-tag:v", "H.264 не должен получать тег hvc1");
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx264AbrMkv_ProducesDecodableH264()
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "Битрейт (ABR)" },
            { "x264_v_bitrate", 1000 },
            { "x264_auto_bitrate", true }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "x264_abr");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx264Lossless_ProducesDecodableH264()
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>());
        var context = Ctx(".mkv", lossless: true);

        // Act
        var (run, output, command) = EncodeWithCpu(settings, context, ".mkv", "x264_lossless");

        // Assert
        command.Should().Contain("-qp");
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx264TuneProfile_StreamingComboAccepted()
    {
        // Arrange: связка для стриминга — частая причина сюрпризов (профиль режет инструменты tune).
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 23 },
            { "x264_advanced_options", true },
            { "x264_tune", "zerolatency" },
            { "x264_profile", "baseline" }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mp4"), ".mp4", "x264_stream");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx264Force10Bit_FallsBackToYuv420p()
    {
        // Arrange
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_rc", "CRF" },
            { "x264_crf", 23 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mp4", force10Bit: true), ".mp4", "x264_10bit");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx264WithScaleFilter_ProducesDownscaledH264()
    {
        // Arrange: совместимость аргументов энкодера с цепочкой -vf.
        var settings = CpuSettings("AVC / H.264", new Dictionary<string, object>
        {
            { "x264_preset", "veryfast" },
            { "x264_rc", "CRF" },
            { "x264_crf", 23 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(
            settings, Ctx(".mkv"), ".mkv", "x264_scale", videoFilter: "scale=160:120:flags=lanczos");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
        var probe = FfmpegTestHost.ProbeVideoStream(_ffprobe, _ffmpeg, output);
        probe["width"].Should().Be("160");
        probe["height"].Should().Be("120");
    }

    [TestMethod]
    public void Encode_CpuLibx265CrfMkv_ProducesDecodableHevc()
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "x265_crf");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx265AbrMkv_ProducesDecodableHevc()
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "Битрейт (ABR)" },
            { "x265_v_bitrate", 1000 },
            { "x265_auto_bitrate", true }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "x265_abr");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx265Lossless_ProducesDecodableHevc()
    {
        // Arrange
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>());
        var context = Ctx(".mkv", lossless: true);

        // Act
        var (run, output, command) = EncodeWithCpu(settings, context, ".mkv", "x265_lossless");

        // Assert
        command.Should().Contain("lossless=1");
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx265AdvancedParams_ExactUnitComboAccepted()
    {
        // Arrange: та же комбинация, что в юнит-тестах — имена x265-параметров обязаны существовать.
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 },
            { "x265_advanced_options", true },
            { "x265_tune", "grain" },
            { "x265_aq_mode", "3" },
            { "x265_aq_strength", 2.0f },
            { "x265_lookahead", 50 },
            { "x265_bframes", 8 },
            { "x265_b_adapt", "0" },
            { "x265_psy_rd", 3.0f },
            { "x265_psy_rdoq", 10.0f },
            { "x265_deblock", "-1:-1" },
            { "x265_no_sao", true },
            { "x265_no_fast_pskip", true }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mkv"), ".mkv", "x265_adv");

        // Assert
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_CpuLibx265Mp4_Hvc1TagAccepted()
    {
        // Arrange: известная ловушка — Tag hvc1 incompatible with output codec при неверном кодеке.
        var settings = CpuSettings("HEVC / H.265", new Dictionary<string, object>
        {
            { "x265_preset", "veryfast" },
            { "x265_rc", "CRF" },
            { "x265_crf", 28 }
        });

        // Act
        var (run, output, command) = EncodeWithCpu(settings, Ctx(".mp4"), ".mp4", "x265_hvc1");

        // Assert
        command.Should().Contain("-tag:v");
        command.Should().Contain("hvc1");
        command.Should().Contain("+faststart");
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_NvencHevc_IfGpuPresent_ProducesDecodableHevc()
    {
        // Arrange
        if (!FfmpegTestHost.IsEncoderListed(_ffmpeg, "hevc_nvenc"))
        {
            Assert.Inconclusive("Сборка FFmpeg без hevc_nvenc: тест пропущен.");
        }
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "HEVC / H.265" },
            { "nvenc_preset", "p1" },
            { "nvenc_rc", "vbr" },
            { "v_bitrate", 2000 },
            { "auto_bitrate", true }
        };

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", "nvenc_hevc");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "hevc", expectedPixFmt: "yuv420p");
    }

    [TestMethod]
    public void Encode_NvencH264_IfGpuPresent_ProducesDecodableH264()
    {
        // Arrange
        if (!FfmpegTestHost.IsEncoderListed(_ffmpeg, "h264_nvenc"))
        {
            Assert.Inconclusive("Сборка FFmpeg без h264_nvenc: тест пропущен.");
        }
        var settings = new Dictionary<string, object>
        {
            { "nvenc_codec", "AVC / H.264" },
            { "nvenc_preset", "p1" },
            { "nvenc_rc", "vbr" },
            { "v_bitrate", 2000 },
            { "auto_bitrate", true }
        };

        // Act
        var (run, output, command) = EncodeWithNvenc(settings, Ctx(".mkv"), ".mkv", "nvenc_h264");

        // Assert
        AssertNvencSuccessOrMissingGpu(run, command);
        ShouldProduceDecodableVideo(run, output, command, expectedCodec: "h264", expectedPixFmt: "yuv420p");
    }

    private static Dictionary<string, object> CpuSettings(string codec, Dictionary<string, object> codecSettings)
    {
        var settings = new Dictionary<string, object> { { CpuEncoder.CodecSettingKey, codec } };
        foreach (var kv in codecSettings)
        {
            settings[kv.Key] = kv.Value;
        }
        return settings;
    }

    private static EncoderSharedContext Ctx(string container, bool lossless = false, bool force10Bit = false)
    {
        return new EncoderSharedContext(IsLossless: lossless, Force10Bit: force10Bit, ContainerExtension: container);
    }

    private (FfmpegTestHost.RunResult Run, string Output, string Command) EncodeWithCpu(
        Dictionary<string, object> settings,
        EncoderSharedContext context,
        string extension,
        string testName,
        string? videoFilter = null,
        int timeoutMs = 180000)
    {
        // Порядок флагов зеркалит VideoEncodingScript: энкодер, -vf, тег контейнера, faststart.
        var parts = new List<string> { "-y", "-i", Quote(_source) };
        parts.AddRange(_cpu.BuildEncoderArguments(settings, context));
        if (videoFilter != null)
        {
            parts.Add("-vf");
            parts.Add(videoFilter);
        }
        string? tag = _cpu.GetContainerTag(settings, context);
        if (!string.IsNullOrEmpty(tag))
        {
            parts.Add("-tag:v");
            parts.Add(tag);
        }
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("-movflags");
            parts.Add("+faststart");
        }
        parts.Add("-an");
        string output = Path.Combine(_workDir, testName + extension);
        parts.Add(Quote(output));

        string command = string.Join(" ", parts);
        var run = FfmpegTestHost.Run(_ffmpeg, command, timeoutMs);
        return (run, output, command);
    }

    private (FfmpegTestHost.RunResult Run, string Output, string Command) EncodeWithNvenc(
        Dictionary<string, object> settings,
        EncoderSharedContext context,
        string extension,
        string testName,
        int timeoutMs = 180000)
    {
        var parts = new List<string> { "-y", "-i", Quote(_source) };
        parts.AddRange(_nvenc.BuildEncoderArguments(settings, context));
        string? tag = _nvenc.GetContainerTag(settings, context);
        if (!string.IsNullOrEmpty(tag))
        {
            parts.Add("-tag:v");
            parts.Add(tag);
        }
        parts.Add("-an");
        string output = Path.Combine(_workDir, testName + extension);
        parts.Add(Quote(output));

        string command = string.Join(" ", parts);
        // Сериализация NVENC-запусков: параллельные сессии на одном GPU дают
        // случайные ресурсные ошибки, маскирующиеся под отсутствие видеокарты.
        FfmpegTestHost.RunResult run;
        lock (NvencEncodeLock)
        {
            run = FfmpegTestHost.Run(_ffmpeg, command, timeoutMs);
        }
        return (run, output, command);
    }

    private static void ShouldProduceDecodableVideo(
        FfmpegTestHost.RunResult run,
        string output,
        string command,
        string expectedCodec,
        string expectedPixFmt,
        string? alternatePixFmt = null)
    {
        run.TimedOut.Should().BeFalse($"превышен таймаут кодирования. Команда: {command}");
        run.ExitCode.Should().Be(0, $"FFmpeg отклонил команду: {command}\nХвост stderr:\n{run.ErrorTail}");
        File.Exists(output).Should().BeTrue($"выходной файл не создан. Команда: {command}");
        new FileInfo(output).Length.Should().BeGreaterThan(0, "выходной файл пуст");

        // Полное декодирование — доказательство, что поток не только стартовал, но и валиден.
        var decode = FfmpegTestHost.Run(_ffmpeg, $"-v error -i \"{output}\" -f null -", 120000);
        decode.ExitCode.Should().Be(0, $"результат не декодируется: {command}\nХвост stderr:\n{decode.ErrorTail}");

        var probe = FfmpegTestHost.ProbeVideoStream(_ffprobe, _ffmpeg, output);
        probe.Should().ContainKey("codec_name", $"не удалось определить кодек результата: {command}");
        probe["codec_name"].Should().Be(expectedCodec, $"неожиданный кодек результата: {command}");
        probe.Should().ContainKey("pix_fmt", $"не удалось определить pix_fmt результата: {command}");
        if (alternatePixFmt == null)
        {
            probe["pix_fmt"].Should().Be(expectedPixFmt, $"неожиданный pix_fmt результата: {command}");
        }
        else
        {
            // Аппаратный P010 на выходе декодера софтверно выглядит как yuv420p10le —
            // это одна и та же 10-битная раскладка в разных представлениях.
            probe["pix_fmt"].Should().BeOneOf(expectedPixFmt, alternatePixFmt, $"неожиданный pix_fmt результата: {command}");
        }
    }

    private static void AssertNvencSuccessOrMissingGpu(FfmpegTestHost.RunResult run, string command)
    {
        if (run.ExitCode != 0 && FfmpegTestHost.LooksLikeMissingGpu(run.ErrorTail))
        {
            Assert.Inconclusive($"На машине нет доступного GPU NVENC: {run.ErrorTail}");
        }
        run.ExitCode.Should().Be(0, $"NVENC отклонил команду: {command}\nХвост stderr:\n{run.ErrorTail}");
    }

    private static string Quote(string path) => $"\"{path}\"";
}
