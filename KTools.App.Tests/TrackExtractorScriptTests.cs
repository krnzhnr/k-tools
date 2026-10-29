// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;


namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта извлечения дорожек TrackExtractorScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class TrackExtractorScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private TrackExtractorScript _script = null!;
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();

        _script = new TrackExtractorScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object
        );
        _tempDir = string.Empty;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Создаёт уникальную временную папку для изоляции файлового состояния теста.
    /// </summary>
    private string CreateTempRoot()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KToolsTrackExtractorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    /// <summary>
    /// Создаёт выходные артефакты для всех путей из аргументов FFmpeg.
    /// </summary>
    private static void CreateValidArtifacts(List<string>? args)
    {
        if (args == null)
        {
            return;
        }

        foreach (string raw in args)
        {
            string trimmed = raw.Trim().Trim('"');
            if (trimmed.Length < 4 || !Path.IsPathRooted(trimmed))
            {
                continue;
            }

            try
            {
                string? dir = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                Directory.CreateDirectory(dir);
                File.WriteAllText(trimmed, "test-data");
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Создаёт пустые заглушки (0 байт) для всех путей из аргументов FFmpeg.
    /// </summary>
    private static void CreateZeroByteArtifacts(List<string>? args)
    {
        if (args == null)
        {
            return;
        }

        foreach (string raw in args)
        {
            string trimmed = raw.Trim().Trim('"');
            if (trimmed.Length < 4 || !Path.IsPathRooted(trimmed))
            {
                continue;
            }

            try
            {
                string? dir = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                Directory.CreateDirectory(dir);
                File.WriteAllBytes(trimmed, Array.Empty<byte>());
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта извлечения дорожек.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Контейнеры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.TrackExtractor);
    }

    /// <summary>
    /// Проверяет форматирование имени выходного файла с различными плейсхолдерами.
    /// </summary>
    [TestMethod]
    public void FormatFilename_VariousPlaceholders_ShouldGenerateExpectedNames()
    {
        var track = new MediaTrack
        {
            TrackId = 2,
            Language = "rus",
            Name = "DUB",
            Codec = "AC3",
            TrackType = "audio"
        };

        var methodInfo = typeof(TrackExtractorScript).GetMethod(
            "FormatFilename", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        methodInfo.Should().NotBeNull();

        // 1. Стандартный шаблон {original}_{lang}_{id}
        var result1 = methodInfo!.Invoke(_script, new object[] { "Movie", track, ".ac3", "{original}_{lang}_{id}", "" }) as string;
        result1.Should().Be("Movie_rus_track02.ac3");

        // 2. Шаблон со всеми плейсхолдерами {original}_{lang}_{id}_{title}_{codec}
        var result2 = methodInfo.Invoke(_script, new object[] { "Movie", track, ".ac3", "{original}_{lang}_{id}_{title}_{codec}", "" }) as string;
        result2.Should().Be("Movie_rus_track02_DUB_ac3.ac3");

        // 3. Шаблон с пустыми плейсхолдерами (например, если нет языка или названия)
        var trackEmpty = new MediaTrack
        {
            TrackId = 3,
            Language = "und",
            Name = "",
            Codec = "srt",
            TrackType = "subtitles"
        };
        var result3 = methodInfo.Invoke(_script, new object[] { "Movie", trackEmpty, ".srt", "{original}_{lang}_{id}_{title}_{codec}", "" }) as string;
        result3.Should().Be("Movie_track03_srt.srt");
    }

    /// <summary>
    /// Проверяет дополнительные сценарии форматирования имени, такие как дефолтный шаблон,
    /// независимость от регистра тегов и сложные разделители.
    /// </summary>
    [TestMethod]
    public void FormatFilename_AdditionalScenarios_ShouldGenerateExpectedNames()
    {
        var track = new MediaTrack
        {
            TrackId = 5,
            Language = "eng",
            Name = "Commentary",
            Codec = "AAC",
            TrackType = "audio"
        };

        var methodInfo = typeof(TrackExtractorScript).GetMethod(
            "FormatFilename", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        methodInfo.Should().NotBeNull();

        // 1. Дефолтный шаблон {original}
        var result1 = methodInfo!.Invoke(_script, new object[] { "Avatar", track, ".mka", "{original}", "" }) as string;
        result1.Should().Be("Avatar.mka");

        // 2. Регистронезависимость тегов
        var result2 = methodInfo.Invoke(_script, new object[] { "Avatar", track, ".mka", "{FiLe_NaMe}_{TRACK_ID}_{TRACK_CODEC}", "" }) as string;
        result2.Should().Be("Avatar_track05_aac.mka");

        // 3. Сложные разделители и пустые плейсхолдеры
        var trackEmpty = new MediaTrack
        {
            TrackId = 1,
            Language = "und",
            Name = "",
            Codec = "SRT",
            TrackType = "subtitles"
        };
        var result3 = methodInfo.Invoke(_script, new object[] { "Avatar", trackEmpty, ".srt", "{original}--{lang}__{id}---{codec}", "" }) as string;
        result3.Should().Be("Avatar-track01-srt.srt");

        // 4. Очистка пустых скобок вокруг отсутствующих значений плейсхолдеров
        var result4 = methodInfo.Invoke(_script, new object[] { "Movie", trackEmpty, ".srt", "{original} [{lang}] [{id}] [{title}] [{codec}]", "" }) as string;
        result4.Should().Be("Movie [track01] [srt].srt");
    }

    /// <summary>
    /// Проверяет правильность определения расширений для сырых потоков видео, аудио и субтитров.
    /// </summary>
    [TestMethod]
    public void ResolveRawExtension_VariousCodecs_ShouldReturnCorrectExtension()
    {
        // H.264 / AVC
        AppConstants.ResolveRawExtension("h264", "video").Should().Be(".h264");
        AppConstants.ResolveRawExtension("H.264", "video").Should().Be(".h264");
        AppConstants.ResolveRawExtension("MPEG-4p10/AVC/h.264", "video").Should().Be(".h264");
        AppConstants.ResolveRawExtension("AVC/H.264/MPEG-4p10", "video").Should().Be(".h264");
        AppConstants.ResolveRawExtension("avc1", "video").Should().Be(".h264");
        AppConstants.ResolveRawExtension("V_MPEG4/ISO/AVC", "video").Should().Be(".h264");

        // HEVC
        AppConstants.ResolveRawExtension("hevc", "video").Should().Be(".h265");
        AppConstants.ResolveRawExtension("h265", "video").Should().Be(".h265");
        AppConstants.ResolveRawExtension("HEVC/H.265/MPEG-H", "video").Should().Be(".h265");
        AppConstants.ResolveRawExtension("hev1", "video").Should().Be(".h265");

        // MPEG-2, VC-1, AV1
        AppConstants.ResolveRawExtension("mpeg2video", "video").Should().Be(".m2v");
        AppConstants.ResolveRawExtension("vc1", "video").Should().Be(".vc1");
        AppConstants.ResolveRawExtension("av1", "video").Should().Be(".ivf");

        // Аудио кодеки
        AppConstants.ResolveRawExtension("TrueHD Atmos", "audio").Should().Be(".thd");
        AppConstants.ResolveRawExtension("DTS-HD Master Audio", "audio").Should().Be(".dts");
        AppConstants.ResolveRawExtension("DTS-HD High Resolution Audio", "audio").Should().Be(".dts");
        AppConstants.ResolveRawExtension("E-AC-3 Atmos", "audio").Should().Be(".eac3");
        AppConstants.ResolveRawExtension("ac3", "audio").Should().Be(".ac3");
        AppConstants.ResolveRawExtension("flac", "audio").Should().Be(".flac");
        AppConstants.ResolveRawExtension("opus", "audio").Should().Be(".opus");
        AppConstants.ResolveRawExtension("pcm_s24le", "audio").Should().Be(".wav");

        // Субтитры
        AppConstants.ResolveRawExtension("SubRip/SRT", "subtitles").Should().Be(".srt");
        AppConstants.ResolveRawExtension("hdmv_pgs_subtitle", "subtitles").Should().Be(".sup");
        AppConstants.ResolveRawExtension("SubStationAlpha", "subtitles").Should().Be(".ass");
        AppConstants.ResolveRawExtension("webvtt", "subtitles").Should().Be(".vtt");

        // Неизвестный формат (fallback)
        AppConstants.ResolveRawExtension("unknown_custom_codec", "video").Should().Be(".mkv");
        AppConstants.ResolveRawExtension("unknown_custom_codec", "audio").Should().Be(".mka");
        AppConstants.ResolveRawExtension("unknown_custom_codec", "subtitles").Should().Be(".mks");
    }

    /// <summary>
    /// Проверяет, что при выполнении извлечения дорожек с включенным LocalRenameOverride
    /// выходные файлы и аргументы FFmpeg получают корректно переименованные имена файлов.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_LocalRenameEnabled_AppliesRenameToExtractedTracks()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "Yomi no Tsugai - 10 TG.mp4");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure
        {
            Duration = 100.0
        };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 1,
            TrackType = "audio",
            Codec = "aac",
            Language = "rus",
            Name = "Stereo"
        });

        _mediaProbeServiceMock
            .Setup(m => m.ProbeAsync(inputPath))
            .ReturnsAsync(structure);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inP, outP, args, inArgs, ow, dur, prog, ct, processContext) =>
                {
                    if (args != null)
                    {
                        capturedExtraArgs = new List<string>(args);
                    }

                    CreateValidArtifacts(args);
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 1 } } } },
            { "name_format", "{original}" },
            { "LocalRenameOverride", true },
            { "LocalRenameSearch", @" - (\d+) TG" },
            { "LocalRenameReplace", @" - [$1]" },
            { "LocalRenameUseRegex", true },
            { "LocalRenameCaseSensitive", false }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath,
            settings,
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputExists.Should().BeTrue();
        result.Should().Contain(r => r.Contains("✅ Извлечена дорожка 1: Yomi no Tsugai - [10].aac"));
        capturedExtraArgs.Should().NotBeNull();
        string expectedPathArg = $"\"{Path.Combine(outputDir, "Yomi no Tsugai - [10].aac")}\"";
        capturedExtraArgs.Should().Contain(expectedPathArg);
        File.Exists(Path.Combine(outputDir, "Yomi no Tsugai - [10].aac")).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что при глобальных настройках переименования дорожки извлекаются с корректно обновленным именем.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_GlobalRenameEnabled_AppliesRenameToExtractedTracks()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "Anime_Episode_01.mkv");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure
        {
            Duration = 50.0
        };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "subtitles",
            Codec = "SubRip/SRT",
            Language = "rus"
        });

        _mediaProbeServiceMock
            .Setup(m => m.ProbeAsync(inputPath))
            .ReturnsAsync(structure);

        _settingsManagerMock.Setup(s => s.RenameEnableRegex).Returns(true);
        _settingsManagerMock.Setup(s => s.RenameRegexSearch).Returns(@"_Episode_(\d+)");
        _settingsManagerMock.Setup(s => s.RenameRegexReplace).Returns(@"_E$1");
        _settingsManagerMock.Setup(s => s.RenameUseRegex).Returns(true);
        _settingsManagerMock.Setup(s => s.RenameCaseSensitive).Returns(false);

        List<string>? capturedExtraArgs = null;
        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inP, outP, args, inArgs, ow, dur, prog, ct, processContext) =>
                {
                    if (args != null)
                    {
                        capturedExtraArgs = new List<string>(args);
                    }

                    CreateValidArtifacts(args);
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "name_format", "{original}" },
            { "LocalRenameOverride", false }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath,
            settings,
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputExists.Should().BeTrue();
        result.Should().Contain(r => r.Contains("✅ Извлечена дорожка 0: Anime_E01.srt"));
        capturedExtraArgs.Should().NotBeNull();
        string expectedPathArg = $"\"{Path.Combine(outputDir, "Anime_E01.srt")}\"";
        capturedExtraArgs.Should().Contain(expectedPathArg);
        File.Exists(Path.Combine(outputDir, "Anime_E01.srt")).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что успешный код возврата FFmpeg с пустым артефактом возвращает сбой с повтором.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ZeroByteArtifact_ReturnsFailedWithRetryable()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.m2ts");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure { Duration = 10.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 1,
            TrackType = "audio",
            Codec = "ac3",
            Language = "rus"
        });

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(inputPath)).ReturnsAsync(structure);

        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inP, outP, args, inArgs, ow, dur, prog, ct, ctx) => CreateZeroByteArtifacts(args))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 1 } } } },
            { "name_format", "{original}" }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath, settings, outputDir,
            (cur, total, msg, pct, fps, br) => { }, 1, 1);

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.Retryable.Should().BeTrue();
        result.OutputExists.Should().BeFalse();
        result.ErrorCode.Should().Be("output-missing");
    }

    /// <summary>
    /// Проверяет, что валидная дорожка и сбой шрифта дают частичный успех.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_TrackOkFontFails_ReturnsPartiallySucceeded()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure { Duration = 20.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = "aac",
            Language = "rus"
        });
        structure.Attachments.Add(new MediaAttachment
        {
            AttachmentId = 0,
            FileName = "font.ttf",
            MimeType = "application/x-truetype-font"
        });

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(inputPath)).ReturnsAsync(structure);

        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inP, outP, args, inArgs, ow, dur, prog, ct, ctx) => CreateValidArtifacts(args))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        _ffmpegRunnerMock
            .Setup(r => r.ExtractAttachmentAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessFailed("attachment-failed", "Не удалось извлечь вложение"));

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "selected_attachments_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "name_format", "{original}" }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath, settings, outputDir,
            (cur, total, msg, pct, fps, br) => { }, 1, 1);

        result.Status.Should().Be(ExecutionStatus.PartiallySucceeded);
        result.OutputExists.Should().BeTrue();
        result.OutputFile.Should().NotBeNullOrEmpty();
        File.Exists(result.OutputFile!).Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что повторный прогон поверх пустых заглушек удаляет их и завершается успехом.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_StaleZeroByteStubs_ReturnsSucceeded()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure { Duration = 15.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = "aac",
            Language = "rus"
        });

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(inputPath)).ReturnsAsync(structure);

        string stubPath = Path.Combine(outputDir, "movie.aac");
        File.WriteAllBytes(stubPath, Array.Empty<byte>());

        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>(
                (inP, outP, args, inArgs, ow, dur, prog, ct, ctx) => CreateValidArtifacts(args))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "name_format", "{original}" }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath, settings, outputDir,
            (cur, total, msg, pct, fps, br) => { }, 1, 1);

        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputExists.Should().BeTrue();
        new FileInfo(stubPath).Length.Should().BeGreaterThan(0);
        result.Should().Contain(r => r.Contains("✅ Извлечена дорожка 0"));
    }

    /// <summary>
    /// Проверяет, что устаревший файл при перезаписи без обновления считается сбоем.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_StaleFileWithOverwrite_ReturnsFailed()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure { Duration = 12.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = "aac",
            Language = "rus"
        });

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(inputPath)).ReturnsAsync(structure);

        _settingsManagerMock
            .Setup(m => m.GetSetting("General", "OverwriteExisting", It.IsAny<bool>()))
            .Returns(true);

        string stalePath = Path.Combine(outputDir, "movie.aac");
        File.WriteAllText(stalePath, "old-data");
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddDays(-2));

        _ffmpegRunnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "name_format", "{original}" }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath, settings, outputDir,
            (cur, total, msg, pct, fps, br) => { }, 1, 1);

        result.Status.Should().Be(ExecutionStatus.Failed);
        result.Retryable.Should().BeTrue();
        result.OutputExists.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что все уже извлечённые файлы возвращают пропуск без запуска FFmpeg.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AllOutputsExist_ReturnsSkippedAlreadyProcessed()
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, "movie.mkv");
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(outputDir);

        var structure = new MediaStructure { Duration = 8.0 };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = "aac",
            Language = "rus"
        });

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(inputPath)).ReturnsAsync(structure);

        string existingPath = Path.Combine(outputDir, "movie.aac");
        File.WriteAllText(existingPath, "valid-data");

        var settings = new Dictionary<string, object>
        {
            { "selected_tracks_per_file", new Dictionary<string, List<int>> { { inputPath, new List<int> { 0 } } } },
            { "name_format", "{original}" }
        };

        var result = await _script.ExecuteSingleAsync(
            inputPath, settings, outputDir,
            (cur, total, msg, pct, fps, br) => { }, 1, 1);

        result.Status.Should().Be(ExecutionStatus.Skipped);
        result.ErrorCode.Should().Be("already-processed");
        result.OutputExists.Should().BeTrue();
        result.OutputFile.Should().Be(existingPath);
        _ffmpegRunnerMock.Verify(
            r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()),
            Times.Never);
    }
}
