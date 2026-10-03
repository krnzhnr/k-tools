// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogServiceResilienceTests
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

    [TestMethod]
    public void Write_SubscriberThrows_IsolatedAndCounted()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        int goodCalls = 0;
        int secondGoodCalls = 0;

        service.LogReceived += Throwing;
        service.LogReceived += (_, _) => Interlocked.Increment(ref goodCalls);
        service.LogReceived += Throwing;
        service.LogReceived += (_, _) => Interlocked.Increment(ref secondGoodCalls);

        Action act = () =>
        {
            for (int index = 0; index < 5; index++)
            {
                service.Info("бизнес-операция " + index, "Business");
            }
        };

        act.Should().NotThrow("ошибка подписчика не должна ломать бизнес-код");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        SpinWaitUntil(() => goodCalls == 5 && secondGoodCalls == 5);
        goodCalls.Should().Be(5, "ошибка одного подписчика не отменяет доставку остальным");
        secondGoodCalls.Should().Be(5);
        service.SubscriberErrorCount.Should().Be(10, "каждая ошибка подписчика учитывается отдельно");
        service.WriteErrorCount.Should().Be(0, "файловый канал не затронут ошибками подписчиков");
        service.ReadCurrentLog().Should().Contain("бизнес-операция 4");
        return;

        void Throwing(object? sender, LogEvent logEvent) => throw new InvalidOperationException("сбой подписчика");
    }

    [TestMethod]
    public void Write_HangingSubscriber_DoesNotBlockWriterFileOrOtherSubscribers()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        using ManualResetEventSlim release = new(false);
        int fastCalls = 0;

        service.LogReceived += (_, _) => release.Wait(TimeSpan.FromSeconds(30));
        service.LogReceived += (_, _) => Interlocked.Increment(ref fastCalls);

        for (int index = 0; index < 20; index++)
        {
            service.Info("событие " + index, "Business");
        }

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue("зависший подписчик не блокирует файл и очередь");
        service.ReadCurrentLog().Should().Contain("событие 19");
        SpinWaitUntil(() => Volatile.Read(ref fastCalls) == 20);
        Volatile.Read(ref fastCalls).Should().Be(20, "второй подписчик получает события независимо от зависшего");
        release.Set();
    }

    [TestMethod]
    public void Write_SubscriberQueueOverflow_CountsDropsAndKeepsFileWorking()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.SubscriberQueueCapacity = 16);
        using ManualResetEventSlim release = new(false);
        service.LogReceived += (_, _) => release.Wait(TimeSpan.FromSeconds(30));

        for (int index = 0; index < 2000; index++)
        {
            service.Info("событие " + index, "Business");
        }

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        service.SubscriberDroppedCount.Should().BeGreaterThan(0, "переполнение bounded-очереди подписчика измеряется отдельно от ошибок доставки");
        release.Set();
        service.ReadCurrentLog().Should().Contain("событие 1999");
    }

    [TestMethod]
    public void Write_QueueOverflow_DropsBoundedEventsAndPersistsMarker()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.QueueCapacity = 16);
        using ManualResetEventSlim release = new(false);
        service.LogReceived += (_, _) => release.Wait(TimeSpan.FromSeconds(30));

        for (int index = 0; index < 500; index++)
        {
            service.DebugLog("нагрузка " + index, "Flood");
        }

        long droppedWhileBlocked = service.DroppedEventCount;
        droppedWhileBlocked.Should().BeGreaterThan(0, "переполнение bounded-очереди измеряется счётчиком");
        release.Set();
        service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();

        string export = service.ReadCurrentLog();
        export.Should().Contain(LogEventMarkerNames.DroppedMarker, "маркер отброшенных событий сохраняется в файле");
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(1000);
        long[] markerCounts = events
            .Where(e => e.EventId == LogEventMarkerNames.DroppedMarker)
            .Select(e => Convert.ToInt64(e.Properties["DroppedCount"], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        markerCounts.Should().NotBeEmpty();
        markerCounts.Sum().Should().BeGreaterThanOrEqualTo(droppedWhileBlocked, "маркеры учитывают все отброшенные события");
        markerCounts[^1].Should().Be(service.DroppedEventCount, "последний маркер фиксирует накопленный итог");
    }

    [TestMethod]
    public void Write_ByteBoundQueue_DropsOversizedEvent()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxMessageLength = 256 * 1024;
            options.QueueCapacity = 4096;
        });

        service.Write("test.huge", LogLevel.Warning, new string('я', 200_000), "Business");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        service.DroppedEventCount.Should().Be(1, "событие больше байтового лимита очереди отбрасывается");
        service.ReadCurrentLog().Should().NotContain(new string('я', 1000));
    }

    [TestMethod]
    public void QueuedEventCount_ReflectsCurrentQueueDepth()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.QueueCapacity = 16);
        using ManualResetEventSlim release = new(false);
        service.LogReceived += (_, _) => release.Wait(TimeSpan.FromSeconds(30));

        for (int index = 0; index < 8; index++)
        {
            service.Info("событие " + index, "Business");
        }

        service.QueuedEventCount.Should().BeLessThanOrEqualTo(16);
        service.QueuedEventBytes.Should().BeLessThanOrEqualTo(8 * 1024 * 1024);
        release.Set();
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        service.QueuedEventCount.Should().Be(0, "после дренирования очередь пуста");
        service.QueuedEventBytes.Should().Be(0);
    }

    [TestMethod]
    public void Write_LogPathBlockedByDirectory_ReportsWriteErrorWithoutThrowing()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string? blocked = service.CurrentLogFile;
        blocked.Should().NotBeNullOrEmpty();
        Directory.CreateDirectory(blocked!);

        Action act = () =>
        {
            for (int index = 0; index < 10; index++)
            {
                service.Error("событие при недоступном файле " + index, "Business");
            }
        };

        act.Should().NotThrow("ошибка файлового канала не должна ломать бизнес-код");
        service.Flush(TimeSpan.FromSeconds(30));
        service.WriteErrorCount.Should().BeGreaterThan(0, "ошибка записи измеряется счётчиком");
        service.ReadCurrentLog().Should().NotBeNull();
        service.ReadRecentEvents(10).Should().NotBeNull();
    }

    [TestMethod]
    public void WriteErrorMarker_RepairFailureStopsFileIntake()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("до сбоя маркера", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        service.ClearCurrentLog().Should().BeTrue();

        string path = service.CurrentLogFile!;
        File.Delete(path);
        Directory.CreateDirectory(path);
        MethodInfo marker = typeof(LogService).GetMethod("WriteErrorMarker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Action act = () => marker.Invoke(service, new object[] { LogEventMarkerNames.WriteFailure });

        act.Should().NotThrow();
        Directory.Delete(path);
        service.Info("после сбоя маркера", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeFalse();

        File.Exists(path).Should().BeFalse("неуспешный repair запрещает последующую запись в тот же live-файл");
        service.WriteErrorCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void WriteErrorMarker_RepairsPartialLineBeforeSuccessfulMarker()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string path = service.CurrentLogFile!;
        File.WriteAllText(path, "{\"partial\":\n");
        typeof(LogService)
            .GetField("_writerPoisoned", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, 1);
        MethodInfo marker = typeof(LogService).GetMethod("WriteErrorMarker", BindingFlags.Instance | BindingFlags.NonPublic)!;

        marker.Invoke(service, new object[] { LogEventMarkerNames.WriteFailure });

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        Action parse = () => JsonDocument.Parse(lines[0]).Dispose();
        parse.Should().NotThrow();
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        document.RootElement.GetProperty("eventId").GetString().Should().Be(LogEventMarkerNames.WriteFailure);
    }

    [TestMethod]
    public void Write_PartialWriteFailure_RepairsBeforeMarkerAndNextLine()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.FlushIntervalMilliseconds = 60000);
        string path = service.CurrentLogFile!;
        PartialWriteStream stream = InstallPartialWriter(service, path);

        service.Info("первая запись при частичной ошибке", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30));
        SpinWaitUntil(() => stream.FailureTriggered);

        stream.FailureTriggered.Should().BeTrue();
        service.Info("после восстановления", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadPhysicalLines(path);
        lines.Should().HaveCount(2);
        foreach (string line in lines)
        {
            Action parse = () => JsonDocument.Parse(line).Dispose();
            parse.Should().NotThrow();
        }

        using JsonDocument marker = JsonDocument.Parse(lines[0]);
        marker.RootElement.GetProperty("eventId").GetString().Should().Be(LogEventMarkerNames.WriteFailure);
        using JsonDocument next = JsonDocument.Parse(lines[1]);
        next.RootElement.GetProperty("message").GetString().Should().Be("после восстановления");
    }

    [TestMethod]
    public void PeriodicFlush_PartialFailure_RepairsBeforeSuccessfulMarker()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.FlushIntervalMilliseconds = 1000);
        string path = service.CurrentLogFile!;
        PartialWriteStream stream = InstallPartialWriter(service, path);
        SetLastFlushTick(service, Environment.TickCount);
        service.Write("test.periodic", LogLevel.Info, "периодическая запись");

        SpinWaitUntil(() => stream.FailureTriggered);

        stream.FailureTriggered.Should().BeTrue();
        service.Info("после периодического сброса", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        string[] lines = ReadPhysicalLines(path);
        lines.Should().HaveCount(2);
        foreach (string line in lines)
        {
            Action parse = () => JsonDocument.Parse(line).Dispose();
            parse.Should().NotThrow();
        }

        using JsonDocument marker = JsonDocument.Parse(lines[0]);
        marker.RootElement.GetProperty("eventId").GetString().Should().Be(LogEventMarkerNames.WriteFailure);
    }

    [TestMethod]
    public void EmergencyBudget_SuppressionsAreAccountedInStatus()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxEmergencyFileBytes = 4096
        };

        using LogService service = new(options);
        Directory.CreateDirectory(service.CurrentLogFile!);

        for (int index = 0; index < 50; index++)
        {
            service.Error("событие " + index, "Business");
        }

        service.Flush(TimeSpan.FromSeconds(30));
        service.WriteErrorCount.Should().BeGreaterThan(LogService.MaxEmergencyReports);
        int emergencyFiles = Directory.Exists(Path.Combine(scope.RootPath, "emergency"))
            ? Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern).Length
            : 0;
        emergencyFiles.Should().BeLessThanOrEqualTo(LogService.MaxEmergencyReports, "бюджет аварийных записей строго ограничен");
        emergencyFiles.Should().BeGreaterThan(0, "аварийный след остаётся независимым каналом");

        LogServiceStatus status = service.Status;
        status.EmergencyWritten.Should().Be(
            emergencyFiles,
            "снимок статуса отражает фактически созданные аварийные записи, а не только те, что дошли до общего sink");
        status.EmergencyWritten.Should().BeGreaterThan(0);
        status.EmergencySuppressed.Should().BeGreaterThan(
            0,
            "подавление по исчерпании бюджета обязано быть посчитано, иначе потеря аварийного следа выглядит как «ошибок не было»");
        status.HasLoss.Should().BeTrue("подавление аварийных записей — это потеря диагностического следа");
        status.Describe().Should().Contain("emergencySuppressed=" + status.EmergencySuppressed);
    }

    /// <summary>
    /// Счётчики внутреннего EmergencyLogService не должны теряться: снимок статуса
    /// обязан показывать и записи, и потери аварийного канала (пункт 11 аудита).
    /// </summary>
    [TestMethod]
    public void Status_ExposesInternalEmergencySinkCounters()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        LogServiceStatus status = service.Status;
        status.EmergencyWritten.Should().Be(0, "до отказов аварийных записей нет");
        status.EmergencyDropped.Should().Be(0);
        status.EmergencySuppressed.Should().Be(0);
        status.FlushScheduleFailures.Should().Be(0);
        status.HasLoss.Should().BeFalse("без отказов потерь нет");
    }

    [TestMethod]
    public void Dispose_ThenWrite_CountsDropAndDoesNotThrow()
    {
        using var scope = new TempDirectoryScope();
        LogService service = CreateService(scope);
        service.Info("до освобождения", "Business");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.Dispose();
        Action act = () => service.Info("после освобождения", "Business");

        act.Should().NotThrow();
        service.DroppedEventCount.Should().BeGreaterThan(0, "события после освобождения считаются отброшенными");
        service.Flush(TimeSpan.FromSeconds(1)).Should().BeFalse("Flush после Dispose сообщает закрытое состояние");
        service.ReadCurrentLog().Should().Contain("до освобождения");
        service.ReadCurrentLog().Should().NotContain("после освобождения");
        service.Dispose();
    }

    [TestMethod]
    public void Dispose_ConcurrentCalls_CompleteOnceWithoutHang()
    {
        using var scope = new TempDirectoryScope();
        LogService service = CreateService(scope);
        service.Info("до освобождения", "Business");

        Task[] disposals = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(service.Dispose))
            .ToArray();

        Task.WaitAll(disposals, TimeSpan.FromSeconds(30)).Should().BeTrue();
        service.ReadCurrentLog().Should().Contain("до освобождения");
    }

    [TestMethod]
    public void PublicApi_AfterDispose_RemainsNonThrowing()
    {
        using var scope = new TempDirectoryScope();
        LogService service = CreateService(scope);
        service.Dispose();

        Action act = () =>
        {
            service.Write(new LogEvent { EventId = "test.event", Level = LogLevel.Info, Message = "событие" });
            service.Write("test.event", LogLevel.Info, "событие");
            service.Exception(new InvalidOperationException("сбой"), "контекст");
            service.Log(LogLevel.Warning, "уровень");
            service.DebugLog("отладка");
            service.Info("информация");
            service.Warn("предупреждение");
            service.Error("ошибка");
            service.Fatal("фатально");
            service.Flush();
            service.InitializeLogFile();
            service.InitializeLogFile(null);
            service.ReadCurrentLog();
            service.ReadRecentEvents(5);
            service.ClearCurrentLog();
        };

        act.Should().NotThrow("публичные методы логгера никогда не бросают наружу");
    }

    [TestMethod]
    public void LogReceived_NotSubscribed_DoesNotFailWrites()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        Action act = () => service.Info("событие без подписчиков", "Business");

        act.Should().NotThrow();
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        service.ReadCurrentLog().Should().Contain("событие без подписчиков");
    }

    [TestMethod]
    public void Write_NullEvent_IsIgnored()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        Action act = () => service.Write((LogEvent)null!);

        act.Should().NotThrow();
        service.DroppedEventCount.Should().Be(0);
    }

    [TestMethod]
    public void Write_SubscriberCallsLogger_DoesNotDeadlock()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        int nested = 0;
        service.LogReceived += OnNested;

        Action act = () => service.Write("test.first", LogLevel.Info, "первая запись");

        act.Should().NotThrow("логирование из подписчика не должно вызывать рекурсивную ошибку");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        SpinWaitUntil(() => Volatile.Read(ref nested) == 1);
        service.ReadCurrentLog().Should().Contain("вложенная запись");
        service.SubscriberErrorCount.Should().Be(0);
        return;

        void OnNested(object? sender, LogEvent logEvent)
        {
            if (logEvent.EventId != "test.first")
            {
                return;
            }

            service.Write("test.nested", LogLevel.Debug, "вложенная запись");
            Interlocked.Increment(ref nested);
        }
    }

    [TestMethod]
    public void Flush_FromSubscriberThread_PerformsRealFlush()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        bool subscriberFlushed = false;
        bool flushResult = false;

        service.LogReceived += (_, _) =>
        {
            if (flushResult)
            {
                return;
            }

            flushResult = service.Flush(TimeSpan.FromSeconds(30));
            subscriberFlushed = true;
        };

        service.Write("test.subscriber.flush", LogLevel.Info, "первая запись");
        SpinWaitUntil(() => Volatile.Read(ref subscriberFlushed));

        flushResult.Should().BeTrue("Flush с потока подписчика выполняет реальный сброс буфера");
        service.ReadRecentEvents(10).Should().ContainSingle(e => e.EventId == "test.subscriber.flush");
    }

    [TestMethod]
    public void Write_SourceWithSecret_IsSanitizedToUnknown()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        List<LogEvent> received = new();
        using ManualResetEventSlim delivered = new(false);
        service.LogReceived += (_, logEvent) =>
        {
            lock (received)
            {
                received.Add(logEvent);
            }

            delivered.Set();
        };

        service.Write(new LogEvent
        {
            EventId = "test.safe.source",
            Level = LogLevel.Info,
            Source = @"C:\Users\ivan\secret.exe",
            Message = "обычное сообщение"
        });

        delivered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        lock (received)
        {
            received.Should().ContainSingle();
            received[0].Source.Should().Be(LogRedactor.UnknownIdentifier, "путь в источнике не сохраняется");
            received[0].Message.Should().NotContain("ivan");
        }
    }

    [TestMethod]
    public void Write_BatchDelivery_DeliversEveryEventOnceInPersistedOrder()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.FlushIntervalMilliseconds = 50);
        const int Rounds = 20;
        const int PerRound = 250;
        List<long> delivered = new();
        service.LogReceived += (_, logEvent) =>
        {
            lock (delivered)
            {
                delivered.Add(logEvent.Sequence);
            }
        };

        // Частые запросы Flush ставят служебные элементы батча внутрь потока событий,
        // поэтому позиции, пропущенные при обработке батча, встречаются постоянно.
        for (int round = 0; round < Rounds; round++)
        {
            for (int index = 0; index < PerRound; index++)
            {
                service.Info(
                    "событие " + round.ToString("D2", CultureInfo.InvariantCulture)
                    + "-" + index.ToString("D3", CultureInfo.InvariantCulture),
                    "BatchOrder");
            }

            service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();
        }

        int expected = Rounds * PerRound;
        SpinWaitUntil(() =>
        {
            lock (delivered)
            {
                return delivered.Count == expected;
            }
        });

        long[] deliveredSequences;
        lock (delivered)
        {
            deliveredSequences = delivered.ToArray();
        }

        long[] persistedSequences = ReadSourceSequenceOrder(service.CurrentLogFile!, "BatchOrder");

        // Доставка выполняется сразу после сериализации, поэтому общего буфера публикации
        // больше нет: устаревший элемент батча не может быть доставлен повторно, а порядок
        // доставки обязан совпасть с порядком строк в файле.
        deliveredSequences.Should().HaveCount(expected, "каждое записанное событие доставляется подписчику ровно один раз");
        deliveredSequences.Should().OnlyHaveUniqueItems("событие не должно дублироваться при доставке батча");
        deliveredSequences.Should().BeInAscendingOrder("порядок доставки обязан совпадать с порядком Sequence");
        persistedSequences.Should().HaveCount(expected, "одна строка JSONL на событие");
        persistedSequences.Should().Equal(
            deliveredSequences,
            "порядок доставки подписчикам обязан совпадать с порядком записи в файл");
    }

    [TestMethod]
    public void Write_DiskWriteFails_SubscriberStillReceivesEveryEventOnce()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.FlushIntervalMilliseconds = 60000);
        const int Count = 200;
        PartialWriteStream stream = InstallPartialWriter(service, service.CurrentLogFile!);
        List<long> sequences = new();
        int total = 0;
        service.LogReceived += (_, logEvent) =>
        {
            lock (sequences)
            {
                sequences.Add(logEvent.Sequence);
            }

            Interlocked.Increment(ref total);
        };

        for (int index = 0; index < Count; index++)
        {
            service.Info("событие при сбое диска " + index, "DiskFailure");
        }

        service.Flush(TimeSpan.FromSeconds(20));
        stream.FailureTriggered.Should().BeTrue("файловая запись обязана действительно упасть");
        SpinWaitUntil(() => Volatile.Read(ref total) == Count);

        Volatile.Read(ref total).Should().Be(Count, "сбой записи на диск не должен отменять доставку подписчикам");
        lock (sequences)
        {
            sequences.Should().HaveCount(Count);
            sequences.Should().OnlyHaveUniqueItems("событие не должно теряться или дублироваться при сбое диска");
            sequences.Should().BeInAscendingOrder("порядок доставки не зависит от успешности файловой записи");
        }

        service.WriteErrorCount.Should().BeGreaterThan(0, "счётчик write-errors обязан учитывать сбой записи");
    }

    private static long[] ReadSourceSequenceOrder(string path, string source)
    {
        return ReadPhysicalLines(path)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                return (Source: root.GetProperty("source").GetString(), Sequence: root.GetProperty("sequence").GetInt64());
            })
            .Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal))
            .Select(entry => entry.Sequence)
            .ToArray();
    }

    private static PartialWriteStream InstallPartialWriter(LogService service, string path)
    {
        PartialWriteStream stream = new(path);
        byte[] partial = Encoding.UTF8.GetBytes("{\"partial\":");
        stream.Write(partial, 0, partial.Length);
        stream.Arm();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LogService).GetField("_stream", flags)!.SetValue(service, stream);
        typeof(LogService).GetField("_lineBatchCount", flags)!.SetValue(service, 0);
        typeof(LogService).GetField("_writerBytes", flags)!.SetValue(service, 0L);
        typeof(LogService).GetField("_writerPoisoned", flags)!.SetValue(service, 0);
        typeof(LogService).GetField("_writerStopping", flags)!.SetValue(service, 0);
        return stream;
    }

    private static void SetLastFlushTick(LogService service, int value)
    {
        typeof(LogService)
            .GetField("_lastFlushTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);
    }

    private static string[] ReadPhysicalLines(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class PartialWriteStream : Stream
    {
        private readonly FileStream _inner;
        private int _armed;
        private int _failed;

        public PartialWriteStream(string path)
        {
            _inner = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public bool FailureTriggered => Volatile.Read(ref _failed) != 0;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (Volatile.Read(ref _armed) == 0)
            {
                _inner.Write(buffer, offset, count);
                return;
            }

            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                int partial = Math.Min(8, count);
                _inner.Write(buffer, offset, partial);
                _inner.Flush();
                throw new IOException("partial write");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static void SpinWaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }
    }
}
