// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;

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
}
