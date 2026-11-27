// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;
using FluentAssertions;

using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Проверяет политику удаления реплик, целиком ушедших в минус при сдвиге назад (issue 91).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class SubtitleShiftNegativeCueTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private SubtitleShiftScript _script = null!;
    private TempDirectoryScope? _tempDir;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _script = new SubtitleShiftScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object);
        _tempDir = new TempDirectoryScope();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _tempDir?.Dispose();
        _tempDir = null;
    }

    /// <summary>
    /// Создаёт настройки сдвига назад на заданную величину.
    /// </summary>
    private static Dictionary<string, object> BackwardSettings(int shiftMs) => new()
    {
        { "ShiftMs", shiftMs },
        { "ShiftDirection", "Назад" }
    };

    /// <summary>
    /// Проверяет, что реплика SRT, целиком ушедшая в минус, удаляется, а не превращается в нулевую.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FullyNegativeSrtCue_RemovesCueInsteadOfZeroClamp()
    {
        // Arrange.
        string sourceFile = _tempDir!.CreateFile("minus.srt",
            "1\n00:00:00,500 --> 00:00:01,000\nПервая\n\n2\n00:00:05,000 --> 00:00:07,000\nВторая\n");

        // Act.
        ExecutionResult result = await _script.ExecuteSingleAsync(
            sourceFile,
            BackwardSettings(2000),
            _tempDir.RootPath,
            (idx, tot, msg, pct, fps, bit) => { },
            0,
            1);

        // Assert.
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        string outputFile = Path.Combine(_tempDir.RootPath, "minus_shifted.srt");
        File.Exists(outputFile).Should().BeTrue();
        string outputContent = File.ReadAllText(outputFile);
        outputContent.Should().NotContain("Первая", "ушедшая в минус реплика обязана удаляться");
        outputContent.Should().NotContain("00:00:00,000 --> 00:00:00,000", "нулевые реплики недопустимы");
        outputContent.Should().Contain("00:00:03,000 --> 00:00:05,000");
        outputContent.Should().Contain("Вторая");
        outputContent.Split('\n').Count(l => l.Contains("-->")).Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что строка Dialogue формата ASS, целиком ушедшая в минус, удаляется из файла.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FullyNegativeAssDialogue_RemovesLineInsteadOfZeroClamp()
    {
        // Arrange.
        string sourceFile = _tempDir!.CreateFile("minus.ass",
            "[Script Info]\nTitle: Test\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.50,0:00:01.00,Default,,0,0,0,,Уйдёт в минус\nDialogue: 0,0:00:05.00,0:00:07.00,Default,,0,0,0,,Останется\n");

        // Act.
        ExecutionResult result = await _script.ExecuteSingleAsync(
            sourceFile,
            BackwardSettings(2000),
            _tempDir.RootPath,
            (idx, tot, msg, pct, fps, bit) => { },
            0,
            1);

        // Assert.
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        string outputFile = Path.Combine(_tempDir.RootPath, "minus_shifted.ass");
        File.Exists(outputFile).Should().BeTrue();
        string outputContent = File.ReadAllText(outputFile);
        outputContent.Should().NotContain("Уйдёт в минус", "ушедшая в минус реплика обязана удаляться");
        outputContent.Should().NotContain("0:00:00.00,0:00:00.00", "нулевые реплики недопустимы");
        outputContent.Should().Contain("Dialogue: 0,0:00:03.00,0:00:05.00,Default");
        outputContent.Should().Contain("Останется");
    }

    /// <summary>
    /// Проверяет, что частично ушедшая в минус реплика сохраняется с ограничением начала нулём.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_PartiallyNegativeSrtCue_ClampsStartToZero()
    {
        // Arrange.
        string sourceFile = _tempDir!.CreateFile("partial.srt",
            "1\n00:00:01,000 --> 00:00:03,000\nЧастичная\n");

        // Act.
        ExecutionResult result = await _script.ExecuteSingleAsync(
            sourceFile,
            BackwardSettings(2000),
            _tempDir.RootPath,
            (idx, tot, msg, pct, fps, bit) => { },
            0,
            1);

        // Assert.
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        string outputFile = Path.Combine(_tempDir.RootPath, "partial_shifted.srt");
        File.Exists(outputFile).Should().BeTrue();
        string outputContent = File.ReadAllText(outputFile);
        outputContent.Should().Contain("00:00:00,000 --> 00:00:01,000");
        outputContent.Should().Contain("Частичная");
    }
}
