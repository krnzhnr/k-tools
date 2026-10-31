// -*- coding: utf-8 -*-
using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Controls;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты фильтра по идентификатору дорожки (ключ "id").
/// Идентификатор доступен только для video/audio/subtitles; у attachments
/// ключа "id" нет — вложения фильтруются по расширению и имени.
/// Проверяют точное совпадение без подстрок, изоляцию диапазонов по категориям,
/// валидность нуля, сбор опций и удаление устаревших правил (Prune).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class TrackSelectionIdFilterTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private TrackSelectionViewModel _vm = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _vm = new TrackSelectionViewModel(_logServiceMock.Object, _settingsManagerMock.Object);
    }

    /// <summary>
    /// Создаёт очередь из одного файла со смешанными идентификаторами по категориям.
    /// </summary>
    private static ObservableCollection<FileQueueItem> CreateMixedIdFiles()
    {
        var structure = new MediaStructure { FilePath = "C:\\media\\film.mkv" };
        structure.Tracks.Add(new MediaTrack { TrackId = 0, TrackType = "video", Codec = "hevc", Language = "eng" });
        structure.Tracks.Add(new MediaTrack { TrackId = 2, TrackType = "video", Codec = "avc", Language = "rus" });
        structure.Tracks.Add(new MediaTrack { TrackId = 1, TrackType = "audio", Codec = "ac3", Language = "rus", Channels = 6 });
        structure.Tracks.Add(new MediaTrack { TrackId = 10, TrackType = "audio", Codec = "dts", Language = "eng", Channels = 2 });
        structure.Tracks.Add(new MediaTrack { TrackId = 5, TrackType = "subtitles", Codec = "srt", Language = "rus" });
        structure.Attachments.Add(new MediaAttachment { AttachmentId = 0, FileName = "Arial.ttf", MimeType = "application/x-truetype-font" });
        structure.Attachments.Add(new MediaAttachment { AttachmentId = 3, FileName = "DejaVu.otf", MimeType = "font/otf" });

        return new ObservableCollection<FileQueueItem>
        {
            new FileQueueItem("C:\\media\\film.mkv") { MediaInfo = structure }
        };
    }

    /// <summary>
    /// Проверяет, что идентификаторы собираются отдельно по своей категории,
    /// а у вложений ключа идентификатора нет.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_FileWithMixedIds_PopulatesEachCategorySeparately()
    {
        // Arrange
        _vm.Files = CreateMixedIdFiles();

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["video"]["id"].Should().BeEquivalentTo("0", "2");
        _vm.DynamicOptions["audio"]["id"].Should().BeEquivalentTo("1", "10");
        _vm.DynamicOptions["subtitles"]["id"].Should().BeEquivalentTo("5");
        _vm.DynamicOptions["attachments"].Should().NotContainKey("id", "у вложений фильтра по идентификатору нет");
        _vm.DynamicOptions["attachments"]["extension"].Should().BeEquivalentTo(".ttf", ".otf");
    }

    /// <summary>
    /// Проверяет, что повторный сбор не дублирует идентификаторы.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_CalledTwice_DoesNotDuplicateIds()
    {
        // Arrange
        _vm.Files = CreateMixedIdFiles();
        _vm.CollectDynamicOptions();

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["video"]["id"].Should().HaveCount(2);
        _vm.DynamicOptions["audio"]["id"].Should().HaveCount(2);
    }

    /// <summary>
    /// Проверяет точное совпадение правила идентификатора видеодорожки.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_VideoIdExactMatch_ReturnsTrue()
    {
        // Arrange
        var item = new TrackNodeItem { Track = new MediaTrack { TrackType = "video", TrackId = 1 } };
        _vm.ActiveRules["video"]["id"].Add("1");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что правило "1" не матчит дорожки 10 и 11 (без подстрок).
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_VideoIdSubstring_DoesNotMatch()
    {
        // Arrange
        var item10 = new TrackNodeItem { Track = new MediaTrack { TrackType = "video", TrackId = 10 } };
        var item11 = new TrackNodeItem { Track = new MediaTrack { TrackType = "video", TrackId = 11 } };
        _vm.ActiveRules["video"]["id"].Add("1");

        // Act
        bool matches10 = _vm.MatchesFilterRules(item10);
        bool matches11 = _vm.MatchesFilterRules(item11);

        // Assert
        matches10.Should().BeFalse("совпадение строго точное, подстроки запрещены");
        matches11.Should().BeFalse("совпадение строго точное, подстроки запрещены");
    }

    /// <summary>
    /// Проверяет, что идентификатор 0 считается валидным значением фильтра.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_VideoIdZero_IsValidMatch()
    {
        // Arrange
        var item = new TrackNodeItem { Track = new MediaTrack { TrackType = "video", TrackId = 0 } };
        _vm.ActiveRules["video"]["id"].Add("0");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue("ноль — полноценный идентификатор дорожки");
    }

    /// <summary>
    /// Проверяет изоляцию категорий: правило video не применяется к audio.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_IdRuleOfOtherCategory_DoesNotApply()
    {
        // Arrange
        var audioItem = new TrackNodeItem { Track = new MediaTrack { TrackType = "audio", TrackId = 5 } };
        _vm.ActiveRules["video"]["id"].Add("5");

        // Act
        bool matches = _vm.MatchesFilterRules(audioItem);

        // Assert
        matches.Should().BeFalse("правило категории video не действует на audio");
    }

    /// <summary>
    /// Проверяет, что у вложений нет ключей идентификатора в опциях и правилах.
    /// </summary>
    [TestMethod]
    public void Attachments_ContainNoIdKeys()
    {
        // Arrange — ключи проверяются сразу после создания модели,
        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["attachments"].Should().NotContainKey("id", "у вложений фильтра по идентификатору нет");
        _vm.ActiveRules["attachments"].Should().NotContainKey("id", "у вложений фильтра по идентификатору нет");
        _vm.DynamicOptions["video"].Should().ContainKey("id");
        _vm.DynamicOptions["audio"].Should().ContainKey("id");
        _vm.DynamicOptions["subtitles"].Should().ContainKey("id");
    }

    /// <summary>
    /// Проверяет, что вложения по-прежнему фильтруются по расширению без участия идентификатора.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_FontExtensionRule_MatchesWithoutId()
    {
        // Arrange
        var item = new TrackNodeItem
        {
            IsFont = true,
            Attachment = new MediaAttachment { AttachmentId = 3, FileName = "DejaVu.otf", MimeType = "font/otf" }
        };
        _vm.ActiveRules["attachments"]["extension"].Add(".otf");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue("вложения фильтруются по расширению, идентификатор не требуется");
    }

    /// <summary>
    /// Проверяет, что правило идентификатора дорожек не применяется к вложениям.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_TrackIdRule_DoesNotApplyToFonts()
    {
        // Arrange
        var item = new TrackNodeItem
        {
            IsFont = true,
            Attachment = new MediaAttachment { AttachmentId = 1, FileName = "Arial.ttf", MimeType = "application/x-truetype-font" }
        };
        _vm.ActiveRules["video"]["id"].Add("1");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeFalse("правило идентификатора видеодорожек не действует на вложения");
    }

    /// <summary>
    /// Проверяет, что удаление устаревшего идентификатора сохраняет валидный.
    /// </summary>
    [TestMethod]
    public void PruneObsoleteRules_StaleIdRule_RemovedKeepsValid()
    {
        // Arrange
        _vm.DynamicOptions["audio"]["id"].Add("1");
        _vm.ActiveRules["audio"]["id"].Add("1");
        _vm.ActiveRules["audio"]["id"].Add("99");

        // Act
        _vm.PruneObsoleteRules();

        // Assert
        _vm.ActiveRules["audio"]["id"].Should().Contain("1");
        _vm.ActiveRules["audio"]["id"].Should().NotContain("99", "устаревший идентификатор должен быть удалён");
    }

    /// <summary>
    /// Проверяет, что пустой набор опций идентификаторов очищает правила.
    /// </summary>
    [TestMethod]
    public void PruneObsoleteRules_EmptyIdOptions_ClearsIdRules()
    {
        // Arrange
        _vm.ActiveRules["video"]["id"].Add("7");
        // DynamicOptions["video"]["id"] пуст

        // Act
        _vm.PruneObsoleteRules();

        // Assert
        _vm.ActiveRules["video"]["id"].Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что смена файлов автоматически прунит устаревший идентификатор.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_FileChanged_IdRuleAutoPruned()
    {
        // Arrange
        _vm.Files = CreateMixedIdFiles();
        _vm.ActiveRules["video"]["id"].Add("42");

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.ActiveRules["video"]["id"].Should().NotContain("42",
            "правила автоматически прунятся при смене набора файлов");
    }

    /// <summary>
    /// Проверяет, что очистка категории затрагивает и ключ идентификатора.
    /// </summary>
    [TestMethod]
    public void ClearRules_Category_ClearsIdRules()
    {
        // Arrange
        _vm.ActiveRules["subtitles"]["id"].Add("5");
        _vm.ActiveRules["audio"]["id"].Add("1");

        // Act
        _vm.ClearRules("subtitles");

        // Assert
        _vm.ActiveRules["subtitles"]["id"].Should().BeEmpty();
        _vm.ActiveRules["audio"]["id"].Should().Contain("1", "другие категории не затрагиваются");
    }
}
