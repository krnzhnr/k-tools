// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;


namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для скрипта конвертации субтитров SubtitlesConvertScript.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SubtitlesConvertScriptTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<IFFmpegRunner> _ffmpegRunnerMock = null!;
    private IAssParser _assParser = null!;
    private SubtitlesConvertScript _script = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = new Mock<ILogService>();
        _settingsManagerMock = new Mock<ISettingsManager>();
        _pathManagerMock = new Mock<IPathManager>();
        _ffmpegRunnerMock = new Mock<IFFmpegRunner>();
        _assParser = new AssParser();

        // Настраиваем путь для временной папки настроек (где будут создаваться временные файлы субтитров)
        _pathManagerMock.Setup(p => p.GetSettingsDirectory()).Returns(Path.GetTempPath());

        _script = new SubtitlesConvertScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            _assParser
        );
    }

    /// <summary>
    /// Проверяет базовые свойства скрипта.
    /// </summary>
    [TestMethod]
    public void ScriptProperties_VerifyCorrectValues()
    {
        _script.Category.Should().Be("Субтитры");
        _script.IconName.Should().Be(AppConstants.ScriptIcons.SubtitlesConvert);
        _script.RequiredDependencies.Should().Contain("ffmpeg");
        _script.FileExtensions.Should().Contain(".ass");
        _script.FileExtensions.Should().Contain(".srt");
    }

    /// <summary>
    /// Проверяет валидность схемы настроек.
    /// </summary>
    [TestMethod]
    public void SettingsSchema_VerifyFields()
    {
        var schema = _script.SettingsSchema;
        schema.Should().NotBeNull();

        schema.Should().Contain(f => f.Key == "target_format" && f.Type == SettingType.Combo);
        schema.Should().Contain(f => f.Key == "strip_formatting" && f.Type == SettingType.Checkbox);
        schema.Should().Contain(f => f.Key == "keep_styles" && f.Type == SettingType.Checkbox);
        schema.Should().Contain(f => f.Key == "strip_caps" && f.Type == SettingType.Checkbox);
        schema.Should().Contain(f => f.Key == "delete_original" && f.Type == SettingType.Checkbox);
    }

    /// <summary>
    /// Проверяет «быстрый путь» (конвертация через FFmpeg напрямую без парсинга),
    /// когда выключены все опции очистки.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_FastPath_CallsFFmpegDirectly()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_sub.srt");
        File.WriteAllText(tempSourceFile, "1\n00:00:01,000 --> 00:00:03,000\nHello World\n");
        string tempOutputDir = Path.GetTempPath();

        _ffmpegRunnerMock.Setup(r => r.RunAsync(tempSourceFile, It.IsAny<string>(), null, null, false, 0.0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "target_format", "WebVTT" },
            { "strip_formatting", false },
            { "keep_styles", true },
            { "strip_caps", false },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ УСПЕХ"));
            _ffmpegRunnerMock.Verify(r => r.RunAsync(tempSourceFile, It.IsAny<string>(), null, null, false, 0.0, null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            string expectedDest = Path.Combine(tempOutputDir, "test_sub.vtt");
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет фильтрацию строк по стилям, актерам, эффектам и ручным правилам.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_WithFilters_FiltersCorrectly()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_filter.ass");
        string assContent = 
@"[Script Info]
Title: Test
[Events]
Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0:01:00.00,0:01:02.00,Style1,Actor1,,0,0,,Keep this line
Dialogue: 0:01:03.00,0:01:05.00,Style2,Actor2,,0,0,,Skip this line (Style2)
Dialogue: 0:01:06.00,0:01:08.00,Style1,Actor3,,0,0,,Skip this line (Actor3)
Dialogue: 0:01:09.00,0:01:11.00,Style1,Actor1,,0,0,Effect1,Skip this line (Effect1)
Dialogue: 0:01:12.00,0:01:14.00,Style1,Actor1,,0,0,,Manually Excluded
Dialogue: 0:01:15.00,0:01:17.00,Style2,Actor2,,0,0,,Manually Included even though Style2
Dialogue: 0:01:18.00,0:01:20.00,Style1,Actor1,,0,0,,{\\b1}Text with tags{\\b0}
Dialogue: 0:01:21.00,0:01:23.00,Style1,Actor1,,0,0,,SHOUTING TEXT";
        File.WriteAllText(tempSourceFile, assContent);
        string tempOutputDir = Path.GetTempPath();

        // Задаем правила фильтрации
        _script.FilterState.ExcludedStyles.Add("Style2");
        _script.FilterState.ExcludedActors.Add("Actor3");
        _script.FilterState.ExcludedEffects.Add("Effect1");
        
        // Ручное исключение: 4-й индекс диалога (строка "Manually Excluded")
        var exclusions = new HashSet<int> { 4 };
        _script.FilterState.ManualExclusions[tempSourceFile] = exclusions;

        // Ручное включение: 5-й индекс диалога ("Manually Included even though Style2")
        var inclusions = new HashSet<int> { 5 };
        _script.FilterState.ManualInclusions[tempSourceFile] = inclusions;

        string? tempAssContent = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, false, 0.0, null, It.IsAny<CancellationToken>()))
            .Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
                (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>{
                    if (File.Exists(inP))
                    {
                        tempAssContent = File.ReadAllText(inP);
                    }
                }
            )
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "target_format", "WebVTT" },
            { "strip_formatting", true },
            { "keep_styles", false },
            { "strip_caps", true },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
            tempAssContent.Should().NotBeNull();
            
            // Проверяем, какие строки попали во временный файл
            tempAssContent.Should().Contain("Keep this line");
            tempAssContent.Should().NotContain("Skip this line (Style2)");
            tempAssContent.Should().NotContain("Skip this line (Actor3)");
            tempAssContent.Should().NotContain("Skip this line (Effect1)");
            tempAssContent.Should().NotContain("Manually Excluded");
            tempAssContent.Should().Contain("Manually Included even though Style2");
            
            // Теги форматирования должны быть удалены
            tempAssContent.Should().Contain("Text with tags");
            tempAssContent.Should().NotContain("\\b1");
            
            // Капс должен быть удален
            tempAssContent.Should().NotContain("SHOUTING TEXT");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            string expectedDest = Path.Combine(tempOutputDir, "test_filter.vtt");
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Тест производительности: проверка отсутствия зависаний и утечек при обработке файла > 10 МБ.
    /// </summary>
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)] // Таймаут 10 секунд на прохождение теста
    public async Task ExecuteSingleAsync_LargeFile_ProcessesEfficiently()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "large_test.ass");
        
        // Генерируем большой файл ~11 МБ
        using (var writer = new StreamWriter(tempSourceFile, false, Encoding.UTF8))
        {
            writer.WriteLine("[Script Info]");
            writer.WriteLine("Title: Large Test");
            writer.WriteLine("[Events]");
            writer.WriteLine("Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
            
            for (int i = 0; i < 150000; i++)
            {
                writer.WriteLine($"Dialogue: 0:01:00.00,0:01:02.00,Style1,Actor1,,0,0,,Subtitle line number {i} - Some text to increase file size.");
            }
        }

        string tempOutputDir = Path.GetTempPath();
        _ffmpegRunnerMock.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, false, 0.0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "target_format", "WebVTT" },
            { "strip_formatting", true },
            { "keep_styles", false },
            { "strip_caps", false },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            string expectedDest = Path.Combine(tempOutputDir, "large_test.vtt");
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет последовательную фильтрацию по регулярным выражениям (Regex).
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_WithRegexPatterns_FiltersCorrectly()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_regex.ass");
        string assContent = 
