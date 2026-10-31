// -*- coding: utf-8 -*-
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты преобразования заготовленных заголовков аудиодорожек.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MuxAudioTitlePresetsTests
{
    /// <summary>
    /// Проверяет пустой результат для отсутствующего и мусорного хранимого значения.
    /// </summary>
    [TestMethod]
    public void ToWords_NullAndGarbage_ReturnsEmpty()
    {
        // Arrange
        object?[] cases = { null, "строка", 42, new List<string> { "Без словаря" } };

        // Act
        foreach (object? stored in cases)
        {
            // Assert
            MuxAudioTitlePresets.ToWords(stored).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Проверяет отбор только активных непустых уникальных заготовок.
    /// </summary>
    [TestMethod]
    public void ToWords_MixedRows_ReturnsActiveOnly()
    {
        // Arrange
        var stored = new List<Dictionary<string, object>>
        {
            new() { { "word", "Дубляж" }, { "active", true } },
            new() { { "word", "Скрытый" }, { "active", false } },
            new() { { "word", "  " }, { "active", true } },
            new() { { "word", "Дубляж" }, { "active", true } },
            new() { { "active", true } }
        };

        // Act
        var words = MuxAudioTitlePresets.ToWords(stored);

        // Assert
        words.Should().BeEquivalentTo("Дубляж");
    }

    /// <summary>
    /// Проверяет чтение заготовок из элементов JSON (вид после загрузки с диска).
    /// </summary>
    [TestMethod]
    public void ToWords_JsonElements_ReturnsActiveWords()
    {
        // Arrange
        using var doc = System.Text.Json.JsonDocument.Parse(
            "[{\"word\": \"Озвучка\", \"active\": true}, {\"word\": \"Скрытый\", \"active\": false}]");
        var stored = new List<Dictionary<string, object>>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, object>();
            foreach (var property in element.EnumerateObject())
            {
                row[property.Name] = property.Value.Clone();
            }

            stored.Add(row);
        }

        // Act
        var words = MuxAudioTitlePresets.ToWords(stored);

        // Assert
        words.Should().BeEquivalentTo("Озвучка");
    }

    /// <summary>
    /// Проверяет упаковку строк: обрезка, уникальность и активный флажок.
    /// </summary>
    [TestMethod]
    public void FromWords_RawStrings_PacksTrimmedActiveRows()
    {
        // Arrange
        string[] words = { " Дубляж ", "", "  ", "Озвучка", "Дубляж" };

        // Act
        var stored = MuxAudioTitlePresets.FromWords(words);

        // Assert
        stored.Should().HaveCount(2);
        stored[0]["word"].Should().Be("Дубляж");
        stored[0]["active"].Should().Be(true);
        stored[1]["word"].Should().Be("Озвучка");
        stored[1]["active"].Should().Be(true);
    }

    /// <summary>
    /// Проверяет круговой обход: упаковка и разбор дают исходные строки.
    /// </summary>
    [TestMethod]
    public void Presets_RoundTrip_PreservesWords()
    {
        // Arrange
        string[] words = { "Дубляж", "Авторский перевод" };

        // Act
        var roundTripped = MuxAudioTitlePresets.ToWords(MuxAudioTitlePresets.FromWords(words));

        // Assert
        roundTripped.Should().BeEquivalentTo(words);
    }

    /// <summary>
    /// Создаёт аудиофайл очереди с заданным ручным и встроенным заголовками.
    /// </summary>
    private static FileQueueItem CreateTitledFile(string path, string? manual, string? embedded)
    {
        var file = new FileQueueItem(path) { MuxAudioTitle = manual };
        if (embedded != null)
        {
            var structure = new MediaStructure { FilePath = path };
            structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "audio", Name = embedded });
            file.MediaInfo = structure;
        }

        return file;
    }

    /// <summary>
    /// Проверяет приоритет ручного заголовка над встроенным в действующем заголовке.
    /// </summary>
    [TestMethod]
    public void GetEffectiveTitle_ManualAndEmbedded_ReturnsManual()
    {
        // Arrange
        var file = CreateTitledFile("C:\\Видео\\s01e01.mka", "Ручной", "Встроенный");

        // Act
        string effective = MuxAudioTitlePresets.GetEffectiveTitle(file);

        // Assert
        effective.Should().Be("Ручной");
    }

    /// <summary>
    /// Проверяет откат к встроенному и пустому заголовку.
    /// </summary>
    [TestMethod]
    public void GetEffectiveTitle_NoManual_FallsBack()
    {
        // Arrange
        var withEmbedded = CreateTitledFile("C:\\Видео\\s01e01.mka", null, "Встроенный");
        var bare = CreateTitledFile("C:\\Видео\\s01e02.mka", "  ", null);

        // Act / Assert
        MuxAudioTitlePresets.GetEffectiveTitle(withEmbedded).Should().Be("Встроенный");
        MuxAudioTitlePresets.GetEffectiveTitle(bare).Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет переименование заголовка во всех сериях с совпадением один в один.
    /// </summary>
    [TestMethod]
    public void ApplyRename_SameTitleAcrossSeries_RenamesAllMatches()
    {
        // Arrange
        var first = CreateTitledFile("C:\\Видео\\s01e01.mka", "Озвучка1", null);
        var second = CreateTitledFile("C:\\Видео\\s01e02.mka", "Озвучка1", null);
        var other = CreateTitledFile("C:\\Видео\\s01e01.dub.mka", "Озвучка2", null);
        var files = new[] { first, second, other };

        // Act
        int renamed = MuxAudioTitlePresets.ApplyRename(files, "Озвучка1", "Озвучка 21");

        // Assert
        renamed.Should().Be(2);
        first.MuxAudioTitle.Should().Be("Озвучка 21");
        second.MuxAudioTitle.Should().Be("Озвучка 21");
        other.MuxAudioTitle.Should().Be("Озвучка2");
    }

    /// <summary>
    /// Проверяет переименование по встроенному заголовку и очистку пустым новым.
    /// </summary>
    [TestMethod]
    public void ApplyRename_EmbeddedMatchEmptyNew_ClearsManual()
    {
        // Arrange
        var embedded = CreateTitledFile("C:\\Видео\\s01e01.mka", null, "Озвучка1");
        var manual = CreateTitledFile("C:\\Видео\\s01e02.mka", "Озвучка1", "Другое");
        var files = new[] { embedded, manual };

        // Act
        int renamed = MuxAudioTitlePresets.ApplyRename(files, "Озвучка1", "   ");

        // Assert
        renamed.Should().Be(2);
        embedded.MuxAudioTitle.Should().BeNull();
        manual.MuxAudioTitle.Should().BeNull();
    }
}
