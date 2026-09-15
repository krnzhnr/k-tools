// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace KTools_App.Tests;

/// <summary>
/// Набор модульных тестов для проверки компонентов модуля распознавания речи Whisper:
/// парсера вывода, детектора бэкендов, модели опций и валидации схемы настроек скрипта.
/// </summary>
[TestClass]
public class WhisperIntegrationTests
{
    [TestMethod]
    public void TryParseProgress_ValidStderrProgressLine_ReturnsPercent()
    {
        // Arrange
        string line = "whisper_print_progress_callback: progress =  42%";

        // Act
        bool success = WhisperOutputParser.TryParseProgress(line, out int percent);

        // Assert
        success.Should().BeTrue();
        percent.Should().Be(42);
    }

    [TestMethod]
    public void TryParseProgress_NonProgressLine_ReturnsFalse()
    {
        // Arrange
        string line = "system_info: n_threads = 6 / 12 | AVX = 1 | AVX2 = 1 |";

        // Act
        bool success = WhisperOutputParser.TryParseProgress(line, out int percent);

        // Assert
        success.Should().BeFalse();
        percent.Should().Be(0);
    }

    [TestMethod]
    public void TryParseSegment_ValidStdoutSubtitleLine_ReturnsTimecodesAndText()
    {
        // Arrange
        string line = "[00:01:23.450 --> 00:01:28.100]   Привет, это распознанная реплика.";

        // Act
        bool success = WhisperOutputParser.TryParseSegment(line, out TimeSpan start, out TimeSpan end, out string text);

        // Assert
        success.Should().BeTrue();
        start.Should().Be(new TimeSpan(0, 0, 1, 23, 450));
        end.Should().Be(new TimeSpan(0, 0, 1, 28, 100));
        text.Should().Be("Привет, это распознанная реплика.");
    }

    [TestMethod]
    public void WhisperBackendDetector_SubfolderNames_ReturnExpectedStrings()
    {
        // Act & Assert
        WhisperBackendDetector.GetSubfolderName(WhisperBackend.Cpu).Should().Be("whisper-cpu");
        WhisperBackendDetector.GetSubfolderName(WhisperBackend.Cuda).Should().Be("whisper-cuda");

        WhisperBackendDetector.GetDependencyKey(WhisperBackend.Cpu).Should().Be("whisper_cpu");
        WhisperBackendDetector.GetDependencyKey(WhisperBackend.Cuda).Should().Be("whisper_cuda");
    }

    [TestMethod]
    public void SpeechRecognitionScript_SettingsSchema_ContainsExpectedTabsAndKeys()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
        var settingsMock = new Mock<ISettingsManager>();
        var pathMock = new Mock<IPathManager>();
        var whisperRunnerMock = new Mock<IWhisperRunner>();
        var modelManagerMock = new Mock<IWhisperModelManager>();
        var ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        var depManagerMock = new Mock<IDependencyManager>();
        var dialogMock = new Mock<IDialogService>();

        modelManagerMock.Setup(m => m.GetAvailableModels()).Returns(new List<WhisperModelInfo>
        {
            new WhisperModelInfo { Key = "base", DisplayName = "Base", FileName = "ggml-base.bin", SizeMb = 142 }
        });

        var script = new Scripts.SpeechRecognitionScript(
            logMock.Object,
            settingsMock.Object,
            pathMock.Object,
            whisperRunnerMock.Object,
            modelManagerMock.Object,
            ffmpegRunnerMock.Object,
            depManagerMock.Object,
            dialogMock.Object);

        // Act
        var schema = script.SettingsSchema;

        // Assert
        schema.Should().NotBeNull();
        schema.Should().Contain(f => f.Key == "whisper_model");
        schema.Should().Contain(f => f.Key == "whisper_language");
        schema.Should().Contain(f => f.Key == "output_srt");
        schema.Should().Contain(f => f.Key == "output_txt");
        schema.Should().Contain(f => f.Key == "whisper_backend");
        schema.Should().Contain(f => f.Key == "max_segment_length");

        script.Name.Should().Be(AppConstants.ScriptMetadata.SpeechRecognitionName);
        script.Category.Should().Be(AppConstants.ScriptCategory.Subtitles);
        script.IconName.Should().Be(AppConstants.ScriptIcons.SpeechRecognition);
    }

    [TestMethod]
    public void SpeechRecognitionScript_SettingsSchema_FiltersModelsToOnlyDownloaded()
    {
        // Arrange
        var logMock = new Mock<ILogService>();
        var settingsMock = new Mock<ISettingsManager>();
        var pathMock = new Mock<IPathManager>();
        var whisperRunnerMock = new Mock<IWhisperRunner>();
        var modelManagerMock = new Mock<IWhisperModelManager>();
        var ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        var depManagerMock = new Mock<IDependencyManager>();
        var dialogMock = new Mock<IDialogService>();

        modelManagerMock.Setup(m => m.GetAvailableModels()).Returns(new List<WhisperModelInfo>
        {
            new WhisperModelInfo { Key = "tiny", DisplayName = "Tiny", FileName = "ggml-tiny.bin", SizeMb = 75 },
            new WhisperModelInfo { Key = "base", DisplayName = "Base", FileName = "ggml-base.bin", SizeMb = 142 },
            new WhisperModelInfo { Key = "large-v3-turbo", DisplayName = "Large v3 Turbo", FileName = "ggml-large-v3-turbo.bin", SizeMb = 1620 }
        });

        // Имитируем, что скачана только модель "base"
        modelManagerMock.Setup(m => m.IsModelDownloaded("tiny")).Returns(false);
        modelManagerMock.Setup(m => m.IsModelDownloaded("base")).Returns(true);
        modelManagerMock.Setup(m => m.IsModelDownloaded("large-v3-turbo")).Returns(false);

        var script = new Scripts.SpeechRecognitionScript(
            logMock.Object,
            settingsMock.Object,
            pathMock.Object,
            whisperRunnerMock.Object,
            modelManagerMock.Object,
            ffmpegRunnerMock.Object,
            depManagerMock.Object,
            dialogMock.Object);

        // Act
        var schema = script.SettingsSchema;
        var modelField = schema.FirstOrDefault(f => f.Key == "whisper_model");

        // Assert
        modelField.Should().NotBeNull();
        modelField!.Options.Should().ContainSingle(o => o == "base");
        modelField.Options.Should().NotContain("tiny");
        modelField.Options.Should().NotContain("large-v3-turbo");
    }
}
