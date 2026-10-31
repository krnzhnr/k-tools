// -*- coding: utf-8 -*-
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Implementations;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты разбора списка изменений и каталога анимаций окна «Что нового».
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class ReleaseNotesTests
{
    private const string SampleBody =
        "### Главное\n"
        + "1. Главный пункт первый.\n"
        + "2. Главный пункт второй.\n"
        + "### Добавлено\n"
        + "1. Новый пункт первый.\n"
        + "2. Новый пункт второй.\n"
        + "### Исправлено\n"
        + "- Мелкое исправление.\n";

    [TestMethod]
    public void Parse_BodyWithHighlights_ParsesHighlightsAndSections()
    {
        // Act
        ReleaseNotes notes = ReleaseNotesParser.Parse("2.1.9", "Релиз", SampleBody, "night-coast");

        // Assert
        notes.Version.Should().Be("2.1.9");
        notes.ArtPieceName.Should().Be("night-coast");
        notes.Highlights.Should().HaveCount(2);
        notes.Sections.Should().HaveCount(2);
        notes.Sections[0].Title.Should().Be("Добавлено");
        notes.Sections[0].Items.Should().HaveCount(2);
        notes.Sections[1].Title.Should().Be("Исправлено");
        notes.Sections[1].Items.Should().ContainSingle().Which.Should().Be("Мелкое исправление.");
    }

    [TestMethod]
    public void Parse_BodyWithoutHighlights_TakesFirstItemsAsHighlights()
    {
        // Act
        ReleaseNotes notes = ReleaseNotesParser.Parse(
            "2.1.9",
            "Релиз",
            "### Добавлено\n1. Раз.\n2. Два.\n3. Три.\n4. Четыре.\n5. Пять.\n6. Шесть.\n");

        // Assert
        notes.Highlights.Should().HaveCount(5);
        notes.Sections.Should().ContainSingle();
    }

    [TestMethod]
    public void Parse_UnknownSection_KeepsRawName()
    {
        // Act
        ReleaseNotes notes = ReleaseNotesParser.Parse(
            "1.0.0",
            "Релиз",
            "### Эксперименты\n1. Смелый пункт.\n");

        // Assert
        notes.Sections.Should().ContainSingle().Which.Title.Should().Be("Эксперименты");
    }

    [TestMethod]
    public void Parse_EmptyBody_ReturnsEmptyNotes()
    {
        // Act
        ReleaseNotes notes = ReleaseNotesParser.Parse("1.0.0", "Релиз", null);

        // Assert
        notes.Highlights.Should().BeEmpty();
        notes.Sections.Should().BeEmpty();
        notes.ArtPieceName.Should().Be(WhatsNewArtCatalog.DefaultPiece);
    }

    [TestMethod]
    public void PickRandom_Always_ReturnsCatalogItem()
    {
        // Act
        string piece = WhatsNewArtCatalog.PickRandom(null, new Random(42));

        // Assert
        WhatsNewArtCatalog.Contains(piece).Should().BeTrue();
    }

    [TestMethod]
    public void PickRandom_WithPrevious_ReturnsDifferentPiece()
    {
        // Arrange
        var random = new Random(7);

        // Act
        for (int i = 0; i < 20; i++)
        {
            string piece = WhatsNewArtCatalog.PickRandom("alpine-dawn", random);

            // Assert
            piece.Should().NotBe("alpine-dawn");
        }
    }

    [TestMethod]
    public void PickNext_ReturnsNextInOrderAndWrapsAround()
    {
        // Act
        string second = WhatsNewArtCatalog.PickNext("alpine-dawn");
        string first = WhatsNewArtCatalog.PickNext("volcano-night");
        string fromUnknown = WhatsNewArtCatalog.PickNext("donut");

        // Assert
        second.Should().Be("amer-fort");
        first.Should().Be("alpine-dawn");
        fromUnknown.Should().Be("alpine-dawn");
    }

    [TestMethod]
    public void PickNextSequence_ReturnsFollowingItemsInOrder()
    {
        // Act
        var sequence = WhatsNewArtCatalog.PickNextSequence("alpine-dawn", 3);

        // Assert
        sequence.Should().ContainInOrder("amer-fort", "aurora-fjord", "black-prism");
    }

    [TestMethod]
    public void FilterAvailable_KeepsCatalogOrderAndIgnoresUnknown()
    {
        // Act
        var available = WhatsNewArtCatalog.FilterAvailable(
            new[] { "tokyo-rain.gif", "DONUT.gif", "alpine-dawn", "night-coast.GIF" });

        // Assert
        available.Should().ContainInOrder("alpine-dawn", "night-coast", "tokyo-rain");
        available.Should().HaveCount(3);
    }

    [TestMethod]
    public void FilterAvailable_WithAsciiRestPrefix_MatchesScenes()
    {
        // Act
        var available = WhatsNewArtCatalog.FilterAvailable(
            new[] { "ascii-rest-kyoto-dusk.gif", "ascii-rest-alpine-dawn.gif" });

        // Assert
        available.Should().ContainInOrder("alpine-dawn", "kyoto-dusk");
    }

    [TestMethod]
    public void Contains_UnknownName_ReturnsFalse()
    {
        // Assert
        WhatsNewArtCatalog.Contains("donut").Should().BeFalse();
        WhatsNewArtCatalog.Contains(null).Should().BeFalse();
        WhatsNewArtCatalog.Contains("  ").Should().BeFalse();
    }

    [TestMethod]
    public void WhatsNewGate_SameVersion_ReturnsFalse()
    {
        // Assert
        WhatsNewGate.ShouldShow("2.2.0", "2.2.0").Should().BeFalse();
    }

    [TestMethod]
    public void WhatsNewGate_DifferentVersion_ReturnsTrue()
    {
        // Assert
        WhatsNewGate.ShouldShow("2.1.8", "2.2.0").Should().BeTrue();
        WhatsNewGate.ShouldShow(string.Empty, "2.2.0").Should().BeTrue();
        WhatsNewGate.ShouldShow(null, "2.2.0").Should().BeTrue();
    }

    [TestMethod]
    public void WhatsNewGate_EmptyBakedVersion_ReturnsFalse()
    {
        // Assert
        WhatsNewGate.ShouldShow(string.Empty, string.Empty).Should().BeFalse();
        WhatsNewGate.ShouldShow("2.2.0", null).Should().BeFalse();
    }

    [TestMethod]
    public void WhatsNewGate_DisabledAutoshow_ReturnsFalse()
    {
        // Assert
        WhatsNewGate.ShouldShow("2.1.8", "2.2.0", false).Should().BeFalse();
        WhatsNewGate.ShouldShow(null, "2.2.0", false).Should().BeFalse();
    }

    [TestMethod]
    public void WhatsNewGate_NoContent_ReturnsFalse()
    {
        // Пустая дельта пререлиза автопоказ пропускает, даже если версия новая.
        // Assert
        WhatsNewGate.ShouldShow("2.2.0-preview.170", "2.2.0-preview.171", true, false).Should().BeFalse();
        WhatsNewGate.ShouldShow(null, "2.2.0-preview.171", true, false).Should().BeFalse();
    }

    [TestMethod]
    public void WhatsNewGate_PreviewVersionDiffers_ReturnsTrue()
    {
        // Каждое новое превью показывает окно, стабильный релиз после превью — тоже.
        // Assert
        WhatsNewGate.ShouldShow("2.2.0-preview.170", "2.2.0-preview.171").Should().BeTrue();
        WhatsNewGate.ShouldShow("2.2.0-preview.171", "2.2.0").Should().BeTrue();
        WhatsNewGate.ShouldShow("2.2.0", "2.2.0").Should().BeFalse();
    }

    [TestMethod]
    public void TryLoadBakedNotes_ValidFile_ParsesVersionAndBody()
    {
        // Arrange
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        string file = System.IO.Path.Combine(dir, "release-notes.md");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(
                file,
                "# 2.2.0\n\n### Главное\n\n1. Главный пункт.\n\n### Добавлено\n\n1. Новый пункт.\n");

            // Act
            bool ok = ReleaseNotesLoader.TryLoadBakedNotes(file, out ReleaseNotes? notes);

            // Assert
            ok.Should().BeTrue();
            notes.Should().NotBeNull();
            notes!.Version.Should().Be("2.2.0");
            notes.Highlights.Should().ContainSingle().Which.Should().Be("Главный пункт.");
            notes.Sections.Should().ContainSingle().Which.Title.Should().Be("Добавлено");
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void TryLoadBakedNotes_PreviewVersion_ParsesFullVersion()
    {
        // Arrange
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        string file = System.IO.Path.Combine(dir, "release-notes.md");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(
                file,
                "# 2.2.0-preview.171\n\n### Добавлено\n\n1. Новый пункт.\n");

            // Act
            bool ok = ReleaseNotesLoader.TryLoadBakedNotes(file, out ReleaseNotes? notes);

            // Assert
            ok.Should().BeTrue();
            notes.Should().NotBeNull();
            notes!.Version.Should().Be("2.2.0-preview.171");
            notes.Sections.Should().ContainSingle().Which.Title.Should().Be("Добавлено");
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void TryLoadBakedNotes_MissingFile_ReturnsFalse()
    {
        // Act
        bool ok = ReleaseNotesLoader.TryLoadBakedNotes(
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                System.Guid.NewGuid().ToString("N"),
                "release-notes.md"),
            out ReleaseNotes? notes);

        // Assert
        ok.Should().BeFalse();
        notes.Should().BeNull();
    }

    [TestMethod]
    public void ReleaseNotesSection_EmptyTitle_ThrowsArgumentException()
    {
        // Act
        Action act = () => new ReleaseNotesSection("  ", new[] { "Пункт" });

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public async Task ShowWhatsNewAsync_NullNotes_ThrowsBeforeXamlRoot()
    {
        // Arrange
        var service = new DialogService();

        // Act
        Func<Task> act = () => service.ShowWhatsNewAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
