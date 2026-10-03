// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using Moq;
using ExecutionContext = KTools_App.Models.ExecutionContext;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Тесты read-only ревью typed execution wave: телеметрия элементов очереди,
/// единственный терминальный результат, отсутствие машинного OperationId в журнале,
/// поведение при критической ошибке очереди с фильтрованным списком файлов,
/// нормализация результатов, сохранение измеренной длительности и постпроверки элемента очереди.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TypedExecutionReviewTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigation = null!;
    private Mock<IDialogService> _dialog = null!;
    private Mock<ISettingsManager> _settings = null!;
    private Mock<IDependencyManager> _dependencies = null!;
    private Mock<IMediaProbeService> _probe = null!;
    private RecordingLogService _recorder = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigation = MockBuilders.CreateNavigationMock();
        _dialog = MockBuilders.CreateDialogMock();
        _settings = MockBuilders.CreateSettingsManagerMock();
        _dependencies = MockBuilders.CreateDependencyManagerMock();
        _probe = MockBuilders.CreateMediaProbeMock();
        _recorder = new RecordingLogService();
    }

    [TestMethod]
    public void LogRedactor_AllowsTypedExecutionProperties()
    {
        LogRedactor redactor = new();

        foreach (string key in new[] { "OutputExists", "CleanupState", "MessageCount", "PartiallySucceeded" })
        {
            redactor.IsAllowedPropertyKey(key).Should().BeTrue(
                $"типовое свойство результата '{key}' обязано попадать в allowlist без редактирования");
        }

        IReadOnlyDictionary<string, object?> properties = redactor.RedactProperties(
            new Dictionary<string, object?>
            {
                ["OutputExists"] = true,
                ["CleanupState"] = "Completed",
                ["MessageCount"] = 3,
                ["PartiallySucceeded"] = 1
            });

        properties.Should().ContainKey("OutputExists");
        properties.Should().ContainKey("CleanupState");
        properties.Should().ContainKey("MessageCount");
        properties.Should().ContainKey("PartiallySucceeded");
        properties.Should().NotContainKey(LogRedactor.MarkedPropertiesProperty,
            "allowlisted свойства не должны помечаться как отброшенные");
    }

    [TestMethod]
    public void TypedExecutionProperties_SurviveJsonlSerialization()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl
        };

        using LogService service = new(options);
        service.Write(new LogEvent
        {
            EventId = "exec.item.partial",
            Level = LogLevel.Warning,
            Status = LogStatus.PartiallySucceeded,
            Source = "WorkPanel",
            OperationId = "operation-typed",
            ItemId = "item-typed",
            Message = "Элемент очереди завершён",
            Properties = new Dictionary<string, object?>
            {
                ["Status"] = "PartiallySucceeded",
                ["OutputExists"] = true,
                ["CleanupState"] = "Failed",
                ["MessageCount"] = 2,
                ["PartiallySucceeded"] = 1
            }
        });
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines;
        using (FileStream stream = new(service.CurrentLogFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(stream))
        {
            lines = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        lines.Should().ContainSingle();

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement written = document.RootElement.GetProperty("properties");
        written.GetProperty("OutputExists").GetBoolean().Should().BeTrue();
        written.GetProperty("CleanupState").GetString().Should().Be("Failed");
        written.GetProperty("MessageCount").GetInt32().Should().Be(2);
        written.GetProperty("PartiallySucceeded").GetInt32().Should().Be(1);
        written.TryGetProperty(LogRedactor.RedactedPropertyPrefix + "01", out _).Should().BeFalse(
            "безопасные типизированные свойства не должны маскироваться как [redacted]");
        written.TryGetProperty(LogRedactor.MarkedPropertiesProperty, out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task WorkPanel_ItemEvents_CarryTypedTelemetryAndSingleTerminalEvent()
    {
        StubScript script = CreateScript((file, settings, context) =>
        {
            if (file.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ExecutionResult.Failed(
                    context,
                    "Ошибка декодирования",
                    errorCode: "decode-failed",
                    outputFile: "C:\\media\\missing.mkv",
                    outputExists: false,
                    retryable: true,
                    cleanupState: CleanupState.Completed));
            }

            if (file.Contains("partial", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ExecutionResult.PartiallySucceeded(
                    context,
                    new[] { "Первое сообщение", "Второе сообщение" },
                    errorCode: "source-cleanup-failed",
                    outputFile: "C:\\media\\partial.mkv",
                    outputExists: true,
                    cleanupState: CleanupState.Failed));
            }

            return Task.FromResult(ExecutionResult.Succeeded(
                context,
                "Готово",
                outputFile: "C:\\media\\ok.mkv",
                outputExists: true));
        });

        WorkPanelViewModel vm = CreateViewModel();
        ObservableCollection<FileQueueItem> files = CreateFiles(
            "C:\\media\\ok.mkv", "C:\\media\\failed.mkv", "C:\\media\\partial.mkv");
        vm.Initialize(script, files);

        await vm.StartExecutionCommand.ExecuteAsync(null);

        IReadOnlyList<RecordedLogEvent> terminalEvents = _recorder.Events
            .Where(e => e.EventId.StartsWith("exec.item.", StringComparison.Ordinal)
                && e.EventId != "exec.item.started")
            .ToArray();
        terminalEvents.Should().HaveCount(3,
            "на каждый элемент очереди приходится ровно одно терминальное событие");
        terminalEvents.Select(e => e.EventId).Should().BeEquivalentTo(
            new[] { "exec.item.succeeded", "exec.item.failed", "exec.item.partial" });

        RecordedLogEvent failed = terminalEvents.Single(e => e.EventId == "exec.item.failed");
        failed.GetProperty<string>("Status").Should().Be("Failed");
        failed.GetProperty<bool>("OutputExists").Should().BeFalse();
        failed.GetProperty<string>("CleanupState").Should().Be("Completed");
        failed.GetProperty<int>("MessageCount").Should().Be(1);
        failed.GetProperty<string>("ErrorCode").Should().Be("decode-failed");
        failed.GetProperty<double>("DurationMs").Should().BeGreaterThan(0,
            "WorkPanel обязан измерять длительность выполнения элемента");

        RecordedLogEvent partial = terminalEvents.Single(e => e.EventId == "exec.item.partial");
        partial.GetProperty<string>("Status").Should().Be("PartiallySucceeded");
        partial.GetProperty<string>("CleanupState").Should().Be("Failed");
        partial.GetProperty<int>("MessageCount").Should().Be(2);
        partial.GetProperty<double>("DurationMs").Should().BeGreaterThan(0);

        RecordedLogEvent succeeded = terminalEvents.Single(e => e.EventId == "exec.item.succeeded");
        succeeded.Properties.Should().NotContainKey("ErrorCode",
            "успешный элемент не должен сообщать код ошибки");

        RecordedLogEvent queueEnded = _recorder.Events.Single(e => e.EventId == "exec.queue.ended");
        queueEnded.GetProperty<int>("PartiallySucceeded").Should().Be(1);
        queueEnded.GetProperty<int>("Failed").Should().Be(1);
        queueEnded.GetProperty<int>("Succeeded").Should().Be(1);
        queueEnded.GetProperty<string>("Status").Should().Be("PartiallySucceeded");
    }

    [TestMethod]
    public async Task WorkPanel_QueueEvent_PartialIsWarningAndSuccessHasNoErrorCode()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.PartiallySucceeded(
                context,
                "Частично",
                errorCode: "source-cleanup-failed",
                outputFile: "C:\\media\\p.mkv",
                outputExists: true)));

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\p.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        RecordedLogEvent queueEnded = _recorder.Events.Single(e => e.EventId == "exec.queue.ended");
        queueEnded.Level.Should().Be(LogLevel.Warning,
            "частично успешная очередь обязана фиксироваться как предупреждение");
        queueEnded.Status.Should().Be(LogStatus.PartiallySucceeded);
        queueEnded.GetProperty<string>("ErrorCode").Should().Be("queue-partial");
    }

    [TestMethod]
    public async Task WorkPanel_QueueEvent_AllSkippedHasNoGenericFailureErrorCode()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Skipped(
                context,
                "Пропущено",
                errorCode: "output-exists",
                outputFile: "C:\\media\\s.mkv",
                outputExists: true)));

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\s.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        RecordedLogEvent queueEnded = _recorder.Events.Single(e => e.EventId == "exec.queue.ended");
        queueEnded.GetProperty<string>("ErrorCode").Should().NotBe("execution-failed",
            "очередь без потерь не должна получать общий код ошибки исполнения");
        queueEnded.GetProperty<int>("Skipped").Should().Be(1);
        queueEnded.GetProperty<string>("Status").Should().Be("PartiallySucceeded");
    }

    [TestMethod]
    public async Task WorkPanel_QueueResult_AllSuccess_HasNoGenericFailureErrorCode()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово", "C:\\media\\ok.mkv", true)));

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\ok.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        vm.LastQueueResult.Should().NotBeNull();
        ExecutionResult queueResult = vm.LastQueueResult!;
        queueResult.Status.Should().Be(ExecutionStatus.Succeeded);
        queueResult.ErrorCode.Should().BeNull(
            "при Succeeded ErrorCode обязан быть пустым, иначе поиск отказов по errorCode != null даёт ложное срабатывание");
        queueResult.DurationMs.Should().BeGreaterThan(0);

        RecordedLogEvent queueEnded = _recorder.Events.Single(e => e.EventId == "exec.queue.ended");
        queueEnded.Level.Should().Be(LogLevel.Info);
        queueEnded.Properties.Should().NotContainKey("ErrorCode",
            "успешная очередь не сообщает код ошибки в телеметрии");
        queueEnded.GetProperty<string>("Status").Should().Be(nameof(ExecutionStatus.Succeeded),
            "диагностика успеха остаётся в отдельном свойстве Status");
    }

    [TestMethod]
    public async Task WorkPanel_JournalText_ContainsNoMachineOperationId()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово: файл обработан")));

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\j.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        string operationId = vm.CurrentOperationId!;
        operationId.Should().NotBeNullOrWhiteSpace();
        script.SavedLogText.Should().NotContain(operationId,
            "машинный OperationId не должен попадать в пользовательский журнал");
        script.SavedLogText.Should().Contain("Готово: файл обработан");
        script.SavedLogText.Should().Contain("🎉 Все файлы успешно обработаны");
    }

    [TestMethod]
    public async Task WorkPanel_OutputMissingSuccess_IsDowngradedWithoutLosingDiagnostics()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(
                context,
                "Отчёт без файла",
                outputFile: "C:\\media\\absent.mkv",
                outputExists: false,
                cleanupState: CleanupState.Completed)));

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\absent.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        ExecutionResult result = vm.TypedItemResults.Should().ContainSingle().Subject;
        result.Status.Should().Be(ExecutionStatus.Failed,
            "успех без реально существующего выходного файла не является успехом");
        result.ErrorCode.Should().Be("output-missing");
        result.OutputExists.Should().BeFalse();
        result.CleanupState.Should().Be(CleanupState.Completed,
            "нормализация обязана сохранять CleanupState исходного результата");
        result.Retryable.Should().BeTrue("повторяемая постпроверка помечается как retryable");
        vm.FailedCount.Should().Be(1);
        vm.LastQueueResult.Should().NotBeNull();
        vm.LastQueueResult!.Status.Should().Be(ExecutionStatus.Failed);
    }

    [TestMethod]
    public async Task WorkPanel_CancelledScript_KeepsExceptionInfoAndErrorCode()
    {
        var failure = new InvalidOperationException("сбой после отмены");
        StubScript? captured = null;
        captured = CreateScript((file, settings, context) =>
        {
            captured!.Cancel();
            return Task.FromResult(ExecutionResult.Failed(
                context,
                "Сбой обработки",
                errorCode: "decode-failed",
                outputFile: "C:\\media\\c.mkv",
                outputExists: true,
                exceptionInfo: ExceptionInfo.FromException(failure),
                exception: failure,
                retryable: true,
                cleanupState: CleanupState.Partial));
        });

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(captured, CreateFiles("C:\\media\\c.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        ExecutionResult result = vm.TypedItemResults.Should().ContainSingle().Subject;
        result.Status.Should().Be(ExecutionStatus.Cancelled);
        result.ExceptionInfo.Should().NotBeNull(
            "нормализация при отмене обязана сохранять ExceptionInfo");
        result.Exception.Should().BeSameAs(failure);
        result.Retryable.Should().BeTrue("нормализация при отмене обязана сохранять Retryable");
        result.ErrorCode.Should().Be("decode-failed",
            "нормализация при отмене обязана сохранять ErrorCode источника");
        result.CleanupState.Should().Be(CleanupState.Partial);
    }

    [TestMethod]
    public async Task WorkPanel_BatchException_FilteredQueue_AppliesResultToProcessableItem()
    {
        using var scope = new TempDirectoryScope();
        string videoOne = scope.CreateFile("video-one.mkv", "data");
        string companion = scope.CreateFile("video-one.rus.srt", "data");
        string videoTwo = scope.CreateFile("video-two.mkv", "data");

        ObservableCollection<FileQueueItem> files = CreateFiles(videoOne, companion, videoTwo);
        FileQueueItem secondVideo = files[2];

        _settings.SetupGet(s => s.EnableParallel)
            .Throws(new InvalidOperationException("сбой чтения настроек очереди"));

        StubScript script = new StubScript(
            _recorder,
            _settings.Object,
            MockBuilders.CreatePathManagerMock().Object,
            supportsParallel: true)
        {
            ExecuteHandler = (file, settings, context) =>
                Task.FromResult(ExecutionResult.Succeeded(context, "Готово"))
        };
        script.ProcessableFilesFilter = all => all
            .Where(item => Path.GetExtension(item.FilePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, files);

        await vm.StartExecutionCommand.ExecuteAsync(null);

        secondVideo.IsProcessing.Should().BeFalse(
            "критическая ошибка очереди обязана применять результат к элементу отфильтрованного списка");
        secondVideo.State.Should().Be(FileProcessingState.Failed);
        secondVideo.ExecutionResult.Should().NotBeNull();
        secondVideo.ExecutionResult!.ErrorCode.Should().Be("queue-exception");

        files[1].State.Should().Be(FileProcessingState.Pending,
            "сопутствующий файл, отфильтрованный GetProcessableFiles, не должен получать терминальный результат очереди");
        files[1].ExecutionResult.Should().BeNull();

        script.SavedStatusText.Should().Be("Обработка завершилась ошибкой");
        _recorder.EventsById("exec.item.failed").Should().HaveCount(2,
            "терминальные события получают только обрабатываемые элементы очереди");
    }

    [TestMethod]
    public async Task WorkPanel_SaveState_DoesNotOverwriteLiveProgressUpdates()
    {
        StubScript script = CreateScript((file, settings, context) =>
            Task.FromResult(ExecutionResult.Succeeded(context, "Готово")));
        WorkPanelViewModel vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\s.mkv"));

        await vm.StartExecutionCommand.ExecuteAsync(null);

        script.SavedGlobalProgress = 42.0;
        script.SavedStatusText = "Выполнение: готово 0 из 1 (42.0%) | Осталось: -";
        script.IsProcessing = true;
        vm.GlobalProgressValue = 0;
        vm.StatusText = "устаревшая копия UI";

        vm.SaveState();

        script.SavedStatusText.Should().Be("Выполнение: готово 0 из 1 (42.0%) | Осталось: -",
            "во время выполнения журнал прогресса принадлежит скрипту и не должен теряться");
        script.SavedGlobalProgress.Should().Be(42.0);

        script.IsProcessing = false;
        vm.GlobalProgressValue = 77.0;
        vm.StatusText = "Обработка завершена";
        vm.SaveState();

        script.SavedStatusText.Should().Be("Обработка завершена");
        script.SavedGlobalProgress.Should().Be(77.0);
    }

    [TestMethod]
    public void FileQueueItem_TerminalFailure_DoesNotShowHundredPercent()
    {
        ExecutionContext context = ExecutionContext.CreateBatch("test", 1).ForItem(0);

        FileQueueItem succeeded = new("C:\\media\\a.mkv");
        succeeded.ApplyExecutionResult(ExecutionResult.Succeeded(context, "Готово", "C:\\media\\a.mkv", true));
        succeeded.Progress.Should().Be(100.0);
        succeeded.State.Should().Be(FileProcessingState.Completed);

        (string Name, ExecutionResult Result)[] terminalCases =
        {
            ("failed", ExecutionResult.Failed(context, "Ошибка", errorCode: "decode-failed")),
            ("cancelled", ExecutionResult.Cancelled(context, "Отменено")),
            ("skipped", ExecutionResult.Skipped(context, "Пропущено")),
            ("partial", ExecutionResult.PartiallySucceeded(context, "Частично", errorCode: "source-cleanup-failed"))
        };

        foreach ((string name, ExecutionResult result) in terminalCases)
        {
            FileQueueItem item = new($"C:\\media\\{name}.mkv") { Progress = 40.0 };
            item.ApplyExecutionResult(result);

            item.Progress.Should().Be(40.0,
                $"неуспешный терминальный статус '{name}' не должен показывать 100%");
            item.State.Should().NotBe(FileProcessingState.Completed);
            item.IsProcessing.Should().BeFalse();
        }
    }

    private StubScript CreateScript(Func<string, Dictionary<string, object>, ExecutionContext, Task<ExecutionResult>> handler)
    {
        return new StubScript(
            _recorder,
            _settings.Object,
            MockBuilders.CreatePathManagerMock().Object)
        {
            ExecuteHandler = handler
        };
    }

    private WorkPanelViewModel CreateViewModel()
    {
        WorkPanelViewModel vm = new(
            _navigation.Object,
            _dialog.Object,
            _settings.Object,
            _recorder,
            _dependencies.Object,
            _probe.Object);
        MessengerIsolation.Track(vm);
        return vm;
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
}
