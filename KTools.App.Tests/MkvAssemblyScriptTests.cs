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
/// Юнит-тесты для скрипта сборки MKV MkvAssemblyScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MkvAssemblyScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private MkvAssemblyScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();

        _script = new MkvAssemblyScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _mkvmergeRunnerMock.Object,
            _mediaProbeServiceMock.Object,
            _ffmpegRunnerMock.Object
        );
    }

    /// <summary>
    /// Проверяет базовые свойства сборщика MKV.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Контейнеры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.MkvAssembly);
    }

    /// <summary>
    /// Проверяет генерацию аргументов с правильным порядком дорожек при включении position_before_builtin.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_WithPositionBeforeBuiltin_GeneratesCorrectTrackOrder()
    {
        // Arrange
        // Используем реальную временную директорию ОС для теста, чтобы Directory.GetFiles работал корректно
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        string videoPath = Path.Combine(tempDir, "video.mp4");
        string audioPath = Path.Combine(tempDir, "video.mka");
        
        // Создаем пустые файлы, чтобы Directory.GetFiles их нашел
        File.WriteAllText(videoPath, "");
        File.WriteAllText(audioPath, "");
        
        var settings = new Dictionary<string, object>
        {
            { "clean_tracks", false },
            { "position_before_builtin", true }
        };

        try
        {
            // Заполняем очередь
            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(audioPath));

        var structure = new MediaStructure { FilePath = videoPath };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio" }); // Встроенное аудио

        _mediaProbeServiceMock.Setup(m => m.ProbeAsync(videoPath))
            .ReturnsAsync(structure);

        _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
            .Returns(true);

        List<string>? capturedExtraArgs = null;
        _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string>(),
                It.IsAny<List<string>>(),
                It.IsAny<Action<double>>(),
                It.IsAny<System.Threading.CancellationToken>()
            ))
            .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                (outPath, inputs, title, extraArgs, onProgress, ct) => capturedExtraArgs = extraArgs)
            .ReturnsAsync(true);

        // Act
        var results = await _script.ExecuteSingleAsync(
            videoPath,
            settings,
            null,
            (fIdx, total, status, progress, fps, bitrate) => { },
            0,
            1
        );

        // Assert
        results.Should().NotBeNull();
        capturedExtraArgs.Should().NotBeNull();
        capturedExtraArgs.Should().Contain("--track-order");
        
        // Вход 0 - видеофайл, Вход 1 - внешний mka аудиофайл.
        // Ожидаемый порядок: видео (0:0), потом новое аудио (1:0), потом встроенное аудио (0:1)
        int orderIdx = capturedExtraArgs.IndexOf("--track-order");
        capturedExtraArgs[orderIdx + 1].Should().Be("0:0,1:0,0:1");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что при выборе формата MP4 вызывается FFmpegRunner и отфильтровываются несовместимые файлы (FLAC/ASS).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Mp4Container_UsesFFmpegRunnerAndFiltersIncompatibleTracks()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_Mp4Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "video.mp4");
            string flacAudioPath = Path.Combine(tempDir, "video.flac");
            string assSubsPath = Path.Combine(tempDir, "video.ass");

            await File.WriteAllTextAsync(videoPath, "dummy video content");
            await File.WriteAllTextAsync(flacAudioPath, "dummy flac content");
            await File.WriteAllTextAsync(assSubsPath, "dummy ass content");

            var settings = new Dictionary<string, object>
            {
                { "output_container", "MP4" },
                { "clean_tracks", true }
            };

            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(flacAudioPath));
            _script.FilesQueue.Add(new FileQueueItem(assSubsPath));

            _ffmpegRunnerMock.Setup(f => f.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<double>(),
                    It.IsAny<Action<Infrastructure.ProgressInfo>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .ReturnsAsync(true);

            // Act
            var results = await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1
            );

            // Assert
            results.Should().NotBeNull();
            results.Should().Contain(r => r.Contains("FLAC") && r.Contains("пропущен"));
            results.Should().Contain(r => r.Contains("ASS/SSA") && r.Contains("пропущены"));
            results.Should().Contain(r => r.Contains("Собран контейнер MP4"));
            _ffmpegRunnerMock.Verify(f => f.RunAsync(
                videoPath,
                It.Is<string>(s => s.EndsWith(".mp4")),
                It.IsAny<List<string>>(),
                null,
                false,
                0.0,
                It.IsAny<Action<Infrastructure.ProgressInfo>>(),
                It.IsAny<System.Threading.CancellationToken>()
            ), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет, что файлы на диске рядом с видео игнорируются, если они не добавлены явно в очередь пользователем.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SiblingsOnDiskNotInQueue_AreIgnored()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_SiblingsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "video.mkv");
            string siblingMka = Path.Combine(tempDir, "video.mka");
            string siblingAss = Path.Combine(tempDir, "video.ass");

            File.WriteAllText(videoPath, "");
            File.WriteAllText(siblingMka, "");
            File.WriteAllText(siblingAss, "");

            // В очередь добавляем ТОЛЬКО видеофайл
            _script.FilesQueue.Add(new FileQueueItem(videoPath));

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);

            List<MkvInputSource>? capturedInputs = null;
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                    (outPath, inputs, title, extraArgs, onProgress, ct) => capturedInputs = inputs)
                .ReturnsAsync(true);

            // Act
            var results = await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1
            );

            // Assert
            results.Should().NotBeNull();
            capturedInputs.Should().NotBeNull();
            // Должен быть только один вход — само видео, без сопутствующих файлов с диска
            capturedInputs!.Should().HaveCount(1);
            capturedInputs[0].Path.Should().Be(videoPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет сборку с несколькими внешними аудио и субтитрами, включая префиксные имена файлов.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_MultipleTracks_AllInputsIncluded()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_MultiTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "s01e01.mkv");
            string audioExact = Path.Combine(tempDir, "s01e01.mka");
            string audioPrefixed = Path.Combine(tempDir, "s01e01.dub.ac3");
            string subsExact = Path.Combine(tempDir, "s01e01.srt");
            string subsPrefixed = Path.Combine(tempDir, "s01e01.en.ass");
            string unrelated = Path.Combine(tempDir, "s01e02.mka");

            File.WriteAllText(videoPath, "");
            File.WriteAllText(audioExact, "");
            File.WriteAllText(audioPrefixed, "");
            File.WriteAllText(subsExact, "");
            File.WriteAllText(subsPrefixed, "");
            File.WriteAllText(unrelated, "");

            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(audioExact));
            _script.FilesQueue.Add(new FileQueueItem(audioPrefixed));
            _script.FilesQueue.Add(new FileQueueItem(subsExact));
            _script.FilesQueue.Add(new FileQueueItem(subsPrefixed));
            _script.FilesQueue.Add(new FileQueueItem(unrelated));

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);

            List<MkvInputSource>? capturedInputs = null;
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                    (outPath, inputs, title, extraArgs, onProgress, ct) => capturedInputs = inputs)
                .ReturnsAsync(true);

            // Act
            var results = await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1
            );

            // Assert
            results.Should().NotBeNull();
            capturedInputs.Should().NotBeNull();
            // Видео + 2 аудио + 2 субтитров; чужой s01e02.mka не должен попасть в сборку.
            capturedInputs!.Should().HaveCount(5);
            capturedInputs[0].Path.Should().Be(videoPath);
            capturedInputs.Select(i => i.Path).Should().NotContain(unrelated);
            // Первая дорожка каждого типа — default, остальные — нет.
            capturedInputs[1].Args.Should().Contain("0:yes");
            capturedInputs[2].Args.Should().Contain("0:no");
            capturedInputs[3].Args.Should().Contain("0:no");
            capturedInputs[4].Args.Should().Contain("0:no");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет роли субтитров по суффиксам (.full/.signs) и ручную привязку чужого файла:
    /// порядок полные-надписи, отдельные заголовки, пин включается в сборку.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SubsRolesAndPin_OrdersAndTitles()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_RolesTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "s01e01.mkv");
            string fullSubs = Path.Combine(tempDir, "s01e01.full.ass");
            string signsSubs = Path.Combine(tempDir, "s01e01.signs.ass");
            string foreignAudio = Path.Combine(tempDir, "foreign-dub.mka");

            File.WriteAllText(videoPath, "");
            File.WriteAllText(fullSubs, "");
            File.WriteAllText(signsSubs, "");
            File.WriteAllText(foreignAudio, "");

            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(fullSubs));
            _script.FilesQueue.Add(new FileQueueItem(signsSubs));
            var pinnedAudio = new FileQueueItem(foreignAudio) { MuxPinnedStem = "s01e01" };
            _script.FilesQueue.Add(pinnedAudio);

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true },
                { "subs_full_title", "Мои полные" },
                { "subs_signs_title", "Мои надписи" }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);

            List<MkvInputSource>? capturedInputs = null;
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                    (outPath, inputs, title, extraArgs, onProgress, ct) => capturedInputs = inputs)
                .ReturnsAsync(true);

            // Act
            var results = await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1
            );

            // Assert
            results.Should().NotBeNull();
            capturedInputs.Should().NotBeNull();
            capturedInputs!.Should().HaveCount(4);
            capturedInputs[0].Path.Should().Be(videoPath);
            capturedInputs[1].Path.Should().Be(foreignAudio);
            capturedInputs[2].Path.Should().Be(signsSubs);
            capturedInputs[3].Path.Should().Be(fullSubs);
            capturedInputs[2].Args.Should().Contain(a => a.Contains("Мои надписи"));
            capturedInputs[3].Args.Should().Contain(a => a.Contains("Мои полные"));
            capturedInputs[2].Args.Should().Contain("0:yes");
            capturedInputs[3].Args.Should().Contain("0:no");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет основную и дополнительные озвучки: первая по имени — основная (rus, default),
    /// остальные различаются языком и заголовком из имени файла; явный флаг переопределяет выбор.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AudioMainAndExtra_FlagsTitlesLanguages()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_AudioMainTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "s01e01.mkv");
            string exactAudio = Path.Combine(tempDir, "s01e01.mka");
            string dubAudio = Path.Combine(tempDir, "s01e01.AniLibria.ac3");
            string enAudio = Path.Combine(tempDir, "s01e01.en.ac3");

            File.WriteAllText(videoPath, "");
            File.WriteAllText(exactAudio, "");
            File.WriteAllText(dubAudio, "");
            File.WriteAllText(enAudio, "");

            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(exactAudio));
            _script.FilesQueue.Add(new FileQueueItem(dubAudio));
            _script.FilesQueue.Add(new FileQueueItem(enAudio));

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);

            List<MkvInputSource>? capturedInputs = null;
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                    (outPath, inputs, title, extraArgs, onProgress, ct) => capturedInputs = inputs)
                .ReturnsAsync(true);

            ScriptProgressCallback noProgress = (fIdx, total, status, progress, fps, bitrate) => { };

            // Act 1 — без явных флагов: первая по имени основная.
            await _script.ExecuteSingleAsync(videoPath, settings, null, noProgress, 0, 1);

            // Assert 1
            capturedInputs.Should().NotBeNull();
            capturedInputs!.Should().HaveCount(4);
            capturedInputs[1].Path.Should().Be(dubAudio);
            capturedInputs[1].Args.Should().Contain("0:yes");
            capturedInputs[1].Args.Should().Contain("0:rus");
            capturedInputs[1].Args.Should().Contain(a => a.Contains("AniLibria"));
            capturedInputs[2].Path.Should().Be(enAudio);
            capturedInputs[2].Args.Should().Contain("0:no");
            capturedInputs[2].Args.Should().Contain("0:rus");
            capturedInputs[3].Path.Should().Be(exactAudio);
            capturedInputs[3].Args.Should().Contain("0:no");

            // Act 2 — явный флаг основной дорожки переопределяет выбор.
            _script.FilesQueue.First(f => f.FilePath == exactAudio).MuxAudioMain = true;
            await _script.ExecuteSingleAsync(videoPath, settings, null, noProgress, 0, 1);

            // Assert 2
            capturedInputs!.Should().HaveCount(4);
            capturedInputs[1].Path.Should().Be(exactAudio);
            capturedInputs[1].Args.Should().Contain("0:yes");
            capturedInputs[1].Args.Should().Contain("0:rus");
            capturedInputs[2].Path.Should().Be(dubAudio);
            capturedInputs[2].Args.Should().Contain("0:no");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет разовую миграцию старых дефолтов заголовков ("Полные" / "[Надписи]") на новые.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_OldSubsTitleDefaults_MigratesToNew()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_MigrateTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "video.mkv");
            File.WriteAllText(videoPath, "");

            _script.FilesQueue.Add(new FileQueueItem(videoPath));

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true },
                { "subs_full_title", "Полные" },
                { "subs_signs_title", "[Надписи]" }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .ReturnsAsync(true);

            // Act
            await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            _settingsManagerMock.Verify(
                s => s.SetSetting(It.IsAny<string>(), "subs_full_title", "Субтитры"),
                Times.Once);
            _settingsManagerMock.Verify(
                s => s.SetSetting(It.IsAny<string>(), "subs_signs_title", "Надписи"),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// Проверяет заголовок аудио из метаданных файла: встроенный Title приоритетнее
    /// имени файла, кавычки вычищаются; без метаданных используется имя файла.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_EmbeddedAudioTitle_WinsOverFileName()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "MkvAssembly_TitleTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string videoPath = Path.Combine(tempDir, "s01e01.mkv");
            string exactAudio = Path.Combine(tempDir, "s01e01.mka");
            string extraAudio = Path.Combine(tempDir, "s01e01.Extra.ac3");

            File.WriteAllText(videoPath, "");
            File.WriteAllText(exactAudio, "");
            File.WriteAllText(extraAudio, "");

            _script.FilesQueue.Add(new FileQueueItem(videoPath));
            _script.FilesQueue.Add(new FileQueueItem(exactAudio));
            _script.FilesQueue.Add(new FileQueueItem(extraAudio));
            _script.FilesQueue.First(f => f.FilePath == exactAudio).MuxAudioMain = true;

            var withTitle = new MediaStructure { FilePath = exactAudio };
            withTitle.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = "AniLibria \"Best\"" });
            _mediaProbeServiceMock.Setup(m => m.ProbeAsync(exactAudio))
                .ReturnsAsync(withTitle);

            var settings = new Dictionary<string, object>
            {
                { "clean_tracks", true }
            };

            _settingsManagerMock.Setup(s => s.GetSetting("General", "OverwriteExisting", false))
                .Returns(true);

            List<MkvInputSource>? capturedInputs = null;
            _mkvmergeRunnerMock.Setup(m => m.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<MkvInputSource>>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<Action<double>>(),
                    It.IsAny<System.Threading.CancellationToken>()
                ))
                .Callback<string, List<MkvInputSource>, string, List<string>, Action<double>, System.Threading.CancellationToken>(
                    (outPath, inputs, title, extraArgs, onProgress, ct) => capturedInputs = inputs)
                .ReturnsAsync(true);

            // Act
            await _script.ExecuteSingleAsync(
                videoPath,
                settings,
                null,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            capturedInputs.Should().NotBeNull();
            capturedInputs!.Should().HaveCount(3);
            capturedInputs[1].Path.Should().Be(exactAudio);
            capturedInputs[1].Args.Should().Contain(a => a.Contains("AniLibria 'Best'"));
            capturedInputs[2].Path.Should().Be(extraAudio);
            capturedInputs[2].Args.Should().Contain(a => a.Contains("Extra"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
