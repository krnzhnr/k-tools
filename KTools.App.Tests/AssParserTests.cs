// -*- coding: utf-8 -*-
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Infrastructure;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты для парсера субтитров AssParser.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class AssParserTests
{
    private AssParser _parser = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new AssParser();
    }

    /// <summary>
    /// Проверяет удаление ASS и HTML тегов из текста субтитров.
    /// </summary>
    [TestMethod]
    public void StripTags_WithTags_RemovesThemCorrectly()
    {
        // Arrange
        string rawText = "{\\an8}{\\b1}Привет, <i>мир</i>!{\\b0}";

        // Act
        string result = _parser.StripTags(rawText);

        // Assert
        result.Should().Be("Привет, мир!");
    }

    /// <summary>
    /// Проверяет распознавание капс-диалогов.
    /// </summary>
    [TestMethod]
    public void IsFullCaps_AllCapsText_ReturnsTrue()
    {
        // Arrange
        string text = "КРИК ИЗ ШКАФА";

        // Act
        bool result = _parser.IsFullCaps(text);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что короткие реплики (менее 2 букв) не считаются капсом.
    /// </summary>
    [TestMethod]
    public void IsFullCaps_ShortText_ReturnsFalse()
    {
        // Arrange
        string text = "А";

        // Act
        bool result = _parser.IsFullCaps(text);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет конвертацию таймкода ASS в WebVTT.
    /// </summary>
    [TestMethod]
    public void AssTimeToVtt_ValidTime_ConvertsCorrectly()
    {
        // Arrange
        string assTime = "0:12:34.56"; // 12 минут, 34 секунды, 560 миллисекунд

        // Act
        string result = AssParser.AssTimeToVtt(assTime);

        // Assert
        result.Should().Be("00:12:34.560");
    }

    /// <summary>
    /// Проверяет конвертацию таймкода SRT в ASS.
    /// </summary>
    [TestMethod]
    public void SrtTimeToAss_ValidTime_ConvertsCorrectly()
    {
        // Arrange
        string srtTime = "00:12:34,560";

        // Act
        string result = AssParser.SrtTimeToAss(srtTime);

        // Assert
        result.Should().Be("0:12:34.56");
    }

    /// <summary>
    /// Проверяет парсинг ASS файла с тестовым диалогом.
    /// </summary>
    [TestMethod]
    public void ParseAss_ValidFile_ReturnsParsedDialogue()
    {
        // Arrange
        string tempFile = Path.GetTempFileName();
        string assContent = 
@"[Script Info]
Title: Test

[Events]
Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0:01:20.50,0:01:23.00,Default,Actor1,0000,0000,0000,,Привет!";
        File.WriteAllText(tempFile, assContent);

        try
        {
            // Act
            var result = _parser.Parse(tempFile);

            // Assert
            result.Dialogues.Should().HaveCount(1);
            result.Dialogues[0].Start.Should().Be("0:01:20.50");
            result.Dialogues[0].End.Should().Be("0:01:23.00");
            result.Dialogues[0].Actor.Should().Be("Actor1");
            result.Dialogues[0].Text.Should().Be("Привет!");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
