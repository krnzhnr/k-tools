// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Controls;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты TrackSelectionViewModel — модели выбора дорожек и вложений.
/// Проверяют сбор уникальных опций фильтрации (CollectDynamicOptions),
/// очистку и удаление устаревших правил (ClearRules/ClearAllRules/PruneObsoleteRules),
/// сопоставление узлов активным правилам (MatchesFilterRules)
/// и null-защиту конструктора.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class TrackSelectionViewModelTests
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
    /// Проверяет, что конструктор с null бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        // Act
        Action logNull = () => new TrackSelectionViewModel(null!, _settingsManagerMock.Object);
        Action settingsNull = () => new TrackSelectionViewModel(_logServiceMock.Object, null!);

        // Assert
        logNull.Should().Throw<ArgumentNullException>();
        settingsNull.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Создаёт FileQueueItem с MediaStructure из видео-, аудио-, субтитровой дорожек и шрифта.
    /// </summary>
    private static FileQueueItem CreateFileWithStructure()
    {
        var structure = new MediaStructure { FilePath = "C:\\media\\film.mkv" };
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 0,
            TrackType = "video",
            Codec = "hevc",
            Language = "eng",
            Resolution = "1920x1080",
            Name = "Основная видео"
        });
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 1,
            TrackType = "audio",
            Codec = "ac3",
            Language = "rus",
            Channels = 6,
            Name = "Дубляж"
        });
        structure.Tracks.Add(new MediaTrack
        {
            TrackId = 2,
            TrackType = "subtitles",
            Codec = "srt",
            Language = "und",
            Name = "Субтитры форс"
        });
        structure.Attachments.Add(new MediaAttachment
        {
            AttachmentId = 0,
            FileName = "ArialBold.ttf",
            MimeType = "application/x-truetype-font"
        });

        return new FileQueueItem("C:\\media\\film.mkv") { MediaInfo = structure };
    }

    /// <summary>
    /// Проверяет сбор уникальных свойств видео/аудио/субтитров/вложений.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_FileWithAllTrackTypes_PopulatesAllCategories()
    {
        // Arrange
        var files = new ObservableCollection<FileQueueItem> { CreateFileWithStructure() };
        _vm.Files = files;

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["video"]["language"].Should().Contain("ENG");
        _vm.DynamicOptions["video"]["codec"].Should().Contain("HEVC");
        _vm.DynamicOptions["video"]["resolution"].Should().Contain("1920x1080");
        _vm.DynamicOptions["video"]["name"].Should().Contain("Основная видео");

        _vm.DynamicOptions["audio"]["language"].Should().Contain("RUS");
        _vm.DynamicOptions["audio"]["codec"].Should().Contain("AC3");
        _vm.DynamicOptions["audio"]["channels"].Should().Contain("6 ch");
        _vm.DynamicOptions["audio"]["name"].Should().Contain("Дубляж");

        _vm.DynamicOptions["subtitles"]["language"].Should().Contain("Неизвестный", "und маппится в Неизвестный");
        _vm.DynamicOptions["subtitles"]["codec"].Should().Contain("SRT");
        _vm.DynamicOptions["subtitles"]["name"].Should().Contain("Субтитры форс");

        _vm.DynamicOptions["attachments"]["extension"].Should().Contain(".ttf");
        _vm.DynamicOptions["attachments"]["name"].Should().Contain("ArialBold");
    }

    /// <summary>
    /// Проверяет, что undefined-язык и пустой кодек превращаются в "Неизвестный"/"Неизвестно".
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_UnknownLanguageAndChannels_MappedToUnknown()
    {
        // Arrange
        var structure = new MediaStructure();
        structure.Tracks.Add(new MediaTrack { TrackType = "audio", Language = "und", Channels = 0 });
        var files = new ObservableCollection<FileQueueItem>
        {
            new FileQueueItem("C:\\media\\a.mkv") { MediaInfo = structure }
        };
        _vm.Files = files;

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["audio"]["language"].Should().Contain("Неизвестный");
        _vm.DynamicOptions["audio"]["channels"].Should().Contain("Неизвестно");
    }

    /// <summary>
    /// Проверяет, что повторный сбор опций не дублирует уникальные значения (HashSet).
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_CalledTwice_DoesNotDuplicateValues()
    {
        // Arrange
        var files = new ObservableCollection<FileQueueItem> { CreateFileWithStructure() };
        _vm.Files = files;
        _vm.CollectDynamicOptions();

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions["video"]["language"].Count.Should().Be(1, "HashSet исключает дубликаты");
        _vm.DynamicOptions["audio"]["codec"].Count.Should().Be(1);
    }

    /// <summary>
    /// Проверяет, что CollectDynamicOptions с null-очередью файлов не бросает исключений.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_NullFiles_DoesNotThrowAndClearsOptions()
    {
        // Arrange
        _vm.Files = null;
        _vm.DynamicOptions["video"]["language"].Add("RUS");

        // Act
        Action act = () => _vm.CollectDynamicOptions();

        // Assert
        act.Should().NotThrow();
        _vm.DynamicOptions["video"]["language"].Should().BeEmpty("опции должны быть очищены");
    }

    /// <summary>
    /// Проверяет, что файлы без MediaInfo пропускаются при сборе опций.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_FileWithoutMediaInfo_IsSkipped()
    {
        // Arrange
        var files = new ObservableCollection<FileQueueItem> { new FileQueueItem("C:\\media\\x.mkv") };
        _vm.Files = files;

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.DynamicOptions.Values.SelectMany(c => c.Values).All(s => s.Count == 0)
            .Should().BeTrue("все наборы опций должны остаться пустыми");
    }

    /// <summary>
    /// Проверяет очистку правил одной категории.
    /// </summary>
    [TestMethod]
    public void ClearRules_SpecificCategory_ClearsOnlyThatCategory()
    {
        // Arrange
        _vm.ActiveRules["video"]["language"].Add("RUS");
        _vm.ActiveRules["audio"]["codec"].Add("AC3");

        // Act
        _vm.ClearRules("video");

        // Assert
        _vm.ActiveRules["video"]["language"].Should().BeEmpty();
        _vm.ActiveRules["audio"]["codec"].Should().Contain("AC3", "другие категории не затрагиваются");
    }

    /// <summary>
    /// Проверяет очистку всех правил всех категорий.
    /// </summary>
    [TestMethod]
    public void ClearAllRules_MultipleActiveRules_ClearsEverything()
    {
        // Arrange
        _vm.ActiveRules["video"]["language"].Add("RUS");
        _vm.ActiveRules["audio"]["codec"].Add("AC3");
        _vm.ActiveRules["subtitles"]["name"].Add("Субтитры");
        _vm.ActiveRules["attachments"]["extension"].Add(".ttf");

        // Act
        _vm.ClearAllRules();

        // Assert
        _vm.ActiveRules.Values.SelectMany(c => c.Values).Should().AllBeEquivalentTo(new HashSet<string>());
    }

    /// <summary>
    /// Проверяет, что PruneObsoleteRules удаляет правила, отсутствующие в новых опциях.
    /// </summary>
    [TestMethod]
    public void PruneObsoleteRules_StaleRuleValue_RemovedFromActiveRules()
    {
        // Arrange — активное правило "FRE", которого нет в текущих опциях
        _vm.DynamicOptions["audio"]["language"].Add("RUS");
        _vm.ActiveRules["audio"]["language"].Add("RUS");
        _vm.ActiveRules["audio"]["language"].Add("FRE");

        // Act
        _vm.PruneObsoleteRules();

        // Assert
        _vm.ActiveRules["audio"]["language"].Should().Contain("RUS");
        _vm.ActiveRules["audio"]["language"].Should().NotContain("FRE", "устаревшее правило должно быть удалено");
    }

    /// <summary>
    /// Проверяет, что PruneObsoleteRules очищает правила категории без опций.
    /// </summary>
    [TestMethod]
    public void PruneObsoleteRules_EmptyOptionSet_ClearsRules()
    {
        // Arrange
        _vm.ActiveRules["video"]["resolution"].Add("3840x2160");
        // DynamicOptions["video"]["resolution"] пуст (файлов нет)

        // Act
        _vm.PruneObsoleteRules();

        // Assert
        _vm.ActiveRules["video"]["resolution"].Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что CollectDynamicOptions автоматически удаляет устаревшие правила.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_AfterRuleBecomesObsolete_RulePruned()
    {
        // Arrange — активное правило по языку, который отсутствует в файле
        var files = new ObservableCollection<FileQueueItem> { CreateFileWithStructure() };
        _vm.Files = files;
        _vm.ActiveRules["video"]["language"].Add("JPN");

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _vm.ActiveRules["video"]["language"].Should().NotContain("JPN",
            "правила должны автоматически пруниться при смене набора файлов");
    }

    /// <summary>
    /// Проверяет сопоставление видеодорожки активному правилу языка.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_VideoTrackWithActiveLanguageRule_ReturnsTrue()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", Language = "rus" };
        var item = new TrackNodeItem { Track = track, FilePath = "C:\\media\\film.mkv" };
        _vm.ActiveRules["video"]["language"].Add("RUS");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что дорожка, не соответствующая активному правилу, отфильтровывается.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_VideoTrackNonMatchingRule_ReturnsFalse()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "video", Language = "eng" };
        var item = new TrackNodeItem { Track = track };
        _vm.ActiveRules["video"]["language"].Add("RUS");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что при отсутствии активных правил узел не соответствует фильтру.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_NoActiveRules_ReturnsFalse()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "audio", Language = "rus" };
        var item = new TrackNodeItem { Track = track };

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeFalse("без активных правил фильтр ничего не выбирает");
    }

    /// <summary>
    /// Проверяет сопоставление вложения-шрифта правилу по расширению.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_FontAttachmentWithExtensionRule_ReturnsTrue()
    {
        // Arrange
        var attachment = new MediaAttachment { FileName = "ArialBold.ttf" };
        var item = new TrackNodeItem { IsFont = true, Attachment = attachment };
        _vm.ActiveRules["attachments"]["extension"].Add(".ttf");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что шрифт без Attachment не соответствует фильтру.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_FontNodeWithoutAttachment_ReturnsFalse()
    {
        // Arrange
        var item = new TrackNodeItem { IsFont = true, Attachment = null };
        _vm.ActiveRules["attachments"]["extension"].Add(".ttf");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что узел без Track и без IsFont не проходит фильтр.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_NodeWithoutTrackOrAttachment_ReturnsFalse()
    {
        // Arrange — узел типа "файл": не шрифт и без дорожки
        var item = new TrackNodeItem { IsFile = true };

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что правило по имени дорожки матчится для аудио.
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_AudioTrackNameRule_MatchesByTrackName()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "audio", Name = "Дубляж [Line]" };
        var item = new TrackNodeItem { Track = track };
        _vm.ActiveRules["audio"]["name"].Add("Дубляж [Line]");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что правило по каналам матчится в формате "N ch".
    /// </summary>
    [TestMethod]
    public void MatchesFilterRules_AudioChannelsRule_MatchesFormattedChannels()
    {
        // Arrange
        var track = new MediaTrack { TrackType = "audio", Channels = 6 };
        var item = new TrackNodeItem { Track = track };
        _vm.ActiveRules["audio"]["channels"].Add("6 ch");

        // Act
        bool matches = _vm.MatchesFilterRules(item);

        // Assert
        matches.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что CollectDynamicOptions пишет Info-лог об успешном сборе.
    /// </summary>
    [TestMethod]
    public void CollectDynamicOptions_Success_LogsInfoMessage()
    {
        // Arrange
        _vm.Files = new ObservableCollection<FileQueueItem>();

        // Act
        _vm.CollectDynamicOptions();

        // Assert
        _logServiceMock.Verify(
            l => l.Info(It.Is<string>(s => s.Contains("Сбор уникальных свойств")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет значения по умолчанию VideoCount/AudioCount/SubtitleCount/AttachmentCount.
    /// </summary>
    [TestMethod]
    public void ViewModel_DefaultCounts_AreZero()
    {
        // Assert
        _vm.VideoCount.Should().Be(0);
        _vm.AudioCount.Should().Be(0);
        _vm.SubtitleCount.Should().Be(0);
        _vm.AttachmentCount.Should().Be(0);
        _vm.ActiveScript.Should().BeNull();
        _vm.Files.Should().BeNull();
        _vm.SettingsManager.Should().BeSameAs(_settingsManagerMock.Object);
    }
}