@"[Script Info]
Title: Test
[Events]
Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0:01:00.00,0:01:02.00,Style1,Actor1,,0,0,,[вздох] Привет, мир!
Dialogue: 0:01:03.00,0:01:05.00,Style1,Actor1,,0,0,,Тестовое предложение
Dialogue: 0:01:06.00,0:01:08.00,Style1,Actor1,,0,0,,[смех] Удалить эту строку целиком";
        File.WriteAllText(tempSourceFile, assContent);
        string tempOutputDir = Path.GetTempPath();

        string? tempAssContent = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, false, 0.0, null, It.IsAny<CancellationToken>()))
            .Callback<string, string, List<string>, List<string>, bool, double, Action<ProgressInfo>, CancellationToken, ProcessExecutionContext>(
                (inP, outP, extArgs, inArgs, ovr, dur, prog, ct, processContext) =>{
                    if (File.Exists(inP))
                    {
                        tempAssContent = File.ReadAllText(inP);
                    }
                }
            )
            .ReturnsAsync(MockBuilders.ProcessSucceeded());

        var settings = new Dictionary<string, object>
        {
            { "target_format", "WebVTT" },
            { "strip_formatting", false },
            { "keep_styles", false },
            { "strip_caps", false },
            { "delete_original", false },
            { "text_patterns", new List<Dictionary<string, object>>
                {
                    new() { { "word", "\\[[^\\]]+\\]" }, { "active", true }, { "only_part", true } }, // Удаляет квадратные скобки
                    new() { { "word", "Удалить эту строку" }, { "active", true }, { "only_part", false } } // Удаляет всю строку
                }
            }
        };

        try
        {
            // Act
            var results = await _script.ExecuteSingleAsync(tempSourceFile, settings, tempOutputDir, (idx, total, status, pct, fps, bit) => { }, 0, 1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
            tempAssContent.Should().NotBeNull();
            
            // Первая строка: скобки удалены, текст остался
            tempAssContent.Should().Contain("Привет, мир!");
            tempAssContent.Should().NotContain("[вздох]");
            
            // Вторая строка: осталась без изменений
            tempAssContent.Should().Contain("Тестовое предложение");
            
            // Третья строка: должна быть полностью удалена из-за regex "Удалить эту строку"
            tempAssContent.Should().NotContain("Удалить эту строку целиком");
            tempAssContent.Should().NotContain("[смех]");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            string expectedDest = Path.Combine(tempOutputDir, "test_regex.vtt");
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }

    /// <summary>
    /// Проверяет, что входной файл формата WebVTT (.vtt) успешно парсится и передаётся в FFmpeg на конвертацию.
    /// Тест использует Mock&lt;IVttParser&gt; для инъекции в AssParser, чтобы изолировать тест от деталей реального VttParser.
    /// </summary>
    [TestMethod]
    public async Task ExecuteSingleAsync_VttInputFile_SuccessfullyParsesAndConverts()
    {
        // Arrange — создаём временный файл, чтобы AssParser мог его «найти» на диске
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_input.vtt");
        File.WriteAllText(tempSourceFile, "WEBVTT\r\n\r\nФиктивный контент — данные придут из мока");
        string tempOutputDir = Path.GetTempPath();

        // Формируем предсказуемые диалоги через мок IVttParser
        var expectedData = new AssData();
        expectedData.Dialogues.Add(new AssDialogue(
            start: "0:00:01.00",
            end: "0:00:03.50",
            style: "Default",
            actor: "Спикер",
            effect: string.Empty,
            text: "Привет из WebVTT!"));
        expectedData.Dialogues.Add(new AssDialogue(
            start: "0:00:04.00",
            end: "0:00:06.00",
            style: "Default",
            actor: string.Empty,
            effect: string.Empty,
            text: "Вторая реплика WebVTT"));

        var vttParserMock = new Mock<IVttParser>();
        vttParserMock.Setup(p => p.Parse(tempSourceFile)).Returns(expectedData);
        // Мок StripVttTags должен возвращать строку без изменений — иначе AssParser.StripTags получит null и упадёт
        vttParserMock.Setup(p => p.StripVttTags(It.IsAny<string>())).Returns<string>(s => s);

        // Создаём AssParser с инъецированным VttParser-моком и пересобираем скрипт
        var assParserWithVttMock = new AssParser(vttParserMock.Object);
        var script = new SubtitlesConvertScript(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object,
            _ffmpegRunnerMock.Object,
            assParserWithVttMock);

        string? tempAssContent = null;
        _ffmpegRunnerMock.Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<Action<ProgressInfo>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ProcessExecutionContext?>()))
            .Callback<string, string?, List<string>?, List<string>?, bool, double, Action<ProgressInfo>?, CancellationToken, ProcessExecutionContext?>((input, output, extArgs, inArgs, ovr, dur, prog, ct, ctx) =>
            {
                // Читаем содержимое временного ASS-файла только если путь заканчивается на .ass
                if (input.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) && File.Exists(input))
                {
                    tempAssContent = File.ReadAllText(input);
                }
            })
            .ReturnsAsync(MockBuilders.ProcessSucceeded());


        var settings = new Dictionary<string, object>
        {
            { "target_format", "SRT" },
            { "strip_formatting", true },
            { "keep_styles", false },
            { "strip_caps", false },
            { "delete_original", false }
        };

        try
        {
            // Act
            var results = await script.ExecuteSingleAsync(
                tempSourceFile,
                settings,
                tempOutputDir,
                (idx, total, status, pct, fps, bit) => { },
                0,
                1);

            // Assert
            results.Should().Contain(s => s.Contains("✅ Конвертирован"));
            tempAssContent.Should().NotBeNull();
            tempAssContent.Should().Contain("Привет из WebVTT!");
            tempAssContent.Should().Contain("Вторая реплика WebVTT");
            tempAssContent.Should().Contain("Спикер");
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
            string expectedDest = Path.Combine(tempOutputDir, "test_input.srt");
            if (File.Exists(expectedDest)) File.Delete(expectedDest);
        }
    }
}

