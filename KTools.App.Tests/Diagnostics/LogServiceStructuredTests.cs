// -*- coding: utf-8 -*-
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogServiceStructuredTests
{
    private static LogService CreateService(TempDirectoryScope scope, Action<LogServiceOptions>? configure = null)
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl
        };
        configure?.Invoke(options);
        return new LogService(options);
    }

    private sealed class HostileProperties : IReadOnlyDictionary<string, object?>
    {
        public int EnumerationAttempts { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            EnumerationAttempts++;
            if (EnumerationAttempts > 1)
            {
                throw new InvalidOperationException("мутабельный словарь сломан");
            }

            yield return new KeyValuePair<string, object?>("QueueSize", 5);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => 1;

        public object? this[string key] => 5;

        public IEnumerable<string> Keys => new[] { "QueueSize" };

        public IEnumerable<object?> Values => new object?[] { 5 };

        public bool ContainsKey(string key) => true;

        public bool TryGetValue(string key, out object? value)
        {
            value = 5;
            return true;
        }
    }

    [TestMethod]
    public void Write_ExplicitEventId_IsPreservedWithoutLegacyPrefix()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(
            "exec.queue.started",
            LogLevel.Info,
            "Очередь обработки запущена",
            "WorkPanel",
            LogStatus.Running,
            LogContext.Empty.WithOperation("op-1"));
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].EventId.Should().Be("exec.queue.started");
        events[0].IsLegacy.Should().BeFalse();
        events[0].Status.Should().Be(LogStatus.Running);
        events[0].Source.Should().Be("WorkPanel");
    }

    [TestMethod]
    public void Write_LegacyCalls_UseStableLevelSourceEventId()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Info("первое сообщение", "SettingsManager");
        service.Info("второе сообщение", "SettingsManager");
        service.Warn("предупреждение", "SettingsManager");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(3);
        events[0].EventId.Should().Be("legacy.settingsmanager.info");
        events[1].EventId.Should().Be("legacy.settingsmanager.info", "идентификатор зависит от уровня и источника, а не от текста");
        events[2].EventId.Should().Be("legacy.settingsmanager.warning");
        events.Should().OnlyContain(e => e.IsLegacy);
    }

    [TestMethod]
    public void LegacyCoreApis_NullAndUnsafeInputs_RemainNonThrowing()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        Action act = () =>
        {
            LogService.BuildLegacyEventId("C:secret.txt", LogLevel.Info).Should().Be("legacy.unknown.info");
            service.Log(LogLevel.Info, null!, "C:secret.txt");
            service.Exception(null!, null!, "Z:foo");
            service.Write(null!, LogLevel.Info, null!, "C:secret.txt");
        };

        act.Should().NotThrow();
    }

    [TestMethod]
    public void Write_SequenceAndSession_AreMonotonicWithinSession()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Guid session = service.SessionId;

        for (int index = 0; index < 25; index++)
        {
            service.Info("событие " + index, "Business");
        }

        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(100);
        events.Should().HaveCount(25);
        events.Should().OnlyContain(e => e.SessionId == session);
        events.Select(e => e.Sequence).Should().BeInAscendingOrder();
        events.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void Write_ParallelCalls_PreservePhysicalOrderWithoutSorting()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        const int threads = 8;
        const int perThread = 60;

        System.Threading.Tasks.Task[] tasks = Enumerable.Range(0, threads)
            .Select(index => System.Threading.Tasks.Task.Run(() =>
            {
                for (int item = 0; item < perThread; item++)
                {
                    service.Info($"поток {index} запись {item}", "Parallel");
                }
            }))
            .ToArray();
        System.Threading.Tasks.Task.WaitAll(tasks);
        service.Flush(TimeSpan.FromSeconds(15)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(threads * perThread + 10);
        events.Should().HaveCount(threads * perThread, "ни одно событие не теряется при параллельной записи");
        events.Select(e => e.Sequence).Should().BeInAscendingOrder("порядок в файле совпадает с порядком последовательности");
        events.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void Write_ForeignSessionId_IsOverriddenByService()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Guid foreign = Guid.NewGuid();

        service.Write(new LogEvent
        {
            EventId = "test.foreign.session",
            Level = LogLevel.Info,
            Source = "Business",
            Message = "событие",
            SessionId = foreign,
            Sequence = 999
        });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].SessionId.Should().Be(service.SessionId, "идентификатор сессии принадлежит сервису");
        events[0].Sequence.Should().NotBe(999, "последовательность назначается сервисом");
    }

    [TestMethod]
    public void Write_InvalidEvent_IsReplacedWithSafeMarkerAndNeverSerialized()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "https://cdn.example.com/secret.mkv?token=abc",
            Level = LogLevel.Info,
            Source = @"C:\Users\ivan\app.exe",
            Message = "SENTINEL-PAYLOAD"
        });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string export = service.ReadCurrentLog();
        export.Should().NotContain("SENTINEL-PAYLOAD");
        export.Should().NotContain("ivan");
        export.Should().NotContain("cdn.example.com");
        service.RejectedEventCount.Should().Be(1);

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].EventId.Should().Be(LogEventMarkerNames.RejectedEvent);
        events[0].Level.Should().Be(LogLevel.Warning);
        events[0].ErrorCode.Should().Be("invalid-event-id");
    }

    [TestMethod]
    public void Write_InvalidEnumValues_AreRejected()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent { EventId = "test.bad.level", Level = (LogLevel)42, Source = "Business", Message = "уровень" });
        service.Write(new LogEvent { EventId = "test.bad.status", Level = LogLevel.Info, Status = (LogStatus)99, Source = "Business", Message = "статус" });
        service.Write(new LogEvent { EventId = "test.bad.attempt", Level = LogLevel.Info, Source = "Business", Message = "попытка", Attempt = 0 });
        service.Write(new LogEvent { EventId = "test.bad.duration", Level = LogLevel.Info, Source = "Business", Message = "длительность", DurationMs = double.NaN });
        service.Write(new LogEvent { EventId = "test.bad.exit", Level = LogLevel.Info, Source = "Business", Message = "код", ExitCode = -5 });
        service.Write(new LogEvent { EventId = "test.empty.source", Level = LogLevel.Info, Source = "   ", Message = "источник" });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.RejectedEventCount.Should().Be(6, "невалидные события не сериализуются, а заменяются маркером");
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(20);
        events.Should().HaveCount(6);
        events.Should().OnlyContain(e => e.EventId == LogEventMarkerNames.RejectedEvent);
    }

    [TestMethod]
    public void Write_ProcessAndBatchEvents_ValidateRequiredCorrelationFields()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent { EventId = "process.started", Level = LogLevel.Info, Source = "Business", Message = "нет operation", Tool = "tool", ProcessId = "process-1", Pid = 1, Status = LogStatus.Running });
        service.Write(new LogEvent { EventId = "process.started", Level = LogLevel.Info, Source = "Business", Message = "нет tool", OperationId = "op-1" });
        service.Write(new LogEvent { EventId = "process.started", Level = LogLevel.Info, Source = "Business", Message = "нет process", OperationId = "op-1", Tool = "tool" });
        service.Write(new LogEvent { EventId = "process.started", Level = LogLevel.Info, Source = "Business", Message = "нет pid", OperationId = "op-1", Tool = "tool", ProcessId = "process-1" });
        service.Write(new LogEvent { EventId = "process.started", Level = LogLevel.Info, Source = "Business", Message = "неверный status", OperationId = "op-1", Tool = "tool", ProcessId = "process-1", Pid = 1, Status = LogStatus.Succeeded });
        service.Write(new LogEvent { EventId = "process.exit", Level = LogLevel.Error, Source = "Business", Message = "нет duration", OperationId = "op-1", Tool = "tool", ProcessId = "process-1", Pid = 1, ExitCode = 1, Status = LogStatus.Failed });
        service.Write(new LogEvent { EventId = "process.exit", Level = LogLevel.Error, Source = "Business", Message = "не terminal", OperationId = "op-1", Tool = "tool", ProcessId = "process-1", Pid = 1, ExitCode = 1, DurationMs = 10, Status = LogStatus.Running });
        service.Write(new LogEvent { EventId = "exec.item.started", Level = LogLevel.Info, Source = "Business", Message = "нет item", OperationId = "op-1" });
        service.Write(new LogEvent { EventId = "batch.started", Level = LogLevel.Info, Source = "Business", Message = "нет operation" });
        service.Write(new LogEvent { EventId = "network.request", Level = LogLevel.Info, Source = "Business", Message = "незарезервированный префикс", StatusCode = "200" });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(20);
        events.Should().HaveCount(10);
        events.Take(9).Select(e => e.ErrorCode).Should().Equal(
            "process-missing-operation-id",
            "process-missing-tool",
            "process-missing-internal-id",
            "process-missing-os-pid",
            "process-started-invalid-status",
            "process-exit-missing-duration",
            "process-exit-invalid-status",
            "exec-item-missing-item-id",
            "batch-missing-operation-id");
        events[9].EventId.Should().Be("network.request",
            "префикс network.* не зарезервирован схемой: таких событий в production нет, ветка схемы удалена");
        events[9].StatusCode.Should().Be("200");
        service.RejectedEventCount.Should().Be(9);
    }

    /// <summary>
    /// Ветка схемы <c>network.*</c> удалена как мёртвая, но <c>Attempt</c> остаётся
    /// частью контракта события и проверяется всеми путями валидации, включая
    /// тестовый двойник <see cref="LogService.ValidateEventSchema"/>.
    /// </summary>
    [TestMethod]
    public void ValidateEventSchema_NonPositiveAttempt_IsRejected()
    {
        LogEvent invalid = new()
        {
            EventId = "exec.queue.ended",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Source = "Business",
            Message = "попытка вне диапазона",
            OperationId = "op-1",
            Attempt = 0
        };

        LogService.ValidateEventSchema(invalid).Should().Be("invalid-attempt");
    }

    [TestMethod]
    public void Write_HostileCallerProperties_AreSnapshottedOrReplacedWithMarker()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        var hostile = new HostileProperties();

        Action act = () => service.Write("test.hostile.properties", LogLevel.Info, "событие", "Business", LogStatus.None, null, hostile);

        act.Should().NotThrow("мутабельный словарь вызывающего кода не должен ломать логгер");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].EventId.Should().Be("test.hostile.properties");
    }

    [TestMethod]
    public void Write_ContextAndProperties_AreMergedIntoEvent()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        LogContext context = LogContext.Empty.WithOperation("op-42").WithTool("yt-dlp");

        var properties = new Dictionary<string, object?>
        {
            ["ScriptId"] = "MediaDownloader",
            ["QueueSize"] = 12,
            ["UnlistedValue"] = "SENTINEL"
        };

        service.Write(
            "exec.item.started",
            LogLevel.Info,
            LogStatus.Running,
            "Начата загрузка",
            null,
            "MediaDownloader",
            context.ToChild().WithItem("item-07"),
            properties);
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        LogEvent logEvent = events[0];
        logEvent.OperationId.Should().Be("op-42", "корреляция наследуется из контекста");
        logEvent.ItemId.Should().Be("item-07");
        logEvent.Tool.Should().Be("yt-dlp");
        logEvent.Properties["ScriptId"].Should().Be("MediaDownloader");
        logEvent.Properties["QueueSize"].Should().Be(12);
        logEvent.Properties.ContainsKey("UnlistedValue").Should().BeFalse("неallowlisted свойство не сохраняет исходное имя");
        logEvent.Properties.Values.Should().NotContain("SENTINEL");
    }

    [TestMethod]
    public void LogContext_UnsafeCorrelationValues_AreDropped()
    {
        LogContext context = LogContext.Empty
            .WithOperation("op-1")
            .ToChild()
            .WithItem(@"C:\Users\ivan\item.mkv")
            .WithProcess("https://host/process")
            .WithAttempt(0);

        context.ResolveOperationId().Should().Be("op-1");
        context.ResolveItemId().Should().BeNull("путь в идентификаторе элемента отбрасывается");
        context.ResolveProcessId().Should().BeNull("URL в идентификаторе процесса отбрасывается");
        context.ResolveAttempt().Should().BeNull("попытка вне диапазона отбрасывается");
    }

    [TestMethod]
    public void Write_Exception_ProducesStructuredChainWithoutDuplicateMessage()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Exception exception;
        try
        {
            try
            {
                throw new ArgumentException("внутренняя ошибка разбора");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("внешняя ошибка операции", inner);
            }
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        service.Write(
            "exec.item.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Элемент не обработан",
            exception,
            "VideoEncodingScript",
            LogContext.Empty.WithOperation("op-1").ToChild().WithItem("item-1"));
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        LogEvent logEvent = events[0];
        logEvent.Status.Should().Be(LogStatus.Failed);
        logEvent.Exception!.Type.Should().Be("System.InvalidOperationException");
        logEvent.Exception.Message.Should().Be("внешняя ошибка операции");
        logEvent.Exception.Inner!.Type.Should().Be("System.ArgumentException");
        logEvent.Message.Should().NotContain("внешняя ошибка операции");
        logEvent.Exception.StackTrace.Should().Contain(nameof(Write_Exception_ProducesStructuredChainWithoutDuplicateMessage));
    }

    [TestMethod]
    public void Write_LegacyExceptionMethod_KeepsContextWithoutAppendingMessage()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Exception exception;
        try
        {
            throw new InvalidOperationException("деталь исключения");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        service.Exception(exception, "контекст сбоя", "Integration");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].Message.Should().Be("контекст сбоя");
        events[0].Level.Should().Be(LogLevel.Error);
        events[0].Exception!.Message.Should().Be("деталь исключения");
        events[0].Message.Should().NotContain("деталь исключения");
    }

    [TestMethod]
    public void Write_DeepException_AddsBoundedTruncationMarkers()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.MaxExceptionDepth = 2);
        Exception exception = new InvalidOperationException("уровень 0");
        for (int index = 1; index < 5; index++)
        {
            exception = new InvalidOperationException("уровень " + index, exception);
        }

        service.Exception(exception, "сбой", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        LogEvent result = service.ReadRecentEvents(1).Should().ContainSingle().Subject;
        result.Exception!.ExceptionTruncated.Should().BeTrue();
        result.Properties[LogEventMarkerNames.ExceptionTruncated].Should().Be(true);
        result.Properties[LogEventMarkerNames.OriginalExceptionCount].Should().Be(5);
        result.Properties[LogEventMarkerNames.KeptExceptionCount].Should().Be(2);
    }

    [TestMethod]
    public void MinLevel_Filtered_DropsLowerLevelsWithoutCodeChanges()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.MinLevel = LogLevel.Warning;

        service.DebugLog("отладочная запись", "Business");
        service.Info("информационная запись", "Business");
        service.Warn("предупреждение", "Business");
        service.Error("ошибка", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(2);
        events.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Error);
    }

    [TestMethod]
    public void MinLevel_Default_IsDebugForLegacyCompatibility()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.DebugLog("отладочная запись", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.MinLevel.Should().Be(LogLevel.Debug);
        service.ReadRecentEvents(10).Should().HaveCount(1, "legacy DebugLog по умолчанию не отбрасывается");
    }

    [TestMethod]
    public void MinLevel_ClampsOutOfRangeValues()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.MinLevel = (LogLevel)99;
        service.Error("ошибка", "Business");
        service.Fatal("фатальная ошибка", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.MinLevel.Should().Be(LogLevel.Fatal);
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].Level.Should().Be(LogLevel.Fatal);
    }

    [TestMethod]
    public void LogReceived_DeliversPreparedRedactedEvent()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        LogEvent? received = null;
        using ManualResetEventSlim delivered = new(false);
        service.LogReceived += OnEvent;

        service.Write("test.redaction", LogLevel.Info, @"Файл C:\Users\ivan\secret.mkv обработан", "Business");
        delivered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

        received.Should().NotBeNull();
        received!.Message.Should().NotContain("ivan");
        received.Message.Should().Contain("secret.mkv");
        received.Sequence.Should().BeGreaterThan(0);
        received.SessionId.Should().Be(service.SessionId);
        return;

        void OnEvent(object? sender, LogEvent logEvent)
        {
            received = logEvent;
            delivered.Set();
        }
    }

    [TestMethod]
    public void Write_EmptyEventId_FallsBackToStableLegacyIdentifier()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent { EventId = string.Empty, Level = LogLevel.Warning, Source = "AbstractScript", Message = "без идентификатора" });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.ReadRecentEvents(10)[0].EventId.Should().Be("legacy.abstractscript.warning");
    }

    [TestMethod]
    public void Options_Defaults_MatchBoundedPolicy()
    {
        LogServiceOptions options = new();

        options.QueueCapacity.Should().Be(4096);
        options.MaxQueuedBytes.Should().Be(8 * 1024 * 1024);
        options.UiQueueCapacity.Should().Be(4096);
        options.SubscriberQueueCapacity.Should().BeGreaterThan(0);
        options.MaxMessageLength.Should().Be(4096);
        options.MaxPropertiesLength.Should().Be(8192);
        options.MaxDetailLength.Should().Be(16384);
        options.MaxFileBytes.Should().Be(16L * 1024 * 1024);
        options.RotationTriggerRatio.Should().Be(0.8d);
        options.RotationTriggerBytes.Should().Be(13_421_772);
        options.MaxFiles.Should().Be(20);
        options.MaxDirectoryBytes.Should().Be(256L * 1024 * 1024);
        options.RetentionDays.Should().Be(10);
        options.FlushIntervalMilliseconds.Should().Be(1000);
        options.MaxEmergencyFileBytes.Should().Be(65536);
        options.MaxEmergencyFiles.Should().Be(5);
        options.EmergencyRetentionDays.Should().Be(30);
    }

    [TestMethod]
    public void Options_Normalize_ClampsInvalidAndNonFiniteValues()
    {
        LogServiceOptions options = new()
        {
            QueueCapacity = 0,
            MaxMessageLength = -5,
            MaxFileBytes = 1,
            MaxFiles = 0,
            RetentionDays = 0,
            MaxQueuedBytes = 0,
            RotationTriggerRatio = double.NaN
        };

        LogServiceOptions normalized = options.Normalize();

        normalized.QueueCapacity.Should().Be(4096);
        normalized.MaxMessageLength.Should().BeGreaterThanOrEqualTo(64);
        normalized.MaxFileBytes.Should().BeGreaterThanOrEqualTo(4096);
        normalized.MaxFiles.Should().BeGreaterThanOrEqualTo(1);
        normalized.RetentionDays.Should().BeGreaterThanOrEqualTo(1);
        normalized.MaxQueuedBytes.Should().BeGreaterThanOrEqualTo(64 * 1024);
        normalized.RotationTriggerRatio.Should().Be(0.8d, "NaN заменяется безопасным значением по умолчанию");
        normalized.RotationTriggerBytes.Should().BeGreaterThan(0);

        options.RotationTriggerRatio = double.PositiveInfinity;
        options.Normalize().RotationTriggerRatio.Should().Be(0.8d);
        options.RotationTriggerRatio = 5d;
        options.Normalize().RotationTriggerRatio.Should().Be(1.0d);
    }

    [TestMethod]
    public void LogService_DefaultConstructor_RemainsAvailableForDependencyInjection()
    {
        using LogService service = new();

        service.EffectiveLogDirectory.Should().NotBeNullOrEmpty();
        Action act = () => service.Info("проверка конструирования по умолчанию", "DiTest");
        act.Should().NotThrow();
        service.Dispose();
    }
}
