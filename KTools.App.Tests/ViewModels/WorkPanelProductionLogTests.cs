// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Проверки production-пути WorkPanel: реальный LogService вместо тестового двойника,
/// отклонения по условной схеме, корректность терминальных событий элемента и очереди,
/// граница отказа запуска и агрегация итогов очереди.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WorkPanelProductionLogTests : IsolatedMessengerTestBase
{
    private const string SecretMarker = "SENTINEL-SECRET-PAYLOAD";
    private const string AbsolutePathMarker = @"C:\private\customer\crash.mkv";

    /// <summary>
    /// Идентификатор сбоя запуска очереди задан в требованиях дословно и сохраняется как есть,
    /// чтобы проверка D6 не расходилась с явным контрактом.
    /// </summary>
    internal const string QueueStartFailedEventId = "exec.queue.start_failed";

    private MockBuildersProxy _mocks = null!;

    [TestInitialize]
    public void Setup()
    {
        _mocks = new MockBuildersProxy();
    }

    [TestMethod]
    public async Task ItemFailure_RealLogService_RejectsNothingAndKeepsTerminalEvent()
    {
        // Arrange
        using TempDirectoryScope scope = new();
        using LogService log = CreateLogService(scope);
        var failure = new InvalidOperationException(SecretMarker);

        StubScript script = CreateScript((file, settings, context) =>
        {
            if (file.Contains("crash", StringComparison.OrdinalIgnoreCase))
            {
                throw failure;
            }

            return Task.FromResult(ExecutionResult.Succeeded(context, "Готово"));
        });

        WorkPanelViewModel vm = CreateViewModel(log);
        ObservableCollection<FileQueueItem> files = CreateFiles(AbsolutePathMarker);
        vm.Initialize(script, files);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        log.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        log.RejectedEventCount.Should().Be(0,
            "production-путь не должен порождать события, отклонённые условной схемой журнала");
        log.Status.HasLoss.Should().BeFalse("потерь и отказов записи на успешном прогоне быть не должно");

        string[] lines = ReadJsonl(log);
        lines.Should().NotContain(line => line.Contains(LogEventMarkerNames.RejectedEvent, StringComparison.Ordinal),
            "отклонённые события не должны попадать в журнал как отдельные записи");

        LogEvent[] persisted = lines.Select(ParseEvent).ToArray();
        persisted.Should().Contain(e => e.EventId == "exec.item.exception",
            "исключение обработки элемента фиксируется отдельным структурированным событием");
        persisted.Should().Contain(e => e.EventId == "exec.queue.ended");

        LogEvent terminal = persisted.Single(e => e.EventId == "exec.item.failed");
        terminal.Status.Should().Be(LogStatus.Failed);
        terminal.ErrorCode.Should().Be("execution-exception");
        terminal.Exception.Should().NotBeNull("терминальное событие несёт реальное исключение");
        terminal.OperationId.Should().NotBeNullOrWhiteSpace();
        terminal.ItemId.Should().NotBeNullOrWhiteSpace();
        terminal.Message.Should().Contain("Элемент очереди завершён");

        LogEvent exceptionEvent = persisted.Single(e => e.EventId == "exec.item.exception");
        exceptionEvent.OperationId.Should().Be(terminal.OperationId,
            "событие исключения и терминальное событие обязаны быть коррелированы одной операцией");
        exceptionEvent.ItemId.Should().Be(terminal.ItemId);

        string payload = string.Join('\n', lines);
        persisted.Should().NotContain(
            e => e.Message.Contains(SecretMarker, StringComparison.Ordinal),
            "текст исключения не должен дублироваться в тексте сообщения");
        payload.Should().NotContain("C:\\private", "полный путь к файлу не должен попадать в журнал");
        payload.Should().NotContain("customer", "журнал не должен раскрывать клиентские каталоги");
        payload.Should().NotContain("stub:", "журнал не должен раскрывать исполняемый файл теста");
    }

    [TestMethod]
    public async Task ItemStarted_RecordsOnlyRunningContextWithoutTerminalObservations()
    {
        // Arrange
        RecordingLogService log = new();
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово")));

        WorkPanelViewModel vm = CreateViewModel(log);
        vm.Initialize(script, CreateFiles("C:\\media\\a.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        log.RejectedEventCount.Should().Be(0);
        RecordedLogEvent started = log.EventsById("exec.item.started").Should().ContainSingle().Subject;
        started.Status.Should().Be(LogStatus.Running);
        started.GetProperty<string>("OperationId").Should().NotBeNullOrWhiteSpace();
        started.GetProperty<string>("ItemId").Should().NotBeNullOrWhiteSpace();
        started.GetProperty<string>("FileName").Should().Be("a.mkv");

        foreach (string terminalKey in new[]
        {
            "Status", "OutputExists", "Retryable", "CleanupState", "MessageCount", "DurationMs", "ErrorCode", "ExitCode"
        })
        {
            started.Properties.Should().NotContainKey(terminalKey,
                $"событие запуска не должно содержать терминальное наблюдение '{terminalKey}'");
        }
    }

    [TestMethod]
    public async Task QueueStartFailure_PreflightThrows_ReportsStartFailedWithoutStuckState()
    {
        // Arrange
        RecordingLogService log = new();
        var failure = new InvalidOperationException("сбой подготовки очереди");

        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово")));
        script.ProcessableFilesFilter = _ => throw failure;

        WorkPanelViewModel vm = CreateViewModel(log);
        ObservableCollection<FileQueueItem> files = CreateFiles("C:\\media\\a.mkv", "C:\\media\\b.mkv");
        vm.Initialize(script, files);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        log.RejectedEventCount.Should().Be(0);

        RecordedLogEvent startFailed = log.EventsById(QueueStartFailedEventId).Should().ContainSingle().Subject;
        startFailed.Level.Should().Be(LogLevel.Error);
        startFailed.Status.Should().Be(LogStatus.Failed);
        startFailed.Exception.Should().BeSameAs(failure);
        startFailed.GetProperty<string>("OperationId").Should().NotBeNullOrWhiteSpace();
        startFailed.GetProperty<string>("ErrorCode").Should().Be("QUEUE_START_FAILED");

        log.EventsById("exec.item.failed").Should().HaveCount(2,
            "все элементы очереди обязаны получить терминальный результат");
        foreach (ExecutionResult itemResult in vm.TypedItemResults)
        {
            itemResult.Status.Should().Be(ExecutionStatus.Failed);
            itemResult.ErrorCode.Should().Be("queue-start-failed");
            itemResult.CleanupState.Should().Be(CleanupState.NotStarted,
                "очистка не запускалась, поэтому состояние не должно выдаваться за выполненную");
        }

        RecordedLogEvent ended = log.EventsById("exec.queue.ended").Should().ContainSingle().Subject;
        ended.Level.Should().Be(LogLevel.Error);
        ended.Status.Should().Be(LogStatus.Failed);
        ended.GetProperty<int>("Total").Should().Be(2);
        ended.GetProperty<int>("Failed").Should().Be(2);

        vm.IsProcessing.Should().BeFalse("после сбоя запуска панель не должна оставаться в состоянии обработки");
        script.IsProcessing.Should().BeFalse();
        files.Should().OnlyContain(item => !item.IsProcessing);
        vm.LastQueueResult.Should().NotBeNull();
        vm.LastQueueResult!.Status.Should().Be(ExecutionStatus.Failed);
    }

    [TestMethod]
    public async Task Aggregate_ThreeSucceededAndOneCancelled_IsPartiallySucceededWithCounts()
    {
        // Arrange
        RecordingLogService log = new();
        StubScript? captured = null;
        captured = CreateScript((file, settings, context) =>
        {
            if (file.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            {
                captured!.Cancel();
            }

            return Task.FromResult(ExecutionResult.Succeeded(context, "Готово"));
        });

        WorkPanelViewModel vm = CreateViewModel(log);
        vm.Initialize(captured, CreateFiles(
            "C:\\media\\a.mkv", "C:\\media\\b.mkv", "C:\\media\\c.mkv", "C:\\media\\cancel.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.SucceededCount.Should().Be(3);
        vm.CancelledCount.Should().Be(1);
        vm.FailedCount.Should().Be(0);

        RecordedLogEvent ended = log.EventsById("exec.queue.ended").Should().ContainSingle().Subject;
        ended.Status.Should().Be(LogStatus.PartiallySucceeded,
            "часть элементов обработана, поэтому очередь частично успешна, а не отменена");
        ended.Level.Should().Be(LogLevel.Warning);
        ended.GetProperty<int>("Succeeded").Should().Be(3);
        ended.GetProperty<int>("Cancelled").Should().Be(1);
        ended.GetProperty<int>("Failed").Should().Be(0);
        vm.LastQueueResult!.Status.Should().Be(ExecutionStatus.PartiallySucceeded);
    }

    [TestMethod]
    public async Task Aggregate_AllCancelled_IsCancelledNotPartial()
    {
        // Arrange
        RecordingLogService log = new();
        StubScript? captured = null;
        captured = CreateScript((file, settings, context) =>
        {
            captured!.Cancel();
            return Task.FromResult(ExecutionResult.Succeeded(context, "Готово"));
        });

        WorkPanelViewModel vm = CreateViewModel(log);
        vm.Initialize(captured, CreateFiles("C:\\media\\a.mkv", "C:\\media\\b.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.CancelledCount.Should().Be(2);
        vm.SucceededCount.Should().Be(0);

        RecordedLogEvent ended = log.EventsById("exec.queue.ended").Should().ContainSingle().Subject;
        ended.Status.Should().Be(LogStatus.Cancelled);
        ended.GetProperty<int>("Cancelled").Should().Be(2);
        ended.GetProperty<string>("ErrorCode").Should().Be("queue-cancelled");
    }

    [TestMethod]
    public async Task Aggregate_AllSucceeded_IsSucceededWithoutErrorCode()
    {
        // Arrange
        RecordingLogService log = new();
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово")));

        WorkPanelViewModel vm = CreateViewModel(log);
        vm.Initialize(script, CreateFiles("C:\\media\\a.mkv", "C:\\media\\b.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.SucceededCount.Should().Be(2);
        RecordedLogEvent ended = log.EventsById("exec.queue.ended").Should().ContainSingle().Subject;
        ended.Status.Should().Be(LogStatus.Succeeded);
        ended.Level.Should().Be(LogLevel.Info);
        ended.Properties.Should().NotContainKey("ErrorCode");
    }

    private static LogService CreateLogService(TempDirectoryScope scope)
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl
        };
        return new LogService(options);
    }

    private static string[] ReadJsonl(LogService log)
    {
        using FileStream stream = new(
            log.CurrentLogFile!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('\r'))
            .ToArray();
    }

    private static LogEvent ParseEvent(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        return new LogEvent
        {
            EventId = root.GetProperty("eventId").GetString() ?? string.Empty,
            Level = Enum.Parse<LogLevel>(root.GetProperty("level").GetString() ?? nameof(LogLevel.Info)),
            Status = Enum.Parse<LogStatus>(root.GetProperty("status").GetString() ?? nameof(LogStatus.None)),
            Source = root.GetProperty("source").GetString() ?? string.Empty,
            Message = root.GetProperty("message").GetString() ?? string.Empty,
            OperationId = root.TryGetProperty("operation", out JsonElement operation) ? operation.GetString() : null,
            ItemId = root.TryGetProperty("item", out JsonElement item) ? item.GetString() : null,
            ErrorCode = root.TryGetProperty("errorCode", out JsonElement error) ? error.GetString() : null,
            Exception = root.TryGetProperty("exception", out JsonElement exception) &&
                exception.ValueKind == JsonValueKind.Object
                ? ExceptionInfo.Create(
                    exception.GetProperty("type").GetString() ?? "Exception",
                    exception.GetProperty("message").GetString() ?? string.Empty,
                    0,
                    null,
                    null,
                    false,
                    1,
                    1)
                : null
        };
    }

    private static ObservableCollection<FileQueueItem> CreateFiles(params string[] paths)
    {
        ObservableCollection<FileQueueItem> files = new();
        foreach (string path in paths)
        {
            files.Add(new FileQueueItem(path));
        }

        return files;
    }

    private StubScript CreateScript(Func<string, Dictionary<string, object>, ExecutionContext, Task<ExecutionResult>> handler)
    {
        return new StubScript(
            _mocks.Log.Object,
            _mocks.Settings.Object,
            MockBuilders.CreatePathManagerMock().Object)
        {
            ExecuteHandler = handler
        };
    }

    private WorkPanelViewModel CreateViewModel(ILogService logService)
    {
        WorkPanelViewModel vm = new(
            _mocks.Navigation.Object,
            _mocks.Dialog.Object,
            _mocks.Settings.Object,
            logService,
            _mocks.Dependencies.Object,
            _mocks.Probe.Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    /// <summary>
    /// Создание общих моков в одном месте, чтобы каждый тест получал независимые экземпляры.
    /// </summary>
    private sealed class MockBuildersProxy
    {
        public Mock<INavigationService> Navigation { get; } = MockBuilders.CreateNavigationMock();

        public Mock<IDialogService> Dialog { get; } = MockBuilders.CreateDialogMock();

        public Mock<ISettingsManager> Settings { get; } = MockBuilders.CreateSettingsManagerMock();

        public Mock<IDependencyManager> Dependencies { get; } = MockBuilders.CreateDependencyManagerMock();

        public Mock<IMediaProbeService> Probe { get; } = MockBuilders.CreateMediaProbeMock();

        public Mock<ILogService> Log { get; } = MockBuilders.CreateLogServiceMock();
    }
}
