// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта сдвига тайминга субтитров SubtitleShiftScript.
/// Все комментарии и логи выполнены на русском языке в соответствии с требованиями.
/// </summary>
[TestClass]
public class SubtitleShiftScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private SubtitleShiftScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();

        _script = new SubtitleShiftScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object
        );
    }

    /// <summary>
    /// Проверяет метаданные и свойства скрипта SubtitleShiftScript.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Субтитры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.SubtitlesShift);
        _script.RequiredDependencies.Should().BeEmpty();
        _script.FileExtensions.Should().Contain(".ass");
        _script.FileExtensions.Should().Contain(".srt");
        _script.FileExtensions.Should().Contain(".vtt");
    }

    /// <summary>
    /// Проверяет корректность схемы настроек скрипта SubtitleShiftScript.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_VerifyFields()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();
        schema.Should().Contain(f => f.Key == "ShiftMs" && f.Type == SettingType.Text);
        schema.Should().Contain(f => f.Key == "ShiftDirection" && f.Type == SettingType.Combo);
    }

    /// <summary>
    /// Проверяет успешный сдвиг тайминга субтитров формата SRT вперед.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_SrtForward_ShiftsTimestamps()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"test_sub_{Guid.NewGuid():N}.srt");
        string srtContent = 
@"1
00:01:20,100 --> 00:01:23,500
Привет, мир!
";
        File.WriteAllText(tempSourceFile, srtContent, Encoding.UTF8);
        string tempOutputDir = Path.GetTempPath();

        var settings = new Dictionary<string, object>
        {
            { "ShiftMs", 1500 },
            { "ShiftDirection", "Вперед" }
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, tot, msg, pct, fps, bit) => {},
                0,
                1
            );

            // Assert
            result.Should().Contain(r => r.StartsWith("✔ Сдвиг выполнен успешно:"));
            string expectedOutputFile = Path.Combine(tempOutputDir, $"{Path.GetFileNameWithoutExtension(tempSourceFile)}_shifted.srt");
            File.Exists(expectedOutputFile).Should().BeTrue();

            string outputContent = File.ReadAllText(expectedOutputFile, Encoding.UTF8);
            outputContent.Should().Contain("00:01:21,600 --> 00:01:25,000");

            if (File.Exists(expectedOutputFile))
            {
                File.Delete(expectedOutputFile);
            }
        }
        finally
        {
            if (File.Exists(tempSourceFile))
            {
                File.Delete(tempSourceFile);
            }
        }
    }

    /// <summary>
    /// Проверяет успешный сдвиг тайминга субтитров формата ASS назад с ограничением на ноль.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AssBackward_ShiftsTimestampsAndCapsAtZero()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"test_sub_{Guid.NewGuid():N}.ass");
        string assContent = 
@"[Script Info]
Title: Test

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:02.50,0:00:05.00,Default,,0,0,0,,Привет!
Dialogue: 0,0:00:00.50,0:00:03.00,Default,,0,0,0,,Быстрый старт!
";
        File.WriteAllText(tempSourceFile, assContent, Encoding.UTF8);
        string tempOutputDir = Path.GetTempPath();

        var settings = new Dictionary<string, object>
        {
            { "ShiftMs", 1000 },
            { "ShiftDirection", "Назад" }
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, tot, msg, pct, fps, bit) => {},
                0,
                1
            );

            // Assert
            result.Should().Contain(r => r.StartsWith("✔ Сдвиг выполнен успешно:"));
            string expectedOutputFile = Path.Combine(tempOutputDir, $"{Path.GetFileNameWithoutExtension(tempSourceFile)}_shifted.ass");
            File.Exists(expectedOutputFile).Should().BeTrue();

            string outputContent = File.ReadAllText(expectedOutputFile, Encoding.UTF8);
            // 2.50 - 1.00 = 1.50 -> 0:00:01.50
            // 5.00 - 1.00 = 4.00 -> 0:00:04.00
            outputContent.Should().Contain("Dialogue: 0,0:00:01.50,0:00:04.00,Default");
            // 0.50 - 1.00 = -0.50 -> Срез на 0:00:00.00
            // 3.00 - 1.00 = 2.00 -> 0:00:02.00
            outputContent.Should().Contain("Dialogue: 0,0:00:00.00,0:00:02.00,Default");

            if (File.Exists(expectedOutputFile))
            {
                File.Delete(expectedOutputFile);
            }
        }
        finally
        {
            if (File.Exists(tempSourceFile))
            {
                File.Delete(tempSourceFile);
            }
        }
    }

    /// <summary>
    /// Проверяет успешный сдвиг тайминга субтитров при передаче сдвига в формате времени Aegisub (Ч:ММ:СС.сс).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_AegisubTimeFormat_ShiftsTimestamps()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), $"test_sub_{Guid.NewGuid():N}.srt");
        string srtContent = 
@"1
00:01:20,100 --> 00:01:23,500
Привет, Aegisub!
";
        File.WriteAllText(tempSourceFile, srtContent, Encoding.UTF8);
        string tempOutputDir = Path.GetTempPath();

        // 0:00:01.50 = 1 секунда и 50 сотых = 1500 мс
        var settings = new Dictionary<string, object>
        {
            { "ShiftMs", "0:00:01.50" },
            { "ShiftDirection", "Вперед" }
        };

        try
        {
            // Act
            var result = await _script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, tot, msg, pct, fps, bit) => {},
                0,
                1
            );

            // Assert
            result.Should().Contain(r => r.StartsWith("✔ Сдвиг выполнен успешно:"));
            string expectedOutputFile = Path.Combine(tempOutputDir, $"{Path.GetFileNameWithoutExtension(tempSourceFile)}_shifted.srt");
            File.Exists(expectedOutputFile).Should().BeTrue();

            string outputContent = File.ReadAllText(expectedOutputFile, Encoding.UTF8);
            outputContent.Should().Contain("00:01:21,600 --> 00:01:25,000");

            if (File.Exists(expectedOutputFile))
            {
                File.Delete(expectedOutputFile);
            }
        }
        finally
        {
            if (File.Exists(tempSourceFile))
            {
                File.Delete(tempSourceFile);
            }
        }
    }
}
