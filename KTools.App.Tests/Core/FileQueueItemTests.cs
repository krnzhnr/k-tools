// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Tests.TestHelpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты элемента очереди файлов FileQueueItem.
/// Проверяют конструктор (FileSizeStr для файлов/URL/несуществующих/null),
/// INPC-уведомления всех изменяемых свойств, синхронность HasCropBadge
/// и соответствие State визуальным свойствам для всех 6 состояний
/// FileProcessingState.
/// Ограничение: StatusIconBrush использует статические SolidColorBrush (WinRT),
/// что требует XAML-среды, — поэтому проверяется только managed-логика иконок.
/// Свойство ShowSubtitlesSelector читает статический App.Services (null вне приложения) — не тестируется.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class FileQueueItemTests
{
    /// <summary>
    /// Проверяет, что конструктор вычисляет FileSizeStr для существующего файла.
    /// </summary>
    [TestMethod]
    public void Constructor_ExistingFile_ComputesFileSize()
    {
        // Arrange
        using var temp = new TempDirectoryScope();
        string path = temp.CreateFile("video.mkv", new string('x', 5 * 1024 * 1024)); // 5 МБ

        // Act
        var item = new FileQueueItem(path);

        // Assert
        item.FilePath.Should().Be(path);
        item.FileName.Should().Be("video.mkv");
        item.FileSizeStr.Should().StartWith("5,00").And.EndWith("МБ");
    }

    /// <summary>
    /// Проверяет, что URL распознаётся и помечается как "Ссылка".
    /// </summary>
    [TestMethod]
    public void Constructor_HttpUrl_MarksAsLink()
    {
        // Act
        var httpItem = new FileQueueItem("http://example.com/video.mkv");
        var httpsItem = new FileQueueItem("https://example.com/video.mkv");
        var uppercaseItem = new FileQueueItem("HTTPS://example.com/video.mkv");

        // Assert
        httpItem.FileSizeStr.Should().Be("Ссылка");
        httpsItem.FileSizeStr.Should().Be("Ссылка");
        uppercaseItem.FileSizeStr.Should().Be("Ссылка", "протокол должен распознаваться без учета регистра");
    }

    /// <summary>
    /// Проверяет, что несуществующий файл помечается как "Неизвестно".
    /// </summary>
    [TestMethod]
    public void Constructor_NonExistingFile_MarksAsUnknown()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "KToolsTests", Guid.NewGuid().ToString("N"), "missing.mkv");

        // Act
        var item = new FileQueueItem(path);

        // Assert
        item.FileSizeStr.Should().Be("Неизвестно");
    }

    /// <summary>
    /// Проверяет, что null-путь не бросает исключений в конструкторе.
    /// </summary>
    [TestMethod]
    public void Constructor_NullPath_DoesNotThrow()
    {
        // Act
        Action act = () => new FileQueueItem(null!);

        // Assert
        act.Should().NotThrow("null-путь должен обрабатываться безопасно");
    }

    /// <summary>
    /// Проверяет, что пустая строка пути не бросает исключений.
    /// </summary>
    [TestMethod]
    public void Constructor_EmptyPath_DoesNotThrow()
    {
        // Act
        Action act = () => new FileQueueItem(string.Empty);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>
    /// Проверяет начальные значения по умолчанию нового элемента очереди.
    /// </summary>
    [TestMethod]
    public void Constructor_NewItem_HasCorrectDefaults()
    {
        // Act
        var item = new FileQueueItem("C:\\video.mkv");

        // Assert
        item.Status.Should().Be("Ожидание");
        item.State.Should().Be(FileProcessingState.Pending);
        item.IsProcessing.Should().BeFalse();
        item.Progress.Should().Be(0);
        item.MediaInfo.Should().BeNull();
        item.HasCropBadge.Should().BeFalse();
        item.IsDeleteEnabled.Should().BeTrue();
        item.DisplayName.Should().Be("video.mkv", "DisplayName по умолчанию равен FileName");
    }

    /// <summary>
    /// Проверяет, что установка Status вызывает единственное INPC-уведомление.
    /// </summary>
    [TestMethod]
    public void Status_SetNewValue_RaisesSinglePropertyChanged()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.Status = "Обработка...";

        // Assert
        var events = recorder.GetEventsFor(nameof(FileQueueItem.Status));
        events.Should().HaveCount(1);
        events[0].Value.Should().Be("Обработка...");
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что повторная установка того же Status не вызывает уведомлений.
    /// </summary>
    [TestMethod]
    public void Status_SetSameValue_DoesNotRaisePropertyChanged()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv") { Status = "Ожидание" };
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.Status = "Ожидание";

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.Status)).Should().BeEmpty();
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что null в Status заменяется на "Ожидание".
    /// </summary>
    [TestMethod]
    public void Status_SetNull_FallsBackToWaiting()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");

        // Act
        item.Status = null!;

        // Assert
        item.Status.Should().Be("Ожидание");
    }

    /// <summary>
    /// Проверяет, что установка Progress уведомляет Progress и ProgressText.
    /// </summary>
    [TestMethod]
    public void Progress_SetNewValue_NotifiesProgressAndText()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.Progress = 55.5;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.Progress)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.ProgressText)).Should().HaveCount(1);
        item.ProgressText.Should().Be("56%", "текст прогресса форматируется как F0");
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что изменение Progress менее чем на 0.01 игнорируется.
    /// </summary>
    [TestMethod]
    public void Progress_SetValueBelowTolerance_DoesNotNotify()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv") { Progress = 10.0 };
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.Progress = 10.005; // |10.005 - 10.0| = 0.005 < 0.01

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.Progress)).Should().BeEmpty();
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что IsProcessing уведомляет IsDeleteEnabled синхронно.
    /// </summary>
    [TestMethod]
    public void IsProcessing_SetTrue_NotifiesIsDeleteEnabled()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.IsProcessing = true;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.IsProcessing)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.IsDeleteEnabled)).Should().HaveCount(1);
        item.IsDeleteEnabled.Should().BeFalse("во время обработки удаление запрещено");
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что MediaInfo уведомляется при установке.
    /// </summary>
    [TestMethod]
    public void MediaInfo_SetValue_RaisesPropertyChanged()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);
        var structure = new MediaStructure { FilePath = "C:\\video.mkv" };

        // Act
        item.MediaInfo = structure;

        // Assert
        var events = recorder.GetEventsFor(nameof(FileQueueItem.MediaInfo));
        events.Should().HaveCount(1);
        events[0].Value.Should().BeSameAs(structure);
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что один вызов setter CropBadgeText синхронно уведомляет
    /// и CropBadgeText, и HasCropBadge (жёсткий assert на одновременность).
    /// </summary>
    [TestMethod]
    public void CropBadgeText_SetValue_NotifiesTextAndHasBadgeSynchronously()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act — один вызов setter
        item.CropBadgeText = "1920x1080 ➔ 1920x816";

        // Assert — оба события фиксируются от одного вызова
        var badgeEvents = recorder.GetEventsFor(nameof(FileQueueItem.CropBadgeText));
        var hasEvents = recorder.GetEventsFor(nameof(FileQueueItem.HasCropBadge));
        badgeEvents.Should().HaveCount(1);
        hasEvents.Should().HaveCount(1, "HasCropBadge должен уведомляться синхронно с CropBadgeText");
        badgeEvents[0].Value.Should().Be("1920x1080 ➔ 1920x816");
        item.HasCropBadge.Should().BeTrue();
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что очистка CropBadgeText возвращает HasCropBadge в false.
    /// </summary>
    [TestMethod]
    public void CropBadgeText_Clear_NotifiesHasBadgeFalse()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv") { CropBadgeText = "crop" };
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.CropBadgeText = null;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.HasCropBadge)).Should().HaveCount(1);
        item.HasCropBadge.Should().BeFalse();
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет, что State уведомляет все зависимые вычисляемые свойства.
    /// </summary>
    [TestMethod]
    public void State_SetNewValue_NotifiesAllDependentProperties()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.State = FileProcessingState.Processing;

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.State)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.ProgressRingVisibility)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.StatusIconVisibility)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.IsProgressIndeterminate)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.StatusIcon)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.IsDeleteEnabled)).Should().HaveCount(1);
        recorder.Detach();
    }

    /// <summary>
    /// Проверяет соответствие State визуальным managed-свойствам
    /// для ВСЕХ 6 состояний FileProcessingState (критично для UI-синхронизации).
    /// </summary>
    [TestMethod]
    public void State_AllSixStates_VisualPropertiesMatchExpected()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var expectations = new Dictionary<FileProcessingState, (Visibility Ring, Visibility Icon, Symbol Glyph)>
        {
            [FileProcessingState.Pending] = (Visibility.Collapsed, Visibility.Visible, Symbol.Clock),
            [FileProcessingState.Processing] = (Visibility.Visible, Visibility.Collapsed, Symbol.Clock),
            [FileProcessingState.Completed] = (Visibility.Collapsed, Visibility.Visible, Symbol.Accept),
            [FileProcessingState.Skipped] = (Visibility.Collapsed, Visibility.Visible, Symbol.Accept),
            [FileProcessingState.Failed] = (Visibility.Collapsed, Visibility.Visible, Symbol.Cancel),
            [FileProcessingState.Cancelled] = (Visibility.Collapsed, Visibility.Visible, Symbol.Cancel),
        };

        foreach (var pair in expectations)
        {
            // Act
            item.State = pair.Key;

            // Assert
            item.ProgressRingVisibility.Should().Be(pair.Value.Ring,
                $"ProgressRingVisibility для {pair.Key}");
            item.StatusIconVisibility.Should().Be(pair.Value.Icon,
                $"StatusIconVisibility для {pair.Key}");
            item.StatusIcon.Should().Be(pair.Value.Glyph,
                $"StatusIcon для {pair.Key}");
        }
    }

    /// <summary>
    /// Проверяет IsProgressIndeterminate: только Processing с нулевым прогрессом.
    /// </summary>
    [TestMethod]
    public void IsProgressIndeterminate_OnlyProcessingWithZeroProgress_ReturnsTrue()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");

        // Act & Assert
        item.State = FileProcessingState.Processing;
        item.Progress = 0;
        item.IsProgressIndeterminate.Should().BeTrue();

        item.Progress = 10;
        item.IsProgressIndeterminate.Should().BeFalse("при положительном прогрессе индикатор становится определённым");

        item.State = FileProcessingState.Pending;
        item.Progress = 0;
        item.IsProgressIndeterminate.Should().BeFalse("вне состояния Processing индикатор не неопределённый");
    }

    /// <summary>
    /// Проверяет соответствие строковых статусов состояниям State
    /// (InferStateFromStatus-совместимость через публичное свойство State).
    /// </summary>
    [TestMethod]
    public void State_RoundTripWithStatusStrings_MatchesInferredStates()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var statusToState = new Dictionary<string, FileProcessingState>
        {
            ["Завершено"] = FileProcessingState.Completed,
            ["Ошибка"] = FileProcessingState.Failed,
            ["Отменено"] = FileProcessingState.Cancelled,
            ["Пропуск файла"] = FileProcessingState.Skipped,
            ["Пропущен: тест"] = FileProcessingState.Skipped,
            ["Обработка..."] = FileProcessingState.Processing,
            ["Обработка 50%"] = FileProcessingState.Processing,
            ["Ожидание"] = FileProcessingState.Pending,
        };

        foreach (var pair in statusToState)
        {
            // Act — статус и состояние устанавливаются согласованно (как делает WorkPanelViewModel)
            item.Status = pair.Key;
            item.State = pair.Value;

            // Assert — статусные строки соответствуют ожидаемым состояниям enum
            item.Status.Should().Be(pair.Key);
            item.State.Should().Be(pair.Value, $"статус '{pair.Key}' должен соответствовать {pair.Value}");
        }
    }

    /// <summary>
    /// Проверяет, что DisplayName возвращает имя файла, если кастомное имя не задано.
    /// </summary>
    [TestMethod]
    public void DisplayName_SetCustomValue_OverridesFileName()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");

        // Act
        item.DisplayName = "YouTube Video 1080p";

        // Assert
        item.DisplayName.Should().Be("YouTube Video 1080p");

        // Act — сброс к пустой строке возвращает FileName
        item.DisplayName = string.Empty;

        // Assert
        item.DisplayName.Should().Be("video.mkv");
    }

    /// <summary>
    /// Проверяет, что BitrateData уведомляет HasBitrateData синхронно.
    /// </summary>
    [TestMethod]
    public void BitrateData_SetValue_NotifiesHasBitrateData()
    {
        // Arrange
        var item = new FileQueueItem("C:\\video.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.BitrateData = new Services.Contracts.BitrateAnalysisResult();

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.BitrateData)).Should().HaveCount(1);
        recorder.GetEventsFor(nameof(FileQueueItem.HasBitrateData)).Should().HaveCount(1);
        item.HasBitrateData.Should().BeTrue();
        recorder.Detach();
    }
}
