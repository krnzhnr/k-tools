using System.IO;
using FluentAssertions;
using KTools_App.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Модульные тесты для специализированного сервиса парсинга субтитров WebVTT (<see cref="VttParser"/>).
/// </summary>
[TestClass]
public class VttParserTests
{
    private VttParser _parser = null!;

    [TestInitialize]
    public void Setup()
    {
        _parser = new VttParser();
    }

    /// <summary>
    /// Проверяет парсинг стандартного файла WebVTT (.vtt) с идентификаторами реплик и заголовком.
    /// </summary>
    [TestMethod]
    public void Parse_StandardFile_ReturnsParsedDialogues()
    {
        // Arrange
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".vtt");
        string vttContent =
@"WEBVTT - Тестовый файл

1
00:01:20.500 --> 00:01:23.000
Привет, мир!
Вторая строка реплики.

2
00:01:24.100 --> 00:01:26.900
Вторая реплика";
        File.WriteAllText(tempFile, vttContent);

        try
        {
            // Act
            var result = _parser.Parse(tempFile);

            // Assert
            result.Dialogues.Should().HaveCount(2);
            result.Dialogues[0].Start.Should().Be("0:01:20.50");
            result.Dialogues[0].End.Should().Be("0:01:23.00");
            result.Dialogues[0].Text.Should().Be("Привет, мир!\\NВторая строка реплики.");

            result.Dialogues[1].Start.Should().Be("0:01:24.10");
            result.Dialogues[1].End.Should().Be("0:01:26.90");
            result.Dialogues[1].Text.Should().Be("Вторая реплика");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// Проверяет парсинг WebVTT с короткими таймкодами без указания часов (MM:SS.mmm).
    /// </summary>
    [TestMethod]
    public void Parse_ShortTimestampsWithoutHours_ConvertsCorrectly()
    {
        // Arrange
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".vtt");
        string vttContent =
@"WEBVTT

02:15.300 --> 02:18.750
Короткий таймкод без часов";
        File.WriteAllText(tempFile, vttContent);

        try
        {
            // Act
            var result = _parser.Parse(tempFile);

            // Assert
            result.Dialogues.Should().HaveCount(1);
            result.Dialogues[0].Start.Should().Be("0:02:15.30");
            result.Dialogues[0].End.Should().Be("0:02:18.75");
            result.Dialogues[0].Text.Should().Be("Короткий таймкод без часов");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// Проверяет парсинг WebVTT с параметрами размещения (cue settings) и тегами голоса/актёров.
    /// </summary>
    [TestMethod]
    public void Parse_WithCueSettingsAndVoices_ExtractsActorAndStripsSettings()
    {
        // Arrange
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".vtt");
        string vttContent =
@"WEBVTT

00:00:10.500 --> 00:00:13.000 line:0 position:20% size:60% align:start
<v Roger Bingham>Мы находимся в Нью-Йорке.</v>

00:00:14.000 --> 00:00:17.000 align:middle
<v.loud Диктор>Срочные новости &amp; подробности</v>";
        File.WriteAllText(tempFile, vttContent);

        try
        {
            // Act
            var result = _parser.Parse(tempFile);

            // Assert
            result.Dialogues.Should().HaveCount(2);
            result.Dialogues[0].Actor.Should().Be("Roger Bingham");
            result.Dialogues[0].Text.Should().Be("Мы находимся в Нью-Йорке.");

            result.Dialogues[1].Actor.Should().Be("Диктор");
            result.Dialogues[1].Text.Should().Be("Срочные новости &amp; подробности");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// Проверяет, что служебные блоки NOTE, STYLE и REGION игнорируются при парсинге WebVTT.
    /// </summary>
    [TestMethod]
    public void Parse_WithNotesAndStyles_IgnoresCommentsAndStyles()
    {
        // Arrange
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".vtt");
        string vttContent =
@"WEBVTT
Kind: captions
Language: ru

NOTE
Это многострочный комментарий
который не должен быть распознан как реплика.

STYLE
::cue {
  color: yellow;
}

00:00:01.000 --> 00:00:03.000
Реальная реплика

NOTE Однострочный комментарий

00:00:05.000 --> 00:00:07.000
Вторая реальная реплика";
        File.WriteAllText(tempFile, vttContent);

        try
        {
            // Act
            var result = _parser.Parse(tempFile);

            // Assert
            result.Dialogues.Should().HaveCount(2);
            result.Dialogues[0].Text.Should().Be("Реальная реплика");
            result.Dialogues[1].Text.Should().Be("Вторая реальная реплика");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    /// <summary>
    /// Проверяет конвертацию времени WebVTT в формат ASS с различными вариациями.
    /// </summary>
    [TestMethod]
    public void VttTimeToAss_VariousFormats_ConvertsProperly()
    {
        _parser.VttTimeToAss("00:12:34.560").Should().Be("0:12:34.56");
        _parser.VttTimeToAss("12:34.560").Should().Be("0:12:34.56");
        _parser.VttTimeToAss("1:02:03.456").Should().Be("1:02:03.45");
        _parser.VttTimeToAss("00:00:05.1").Should().Be("0:00:05.10");
        _parser.VttTimeToAss("00:00:05.12").Should().Be("0:00:05.12");
        _parser.VttTimeToAss("00:12:34,560").Should().Be("0:12:34.56");
        _parser.VttTimeToAss("").Should().Be("0:00:00.00");
    }

    /// <summary>
    /// Проверяет очистку тегов WebVTT (классы стилей, караоке-таймкоды, HTML-сущности).
    /// </summary>
    [TestMethod]
    public void StripVttTags_WithVttTagsAndKaraokeAndEntities_CleansText()
    {
        string raw = "<c.yellow>Привет</c> <00:01.500>мир &amp; &lt;вселенная&gt;!";
        string cleaned = _parser.StripVttTags(raw);

        cleaned.Should().Be("Привет мир & <вселенная>!");
    }
}
