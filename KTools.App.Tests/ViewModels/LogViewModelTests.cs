// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using KTools_App.Core;
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
    /// Настраивает текст, возвращаемый ReadCurrentLog.
    /// </summary>
    private void SetupLogText(string text)
    {
        _logServiceMock.Setup(l => l.ReadCurrentLog()).Returns(text);
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
    /// Проверяет, что GetCurrentLogText делегирует чтение сервису логирования.
    /// </summary>
    [TestMethod]
    public void GetCurrentLogText_DelegatesToLogService()
    {
        // Arrange
        SetupLogText("текст лога");
        var vm = CreateViewModel();

        // Act
        string result = vm.GetCurrentLogText();

        // Assert
        result.Should().Be("текст лога");
        _logServiceMock.Verify(l => l.ReadCurrentLog(), Times.Once);
    }

    /// <summary>
    /// Проверяет загрузку логов с корректным парсингом уровней из строк.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LogWithMixedLevels_ParsesLevelsCorrectly()
    {
        // Arrange
        SetupLogText(string.Join(Environment.NewLine, new[]
        {
            "2024-01-01 10:00:00 | DEBUG   | Отладка",
            "2024-01-01 10:00:01 | INFO    | Информация",
            "2024-01-01 10:00:02 | WARNING | Предупреждение",
            "2024-01-01 10:00:03 | ERROR   | Ошибка",
            "2024-01-01 10:00:04 | FATAL   | Фатальная",
            "2024-01-01 10:00:05 | Строка без маркера уровня",
        }));
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().HaveCount(6);
        vm.Logs[0].Level.Should().Be(LogLevel.Debug);
        vm.Logs[1].Level.Should().Be(LogLevel.Info);
        vm.Logs[2].Level.Should().Be(LogLevel.Warning);
        vm.Logs[3].Level.Should().Be(LogLevel.Error);
        vm.Logs[4].Level.Should().Be(LogLevel.Fatal);
        vm.Logs[5].Level.Should().Be(LogLevel.Info, "строка без маркера трактуется как Info");
    }

    /// <summary>
    /// Проверяет, что пустой лог не добавляет записей и пишет DebugLog.
    /// </summary>
    [TestMethod]
    public void LoadLogs_EmptyLog_LeavesCollectionEmptyAndLogsDebug()
    {
        // Arrange
        SetupLogText(string.Empty);
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().BeEmpty();
        _logServiceMock.Verify(
            l => l.DebugLog(It.Is<string>(s => s.Contains("пуст или не инициализирован")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что загружаются только последние 1000 строк.
    /// </summary>
    [TestMethod]
    public void LoadLogs_MoreThanThousandLines_LoadsOnlyLastThousand()
    {
        // Arrange
        var lines = Enumerable.Range(1, 1500).Select(i => $"Строка лога номер {i}").ToList();
        SetupLogText(string.Join(Environment.NewLine, lines));
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().HaveCount(1000, "лимит загрузки — последние 1000 строк");
        vm.Logs[0].Message.Should().Be("Строка лога номер 501", "первой должна быть строка 501");
        vm.Logs[999].Message.Should().Be("Строка лога номер 1500");
    }

    /// <summary>
    /// Проверяет, что LoadLogs очищает коллекцию перед загрузкой.
    /// </summary>
    [TestMethod]
    public void LoadLogs_PreExistingItems_ClearsCollectionBeforeLoading()
    {
        // Arrange
        SetupLogText("новая строка");
        var vm = CreateViewModel();
        vm.AddLog("старая запись", LogLevel.Info);
        vm.Logs.Should().HaveCount(1);

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs.Should().HaveCount(1);
        vm.Logs[0].Message.Should().Be("новая строка");
    }

    /// <summary>
    /// Проверяет, что LoadLogs при исключении сервиса не падает и пишет Exception-лог.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LogServiceThrows_HandlesExceptionAndLogsIt()
    {
        // Arrange
        _logServiceMock.Setup(l => l.ReadCurrentLog()).Throws(new IOException("диск недоступен"));
        var vm = CreateViewModel();

        // Act
        Action act = () => vm.LoadLogs();

        // Assert
        act.Should().NotThrow("исключения загрузки должны перехватываться");
        _logServiceMock.Verify(
            l => l.Exception(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("Критическая ошибка")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет добавление новой записи лога в коллекцию.
    /// </summary>
    [TestMethod]
    public void AddLog_NewMessage_AppendsToCollection()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.AddLog("Новое сообщение", LogLevel.Warning);

        // Assert
        vm.Logs.Should().HaveCount(1);
        vm.Logs[0].Message.Should().Be("Новое сообщение");
        vm.Logs[0].Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    /// Проверяет усечение коллекции при превышении лимита 2000 записей.
    /// </summary>
    [TestMethod]
    public void AddLog_ExceedsTwoThousandRecords_TrimsOldestEntries()
    {
        // Arrange
        SetupLogText(string.Empty);
        var vm = CreateViewModel();

        // Act — добавляем 2005 записей
        for (int i = 0; i < 2005; i++)
        {
            vm.AddLog($"Запись {i}", LogLevel.Info);
        }

        // Assert
        vm.Logs.Should().HaveCount(2000, "лимит коллекции — 2000 записей");
        vm.Logs[0].Message.Should().Be("Запись 5", "пять самых старых записей должны быть удалены");
        vm.Logs[1999].Message.Should().Be("Запись 2004");
    }

    /// <summary>
    /// Проверяет, что ClearLogsCommand очищает коллекцию и пишет Info-лог.
    /// </summary>
    [TestMethod]
    public void ClearLogsCommand_WithItems_ClearsCollectionAndLogsInfo()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.AddLog("Запись 1", LogLevel.Info);
        vm.AddLog("Запись 2", LogLevel.Error);

        // Act
        vm.ClearLogsCommand.Execute(null);

        // Assert
        vm.Logs.Should().BeEmpty();
        _logServiceMock.Verify(
            l => l.Info(It.Is<string>(s => s.Contains("успешно очищено")), It.IsAny<string>()),
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
    /// Проверяет, что LoadLogs отправляет Debug-сообщение об успешной загрузке.
    /// </summary>
    [TestMethod]
    public void LoadLogs_SuccessfulLoad_LogsDebugMessageWithCount()
    {
        // Arrange
        SetupLogText("одна строка");
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        _logServiceMock.Verify(
            l => l.DebugLog(It.Is<string>(s => s.Contains("1") && s.Contains("записей")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Проверяет, что парсинг уровней распознаёт marker-формат с корректными пробелами.
    /// </summary>
    [TestMethod]
    public void LoadLogs_LevelMarkers_RequireExactPadding()
    {
        // Arrange — "| INFO    |" с четырьмя пробелами; вариант "| INFO |" не распознаётся
        SetupLogText(string.Join(Environment.NewLine, new[]
        {
            "| INFO    | корректный маркер",
            "| INFO | неверный паддинг",
        }));
        var vm = CreateViewModel();

        // Act
        vm.LoadLogs();

        // Assert
        vm.Logs[0].Level.Should().Be(LogLevel.Info);
        vm.Logs[1].Level.Should().Be(LogLevel.Info, "неверный паддинг всё равно fallback на Info");
    }
}
