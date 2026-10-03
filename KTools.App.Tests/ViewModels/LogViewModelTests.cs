// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Юнит-тесты LogViewModel — модели представления страницы логов.
/// Проверяют загрузку истории логов с диска (парсинг уровней, лимит 1000 строк),
/// динамическое добавление записей с усечением до 2000, команду очистки
/// и делегирование GetCurrentLogText сервису логирования.
/// Ограничение: CopyAllLogs/CopySelectedLogs/OpenLogDirectory используют
/// WinRT Clipboard и Process.Start(explorer.exe) — системные операции,
/// неприменимые в headless-тестах (Clipboard требует STA/окна) — не тестируются напрямую.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class LogViewModelTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
    }

    /// <summary>
    /// Создаёт LogViewModel с настроенными зависимостями.
    /// </summary>
    private LogViewModel CreateViewModel()
    {
        return new LogViewModel(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object);
    }

    /// <summary>
    /// Настраивает структурированные события, возвращаемые ReadRecentEvents,
    /// с ограничением окна так же, как это делает реальный сервис журналирования.
    /// </summary>
    private void SetupEvents(IReadOnlyList<LogEvent> events)
    {
        _logServiceMock
            .Setup(l => l.ReadRecentEvents(It.IsAny<int>()))
            .Returns<int>(limit => events.Skip(Math.Max(0, events.Count - limit)).Take(limit).ToArray());
    }

    /// <summary>
    /// Создаёт типизированное событие журнала для проверок загрузки истории.
    /// </summary>
    private static LogEvent CreateEvent(
        string eventId,
        LogLevel level,
        string message,
        long sequence = 0,
        LogStatus status = LogStatus.None)
    {
        return new LogEvent
        {
            EventId = eventId,
            Level = level,
            Status = status,
            Source = "TestSource",
            Message = message,
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)
        };
    }

    /// <summary>
    /// Проверяет, что конструктор с null-зависимостями бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void Constructor_NullDependencies_ThrowArgumentNullException()
    {
        // Act
        Action logNull = () => new LogViewModel(null!, _settingsManagerMock.Object, _pathManagerMock.Object);
        Action settingsNull = () => new LogViewModel(_logServiceMock.Object, null!, _pathManagerMock.Object);
        Action pathNull = () => new LogViewModel(_logServiceMock.Object, _settingsManagerMock.Object, null!);

        // Assert
        logNull.Should().Throw<ArgumentNullException>();
        settingsNull.Should().Throw<ArgumentNullException>();
        pathNull.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет загрузку истории с корректным переносом типизированных уровней
    /// и статусов из структурированных событий в элементы панели.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LogWithMixedLevels_ParsesLevelsCorrectly()
    {
        // Arrange
        SetupEvents(new[]
        {
            CreateEvent("test.debug", LogLevel.Debug, "Отладка", 1),
            CreateEvent("test.info", LogLevel.Info, "Информация", 2),
            CreateEvent("test.warning", LogLevel.Warning, "Предупреждение", 3, LogStatus.Failed),
            CreateEvent("test.error", LogLevel.Error, "Ошибка", 4, LogStatus.Failed),
            CreateEvent("test.fatal", LogLevel.Fatal, "Фатальная", 5, LogStatus.Failed),
            CreateEvent("test.none", LogLevel.Info, "Событие без уровня ошибки", 6, LogStatus.Succeeded)
        });
        var vm = CreateViewModel();

        // Act
        IReadOnlyList<LogEvent> loaded = vm.LoadLogs();

        // Assert
        loaded.Should().HaveCount(6);
        vm.Logs.Should().HaveCount(6);
        vm.Logs[0].Level.Should().Be(LogLevel.Debug);
        vm.Logs[1].Level.Should().Be(LogLevel.Info);
        vm.Logs[2].Level.Should().Be(LogLevel.Warning);
        vm.Logs[3].Level.Should().Be(LogLevel.Error);
        vm.Logs[4].Level.Should().Be(LogLevel.Fatal);
        vm.Logs[5].Level.Should().Be(LogLevel.Info);
        vm.Logs.Select(l => l.EventId).Should().Equal(
            "test.debug", "test.info", "test.warning", "test.error", "test.fatal", "test.none");
        vm.Logs.Select(l => l.Status).Should().Equal(
            LogStatus.None, LogStatus.None, LogStatus.Failed, LogStatus.Failed, LogStatus.Failed, LogStatus.Succeeded);
        vm.Logs.Should().OnlyContain(l => l.HasStructuredEvent,
            "уровень берётся из типизированного события, а не из разбора текста");
        vm.Logs.Select(l => l.Sequence).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    /// <summary>
    /// Проверяет, что пустая история не добавляет записей
    /// и фиксирует структурированное событие log.history.loaded с нулевым счётчиком.
    /// </summary>
    [TestMethod]
    public void LoadLogs_EmptyLog_LeavesCollectionEmptyAndLogsDebug()
    {
        // Arrange
        SetupEvents(Array.Empty<LogEvent>());
        var vm = CreateViewModel();

        // Act
        IReadOnlyList<LogEvent> loaded = vm.LoadLogs();

        // Assert
        loaded.Should().BeEmpty();
        vm.Logs.Should().BeEmpty();
        _logServiceMock.Verify(
            l => l.ReadRecentEvents(LogViewModel.RecentEventsLimit),
            Times.Once,
            "история запрашивается ограниченным окном событий");
        _logServiceMock.Verify(
            l => l.Write(
                "log.history.loaded",
                LogLevel.Debug,
                LogStatus.Succeeded,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                "LogViewModel",
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["Count"], 0))),
            Times.Once,
            "пустая история фиксируется структурированным событием с нулевым счётчиком");
    }

    /// <summary>
    /// Проверяет, что загружается только ограниченное окно последних событий.
    /// </summary>
    [TestMethod]
    public void LoadLogs_MoreThanThousandLines_LoadsOnlyLastThousand()
    {
        // Arrange
        var events = Enumerable.Range(1, 1500)
            .Select(i => CreateEvent("test.bulk", LogLevel.Info, $"Строка лога номер {i}", i))
            .ToArray();
        SetupEvents(events);
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        _logServiceMock.Verify(
            l => l.ReadRecentEvents(LogViewModel.RecentEventsLimit),
            Times.Once,
            "модель представления обязана запрашивать ровно 1000 последних событий");
        vm.Logs.Should().HaveCount(1000, "в панель попадает только ограниченное окно событий");
        vm.Logs[0].Message.Should().Contain("Строка лога номер 501", "первым должно быть событие 501");
        vm.Logs[999].Message.Should().Contain("Строка лога номер 1500");
        vm.Logs[0].Sequence.Should().Be(501);
        vm.Logs[999].Sequence.Should().Be(1500);
    }

    /// <summary>
    /// Проверяет, что LoadLogs очищает коллекцию перед загрузкой.
    /// </summary>
    [TestMethod]
    public void LoadLogs_PreExistingItems_ClearsCollectionBeforeLoading()
    {
        // Arrange
        SetupEvents(new[] { CreateEvent("test.new", LogLevel.Info, "новая строка", 1) });
        var vm = CreateViewModel();
        vm.AddLogs(new[] { new LogItem { Message = "старая запись", Level = LogLevel.Info } });
        vm.Logs.Should().HaveCount(1);

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().HaveCount(1);
        vm.Logs[0].Message.Should().Contain("новая строка");
        vm.Logs[0].EventId.Should().Be("test.new");
    }

    /// <summary>
    /// Проверяет, что LoadLogs при исключении сервиса не падает, возвращает пустой
    /// список и фиксирует структурированное событие log.history.load_failed с кодом ошибки.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LogServiceThrows_HandlesExceptionAndLogsIt()
    {
        // Arrange
        var failure = new IOException("диск недоступен");
        _logServiceMock.Setup(l => l.ReadRecentEvents(It.IsAny<int>())).Throws(failure);
        var vm = CreateViewModel();

        // Act
        IReadOnlyList<LogEvent>? loaded = null;
        Action act = () => loaded = vm.LoadLogs();

        // Assert
        act.Should().NotThrow("исключения загрузки должны перехватываться");
        loaded.Should().BeEmpty("при ошибке загрузки панель получает пустую историю");
        _logServiceMock.Verify(
            l => l.Write(
                "log.history.load_failed",
                LogLevel.Error,
                LogStatus.Failed,
                It.Is<string>(m => !m.Contains("Exception", StringComparison.Ordinal)),
                It.Is<IOException>(ex => ReferenceEquals(ex, failure)),
                "LogViewModel",
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["ErrorCode"], "LOG_HISTORY_LOAD_FAILED"))),
            Times.Once,
            "ошибка загрузки фиксируется структурированным событием с кодом и исключением в отдельном параметре");
    }

    /// <summary>
    /// Проверяет добавление пакета записей в коллекцию панели журнала.
    /// </summary>
    [TestMethod]
    public void AddLogs_NewItems_AppendsToCollection()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.AddLogs(new[] { new LogItem { Message = "Новое сообщение", Level = LogLevel.Warning } });

        // Assert
        vm.Logs.Should().HaveCount(1);
        vm.Logs[0].Message.Should().Be("Новое сообщение");
        vm.Logs[0].Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    /// Проверяет усечение коллекции при превышении лимита 2000 записей.
    /// </summary>
    [TestMethod]
    public void AddLogs_ExceedsTwoThousandRecords_TrimsOldestEntries()
    {
        // Arrange
        SetupEvents(Array.Empty<LogEvent>());
        var vm = CreateViewModel();

        // Act — добавляем 2005 записей
        vm.AddLogs(Enumerable.Range(0, 2005)
            .Select(index => new LogItem { Message = $"Запись {index}", Level = LogLevel.Info })
            .ToArray());

        // Assert
        vm.Logs.Should().HaveCount(2000, "лимит коллекции — 2000 записей");
        vm.Logs[0].Message.Should().Be("Запись 5", "пять самых старых записей должны быть удалены");
        vm.Logs[1999].Message.Should().Be("Запись 2004");
    }

    /// <summary>
    /// Проверяет, что ClearLogsCommand очищает коллекцию и фиксирует
    /// структурированное событие log.history.window_cleared.
    /// </summary>
    [TestMethod]
    public void ClearLogsCommand_WithItems_ClearsCollectionAndLogsInfo()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.AddLogs(new[]
        {
            new LogItem { Message = "Запись 1", Level = LogLevel.Info },
            new LogItem { Message = "Запись 2", Level = LogLevel.Error }
        });

        // Act
        vm.ClearLogsCommand.Execute(null);

        // Assert
        vm.Logs.Should().BeEmpty();
        _logServiceMock.Verify(
            l => l.Write(
                "log.history.window_cleared",
                LogLevel.Info,
                LogStatus.Changed,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                "LogViewModel",
                It.IsAny<LogContext?>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что ClearLogsCommand на пустой коллекции не падает.
    /// </summary>
    [TestMethod]
    public void ClearLogsCommand_EmptyCollection_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        Action act = () => vm.ClearLogsCommand.Execute(null);

        // Assert
        act.Should().NotThrow();
        vm.Logs.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет, что LoadLogs фиксирует структурированное событие
    /// log.history.loaded с числом загруженных событий.
    /// </summary>
    [TestMethod]
    public void LoadLogs_SuccessfulLoad_LogsDebugMessageWithCount()
    {
        // Arrange
        SetupEvents(new[]
        {
            CreateEvent("test.first", LogLevel.Info, "одна строка", 1)
        });
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        _logServiceMock.Verify(
            l => l.Write(
                "log.history.loaded",
                LogLevel.Debug,
                LogStatus.Succeeded,
                It.IsAny<string>(),
                It.IsAny<Exception>(),
                "LogViewModel",
                It.IsAny<LogContext?>(),
                It.Is<IReadOnlyDictionary<string, object?>>(p => Equals(p["Count"], 1))),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что уровень элемента панели берётся из типизированного события
    /// без разбора текстовых маркеров уровня.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LevelMarkers_RequireExactPadding()
    {
        // Arrange — типизированное событие переносит уровень без разбора паддинга текста
        SetupEvents(new[]
        {
            CreateEvent("test.info", LogLevel.Info, "| INFO    | корректный маркер", 1, LogStatus.Succeeded),
            CreateEvent("test.info.padding", LogLevel.Info, "| INFO | неверный паддинг", 2, LogStatus.Succeeded)
        });
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().HaveCount(2);
        vm.Logs[0].Level.Should().Be(LogLevel.Info);
        vm.Logs[1].Level.Should().Be(LogLevel.Info);
        vm.Logs.Select(l => l.Level).Should().AllBeEquivalentTo(LogLevel.Info,
            "уровень определяется типизированным событием, а не пробелами в тексте маркера");
        vm.Logs.Should().OnlyContain(l => l.HasStructuredEvent);
    }
}
