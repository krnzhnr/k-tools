// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта распознавания речи SpeechRecognitionScript.
/// Проверяют ветви отсутствия файла и модели, установки рантайма,
/// извлечения аудио, транскрибации и обработки исключений.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SpeechRecognitionScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IWhisperRunner> _whisperRunnerMock = null!;
    private Mock<IWhisperModelManager> _modelManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<IDialogService> _dialogServiceMock = null!;
    private SpeechRecognitionScript _script = null!;
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
        _whisperRunnerMock = new Mock<IWhisperRunner>();
        _modelManagerMock = new Mock<IWhisperModelManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _dependencyManagerMock = new Mock<IDependencyManager>();
        _dialogServiceMock = MockBuilders.CreateDialogMock();

        _script = new SpeechRecognitionScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _whisperRunnerMock.Object,
            _modelManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _dependencyManagerMock.Object,
            _dialogServiceMock.Object);
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
            // Игнорируем транзитные блокировки файлов при очистке
        }
    }

    /// <summary>
    /// Создаёт уникальную временную папку для изоляции файлового состояния теста.
    /// </summary>
    private string CreateTempRoot()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KToolsSpeechRecognitionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    /// <summary>
    /// Создаёт входной медиафайл и отдельную папку результатов.
    /// </summary>
    private (string InputPath, string OutputDir) CreateInputWithOutputDir(string fileName = "movie.mp4")
    {
        string tempRoot = CreateTempRoot();
        string inputPath = Path.Combine(tempRoot, fileName);
        File.WriteAllText(inputPath, "input");
        string outputDir = Path.Combine(tempRoot, "Out");
        Directory.CreateDirectory(outputDir);
        return (inputPath, outputDir);
    }

    /// <summary>
    /// Возвращает базовый словарь настроек с явным CPU-бэкендом для детерминированности.
    /// </summary>
    private static Dictionary<string, object> CreateDefaultSettings()
    {
        return new Dictionary<string, object>
        {
            { "whisper_model", "base" },
            { "whisper_backend", "CPU (Процессор, AVX2)" },
            { "output_srt", true },
            { "output_txt", false }
        };
    }

    /// <summary>
    /// Настраивает модель и рантайм как установленные для прохода к извлечению аудио.
    /// </summary>
    private string SetupReadyPipeline()
    {
        _modelManagerMock
            .Setup(m => m.IsModelDownloaded(It.IsAny<string>()))
            .Returns(true);
        _modelManagerMock
            .Setup(m => m.GetModelFilePath(It.IsAny<string>()))
            .Returns(Path.Combine(_tempDir, "ggml-base.bin"));
        _dependencyManagerMock
            .Setup(d => d.IsInstalled(It.IsAny<string>()))
            .Returns(true);
        return _tempDir;
    }

    /// <summary>
    /// Настраивает мок FFmpeg на успешное извлечение с созданием выходного WAV-файла.
    /// </summary>
    /// <returns>Переменная-приёмник фактического пути созданного WAV-файла.</returns>
    private void SetupFfmpegSuccess(out Func<string?> getWavPath)
    {
        string? wavPath = null;
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
                (inP, outP, args, inArgs, ow, dur, prog, ct, ctx) =>
                {
                    if (!string.IsNullOrEmpty(outP))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outP)!);
                        File.WriteAllText(outP, "wav-data");
                        wavPath = outP;
                    }
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());
        getWavPath = () => wavPath;
    }

    /// <summary>
    /// Настраивает мок Whisper на успешную транскрибацию с созданием SRT-файла.
    /// </summary>
    private void SetupWhisperSuccessWithSrt()
    {
        _whisperRunnerMock
            .Setup(w => w.TranscribeAsync(
                It.IsAny<WhisperTranscribeOptions>(),
                It.IsAny<Action<int>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<WhisperTranscribeOptions, Action<int>?, Action<string>?, CancellationToken, ProcessExecutionContext?>(
                (opts, progress, segment, ct, ctx) =>
                {
                    string srtPath = opts.OutputBasePath + ".srt";
                    Directory.CreateDirectory(Path.GetDirectoryName(srtPath)!);
                    File.WriteAllText(srtPath, "1\n00:00:00,000 --> 00:00:01,000\nПривет\n");
                })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());
    }

    /// <summary>
    /// Проверяет базовые метаданные скрипта распознавания речи.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_ValidMetadata_ReturnsExpectedValues()
    {
        // Assert
        _script.Name.Should().Be(AppConstants.ScriptMetadata.SpeechRecognitionName);
        _script.Description.Should().Be(AppConstants.ScriptMetadata.SpeechRecognitionDesc);
        _script.Category.Should().Be(AppConstants.ScriptCategory.Subtitles);
        _script.IconName.Should().Be(AppConstants.ScriptIcons.SpeechRecognition);
        _script.SupportsParallel.Should().BeFalse();
        _script.RequiredDependencies.Should().Contain("ffmpeg");
        _script.FileExtensions.Should().NotBeEmpty();
        _script.FileExtensions.Should().Contain(".mp4");
    }

    /// <summary>
    /// Проверяет, что отсутствующий входной файл даёт сбой input-missing до обращений к модели.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_MissingInputFile_ReturnsFailedInputMissing()
    {
        // Arrange
        string tempRoot = CreateTempRoot();
        string missingPath = Path.Combine(tempRoot, "ghost.mp4");

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            missingPath,
            CreateDefaultSettings(),
            tempRoot,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("input-missing");
        _modelManagerMock.Verify(m => m.IsModelDownloaded(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Проверяет, что отказ от загрузки отсутствующей модели отменяет операцию.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ModelMissingAndDialogCancelled_ReturnsCancelled()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        _modelManagerMock
            .Setup(m => m.IsModelDownloaded(It.IsAny<string>()))
            .Returns(false);
        _dialogServiceMock
            .Setup(d => d.ShowConfirmationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Cancelled);
        result.ErrorCode.Should().Be("model-download-cancelled");
        result.CleanupState.Should().Be(CleanupState.NotStarted);
        _dialogServiceMock.Verify(
            d => d.ShowConfirmationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что неудачная загрузка модели даёт сбой с возможностью повтора.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_ModelDownloadFails_ReturnsFailedRetryable()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        _modelManagerMock
            .Setup(m => m.IsModelDownloaded(It.IsAny<string>()))
            .Returns(false);
        _modelManagerMock
            .Setup(m => m.DownloadModelAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("model-download-failed");
        result.Retryable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что провал установки рантайма даёт сбой missing-dependency.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_RuntimeInstallFails_ReturnsFailedMissingDependency()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        _modelManagerMock
            .Setup(m => m.IsModelDownloaded(It.IsAny<string>()))
            .Returns(true);
        _dependencyManagerMock
            .Setup(d => d.IsInstalled(It.IsAny<string>()))
            .Returns(false);

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("missing-dependency");
        result.Retryable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что успешная установка рантайма продолжает конвейер до извлечения аудио.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_RuntimeInstalledOnDemand_ProceedsToAudioExtraction()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        _modelManagerMock
            .Setup(m => m.IsModelDownloaded(It.IsAny<string>()))
            .Returns(true);
        bool installed = false;
        _dependencyManagerMock
            .Setup(d => d.IsInstalled(It.IsAny<string>()))
            .Returns(() => installed);
        _dependencyManagerMock
            .Setup(d => d.InstallDependencyAsync(It.IsAny<string>()))
            .Callback(() => installed = true)
            .Returns(Task.CompletedTask);
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
            .ReturnsAsync(MockBuilders.ProcessFailed());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("audio-extraction-failed");
        _dependencyManagerMock.Verify(d => d.InstallDependencyAsync("whisper_cpu"), Times.Once);
    }

    /// <summary>
    /// Проверяет, что сбой FFmpeg даёт ошибку извлечения аудио.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FfmpegFails_ReturnsFailedAudioExtractionFailed()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
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
            .ReturnsAsync(MockBuilders.ProcessFailed());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("audio-extraction-failed");
        result.Retryable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что неуспешная транскрибация даёт ошибку transcription-failed.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_TranscriptionFails_ReturnsFailedTranscriptionFailed()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
        SetupFfmpegSuccess(out _);
        _whisperRunnerMock
            .Setup(w => w.TranscribeAsync(
                It.IsAny<WhisperTranscribeOptions>(),
                It.IsAny<Action<int>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .ReturnsAsync(MockBuilders.ProcessFailed());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("transcription-failed");
        result.Retryable.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что успех транскрибации без выходных файлов даёт ошибку output-missing.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_NoOutputFiles_ReturnsFailedOutputMissing()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
        SetupFfmpegSuccess(out _);
        _whisperRunnerMock
            .Setup(w => w.TranscribeAsync(
                It.IsAny<WhisperTranscribeOptions>(),
                It.IsAny<Action<int>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("output-missing");
        result.OutputExists.Should().BeFalse();
        result.Should().Contain(r => r.Contains("Выходные файлы распознавания не найдены"));
    }

    /// <summary>
    /// Проверяет полный успешный конвейер: извлечение, транскрибация и SRT-результат с очисткой WAV.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FullPipeline_ReturnsSucceededWithSrtOutput()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
        SetupFfmpegSuccess(out Func<string?> getWavPath);
        SetupWhisperSuccessWithSrt();

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.OutputExists.Should().BeTrue();
        result.OutputFile.Should().NotBeNullOrEmpty();
        result.OutputFile.Should().EndWith(".srt");
        File.Exists(result.OutputFile!).Should().BeTrue();
        getWavPath().Should().NotBeNull();
        File.Exists(getWavPath()!).Should().BeFalse("временный WAV-файл удаляется в блоке finally");
    }

    /// <summary>
    /// Проверяет, что отмена FFmpeg превращается в статус Cancelled.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FfmpegThrowsOperationCancelled_ReturnsCancelled()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
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
            .ThrowsAsync(new OperationCanceledException());

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Cancelled);
        result.ErrorCode.Should().Be("cancelled");
    }

    /// <summary>
    /// Проверяет, что неожиданное исключение транскрибации даёт сбой speech-exception.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_UnexpectedException_ReturnsFailedSpeechException()
    {
        // Arrange
        (string inputPath, string outputDir) = CreateInputWithOutputDir();
        SetupReadyPipeline();
        SetupFfmpegSuccess(out _);
        _whisperRunnerMock
            .Setup(w => w.TranscribeAsync(
                It.IsAny<WhisperTranscribeOptions>(),
                It.IsAny<Action<int>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .ThrowsAsync(new InvalidOperationException("сбой инференса"));

        // Act
        ExecutionResult result = await _script.ExecuteSingleAsync(
            inputPath,
            CreateDefaultSettings(),
            outputDir,
            (cur, total, msg, pct, fps, br) => { },
            1,
            1);

        // Assert
        result.Status.Should().Be(ExecutionStatus.Failed);
        result.ErrorCode.Should().Be("speech-exception");
        result.Should().Contain(r => r.Contains("Ошибка распознавания речи"));
    }
}
