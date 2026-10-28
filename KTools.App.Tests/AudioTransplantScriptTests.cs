// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Scripts;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта пересадки аудио AudioTransplantScript.
/// Все комментарии написаны на русском языке в соответствии с регламентом.
/// </summary>
[TestClass]
public class AudioTransplantScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IMkvmergeRunner> _mkvmergeRunnerMock = null!;
    private Mock<IEac3toRunner> _eac3toRunnerMock = null!;
    private Mock<IAudioWaveformService> _waveformServiceMock = null!;
    private Mock<IMediaProbeService> _mediaProbeServiceMock = null!;
    private AudioTransplantScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _mkvmergeRunnerMock = new Mock<IMkvmergeRunner>();
        _eac3toRunnerMock = new Mock<IEac3toRunner>();
        _waveformServiceMock = new Mock<IAudioWaveformService>();
        _mediaProbeServiceMock = new Mock<IMediaProbeService>();

        _script = new AudioTransplantScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _mkvmergeRunnerMock.Object,
            _eac3toRunnerMock.Object,
            _waveformServiceMock.Object,
            _mediaProbeServiceMock.Object
        );
    }

    /// <summary>
    /// Проверяет свойства скрипта пересадки аудио.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Name.Should().Be("Пересадка аудио");
        _script.Category.Should().Be("Аудио");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.AudioTransplant);
        _script.RequiredDependencies.Should().Contain("ffmpeg").And.Contain("mkvtoolnix").And.Contain("eac3to");
    }

    /// <summary>
    /// Проверяет точность формулы компенсации задержки AAC (21.33 мс на 1024 сэмпла при 48 кГц).
    /// </summary>
    [TestMethod]
    public void AacPrimingDelay_CalculationAccuracy_ShouldBeExact()
    {
        double delayMs = (1024.0 / 48000.0) * 1000.0;
        delayMs.Should().BeApproximately(21.333333333333332, 0.0001);
    }

    /// <summary>
    /// Создаёт структуру медиафайла с одной аудиодорожкой заданного кодека.
    /// </summary>
    private static MediaStructure CreateAudioStructure(string filePath, string codec)
    {
        var structure = new MediaStructure { FilePath = filePath };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "audio",
            Codec = codec,
            Channels = 2
        });
        return structure;
    }

    /// <summary>
    /// Проверяет классификацию кодеков по поддержке eac3to (Opus/Vorbis не поддерживаются).
    /// </summary>
    [TestMethod]
    [DataRow("opus", false)]
    [DataRow("Opus", false)]
    [DataRow("vorbis", false)]
    [DataRow("alac", false)]
    [DataRow("wavpack", false)]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("unknowncodec", false)]
    [DataRow("aac", true)]
    [DataRow("AAC", true)]
    [DataRow("ac3", true)]
    [DataRow("eac3", true)]
    [DataRow("E-AC-3", true)]
    [DataRow("dts", true)]
    [DataRow("dts-hd ma", true)]
    [DataRow("truehd", true)]
    [DataRow("thd", true)]
    [DataRow("flac", true)]
    [DataRow("mp3", true)]
    [DataRow("mp2", true)]
    [DataRow("pcm_s16le", true)]
    [DataRow("wav", true)]
    public void IsEac3toSupportedCodec_VariousCodecs_ReturnsExpected(string? codec, bool expected)
    {
        // Act
        bool actual = AudioTransplantScript.IsEac3toSupportedCodec(codec);

        // Assert
        actual.Should().Be(expected);
    }

    /// <summary>
    /// Проверяет подбор кодировщика FFmpeg для резервного сдвига.
    /// </summary>
    [TestMethod]
    [DataRow("opus", "opus", "libopus")]
    [DataRow("vorbis", "ogg", "libvorbis")]
    [DataRow("mp3", "mp3", "libmp3lame")]
    [DataRow("aac", "aac", "aac")]
    [DataRow("ac3", "ac3", "ac3")]
    [DataRow("eac3", "eac3", "eac3")]
    [DataRow("dts", "dts", "dca")]
    [DataRow("truehd", "thd", "truehd")]
    [DataRow("flac", "flac", "flac")]
    [DataRow("pcm_s16le", "wav", "pcm_s16le")]
    [DataRow("unknowncodec", "bin", "flac")]
    public void ResolveFfmpegAudioEncoder_VariousCodecs_ReturnsExpected(string codec, string rawExt, string expected)
    {
        // Act
        string actual = AudioTransplantScript.ResolveFfmpegAudioEncoder(codec, rawExt);

        // Assert
        actual.Should().Be(expected);
    }

    /// <summary>
    /// Проверяет расширение временного файла для кодировщика libopus.
    /// </summary>
    [TestMethod]
    public void ResolveShiftedExtension_Libopus_ReturnsOpus()
    {
        // Act
        string actual = AudioTransplantScript.ResolveShiftedExtension("libopus");

        // Assert
        actual.Should().Be("opus");
    }

    /// <summary>
    /// Проверяет, что источник Opus сдвигается через FFmpeg без вызова eac3to.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_OpusSource_UsesFfmpegFallbackWithoutEac3to()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_TransplantOpus_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destFile = Path.Combine(tempDir, "dest.mkv");
        string sourceFile = Path.Combine(tempDir, "source.mp4");
        await File.WriteAllTextAsync(destFile, "fake video");
        await File.WriteAllTextAsync(sourceFile, "fake audio");

        _script.SourceFilePath = sourceFile;
        _script.ShiftMs = 100;

        _mediaProbeServiceMock
            .Setup(p => p.ProbeAsync(It.IsAny<string>()))
            .ReturnsAsync((string path) => CreateAudioStructure(path, path == sourceFile ? "opus" : "ac3"));

        var ffmpegExtraArgs = new List<List<string>>();
        _ffmpegRunnerMock
            .Setup(f => f.RunAsync(
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
                (inputPath, outputPath, extraArgs, inputArgs, overwrite, duration, onProgress, ct, ctx) =>
                {
                    ffmpegExtraArgs.Add(new List<string>(extraArgs ?? new List<string>()));
                    if (!string.IsNullOrEmpty(outputPath))
                    {
                        File.WriteAllText(outputPath, "shifted");
                    }
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        _mkvmergeRunnerMock
            .Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, List<MkvInputSource>, string?, List<string>?, Action<double>?, CancellationToken, ProcessExecutionContext?>(
                (outputPath, inputs, title, extraArgs, onProgress, ct, ctx) => File.WriteAllText(outputPath, "mkv"))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        try
        {
            // Act
            ExecutionResult result = await _script.ExecuteSingleAsync(
                destFile,
                new Dictionary<string, object>(),
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            _eac3toRunnerMock.Verify(
                e => e.RunAsync(
                    It.IsAny<List<string>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Action<double>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<ProcessExecutionContext?>(),
                    It.IsAny<string?>()),
                Times.Never);
            ffmpegExtraArgs.Should().HaveCount(1);
            ffmpegExtraArgs[0].Should().Contain("libopus");
            ffmpegExtraArgs[0].Should().ContainMatch("adelay=79*");
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
    /// Проверяет, что источник AC3 обрабатывается через eac3to без резервного перекодирования.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Ac3SourceWithWorkingEac3to_UsesEac3toBitstream()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_TransplantAc3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destFile = Path.Combine(tempDir, "dest.mkv");
        string sourceFile = Path.Combine(tempDir, "source.mkv");
        await File.WriteAllTextAsync(destFile, "fake video");
        await File.WriteAllTextAsync(sourceFile, "fake audio");

        _script.SourceFilePath = sourceFile;
        _script.ShiftMs = 100;

        _mediaProbeServiceMock
            .Setup(p => p.ProbeAsync(It.IsAny<string>()))
            .ReturnsAsync((string path) => CreateAudioStructure(path, "ac3"));

        int ffmpegCalls = 0;
        _ffmpegRunnerMock
            .Setup(f => f.RunAsync(
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
                (inputPath, outputPath, extraArgs, inputArgs, overwrite, duration, onProgress, ct, ctx) =>
                {
                    ffmpegCalls++;
                    if (!string.IsNullOrEmpty(outputPath))
                    {
                        File.WriteAllText(outputPath, "raw");
                    }
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        List<string>? capturedEac3toArgs = null;
        _eac3toRunnerMock
            .Setup(e => e.RunAsync(
                It.IsAny<List<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>(),
                It.IsAny<string?>()))
            .Callback<List<string>, string?, Action<double>?, CancellationToken, ProcessExecutionContext?, string?>(
                (args, workingDir, onProgress, ct, ctx, artifact) =>
                {
                    capturedEac3toArgs = new List<string>(args);
                    string outPath = args[1].Trim('"');
                    File.WriteAllText(outPath, "shifted");
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        _mkvmergeRunnerMock
            .Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, List<MkvInputSource>, string?, List<string>?, Action<double>?, CancellationToken, ProcessExecutionContext?>(
                (outputPath, inputs, title, extraArgs, onProgress, ct, ctx) => File.WriteAllText(outputPath, "mkv"))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        try
        {
            // Act
            ExecutionResult result = await _script.ExecuteSingleAsync(
                destFile,
                new Dictionary<string, object>(),
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            capturedEac3toArgs.Should().NotBeNull();
            capturedEac3toArgs.Should().Contain("+79ms");
            ffmpegCalls.Should().Be(1, "прямое извлечение выполняется один раз, перекодирование не требуется");
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
    /// Проверяет, что при отказе eac3to для поддерживаемого кодека применяется резервный сдвиг через FFmpeg.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_Eac3toFailure_FallsBackToFfmpegShift()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "KTools_TransplantFallback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destFile = Path.Combine(tempDir, "dest.mkv");
        string sourceFile = Path.Combine(tempDir, "source.mkv");
        await File.WriteAllTextAsync(destFile, "fake video");
        await File.WriteAllTextAsync(sourceFile, "fake audio");

        _script.SourceFilePath = sourceFile;
        _script.ShiftMs = -100;

        _mediaProbeServiceMock
            .Setup(p => p.ProbeAsync(It.IsAny<string>()))
            .ReturnsAsync((string path) => CreateAudioStructure(path, "ac3"));

        var ffmpegExtraArgs = new List<List<string>>();
        _ffmpegRunnerMock
            .Setup(f => f.RunAsync(
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
                (inputPath, outputPath, extraArgs, inputArgs, overwrite, duration, onProgress, ct, ctx) =>
                {
                    ffmpegExtraArgs.Add(new List<string>(extraArgs ?? new List<string>()));
                    if (!string.IsNullOrEmpty(outputPath))
                    {
                        File.WriteAllText(outputPath, "shifted");
                    }
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        _eac3toRunnerMock
            .Setup(e => e.RunAsync(
                It.IsAny<List<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(MockBuilders.ProcessFailed());

        _mkvmergeRunnerMock
            .Setup(m => m.RunAsync(
                It.IsAny<string>(),
                It.IsAny<List<MkvInputSource>>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<Action<double>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, List<MkvInputSource>, string?, List<string>?, Action<double>?, CancellationToken, ProcessExecutionContext?>(
                (outputPath, inputs, title, extraArgs, onProgress, ct, ctx) => File.WriteAllText(outputPath, "mkv"))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        try
        {
            // Act
            ExecutionResult result = await _script.ExecuteSingleAsync(
                destFile,
                new Dictionary<string, object>(),
                tempDir,
                (fIdx, total, status, progress, fps, bitrate) => { },
                0,
                1);

            // Assert
            result.Status.Should().Be(ExecutionStatus.Succeeded);
            ffmpegExtraArgs.Should().HaveCount(2, "извлечение и резервный сдвиг");
            ffmpegExtraArgs[1].Should().Contain("ac3");
            ffmpegExtraArgs[1].Should().ContainMatch("atrim=start=0.121*");
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
