// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogServiceReadTests
{
    private static LogService CreateService(TempDirectoryScope scope, Action<LogServiceOptions>? configure = null)
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000
        };
        configure?.Invoke(options);
        return new LogService(options);
    }

    private static void AppendRaw(string path, string content)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
    }

    [TestMethod]
    public void ReadRecentEvents_FileReadFailure_ReturnsEmptyAndIncrementsReadError()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("будет прочитано", "ReadTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        service.Dispose();

        using FileStream locked = new(service.CurrentLogFile!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);

        events.Should().BeEmpty("частичный результат не выдаётся при ошибке чтения файла");
        service.ReadCurrentLog().Should().BeEmpty();
        service.ReadErrorCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void ReadRecentEvents_ReturnsTypedEventsInChronologicalOrder()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        LogContext context = LogContext.Empty.WithOperation("op-1");
        service.Write("exec.item.started", LogLevel.Info, LogStatus.Running, "начало", null, "WorkPanel", context.ToChild().WithItem("item-1"));
        service.Write("exec.item.succeeded", LogLevel.Info, LogStatus.Succeeded, "успех", null, "WorkPanel", context.ToChild().WithItem("item-2"));
        service.Write("exec.item.failed", LogLevel.Error, LogStatus.Failed, "сбой", null, "WorkPanel", context.ToChild().WithItem("item-3"));
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(1000);

        events.Should().HaveCount(3);
        events.Select(e => e.EventId).Should().Equal("exec.item.started", "exec.item.succeeded", "exec.item.failed");
        events.Select(e => e.Status).Should().Equal(LogStatus.Running, LogStatus.Succeeded, LogStatus.Failed);
        events.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void ReadRecentEvents_RespectsWindowWithoutMaterializingWholeFile()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.MaxRecentReadBytes = 16 * 1024);
        for (int index = 0; index < 500; index++)
        {
            service.Info(new string('я', 200) + " " + index, "WindowTest");
        }

        service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();

        IReadOnlyList<LogEvent> window = service.ReadRecentEvents(10);

        window.Should().HaveCount(10);
        window[0].Sequence.Should().BeGreaterThan(400, "чтение без полной материализации файла возвращает последние события");
        window[9].Message.Should().Contain("499");
    }

    [TestMethod]
    public void ReadRecentEvents_NonPositiveCount_ReturnsEmpty()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("событие", "ReadTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.ReadRecentEvents(0).Should().BeEmpty();
        service.ReadRecentEvents(-5).Should().BeEmpty();
    }

    [TestMethod]
    public void ReadRecentEvents_BeforeAnyEvent_ReturnsEmpty()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.ReadRecentEvents(100).Should().BeEmpty();
        service.ReadCurrentLog().Should().BeEmpty();
    }

    [TestMethod]
    public void ReadCurrentLog_ReturnsHumanReadableRedactedExport()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Write(
            "exec.item.failed",
            LogLevel.Error,
            LogStatus.Failed,
            @"Сбой элемента https://user:SENTINEL-TOKEN@host/path/file.mkv?token=SENTINEL-QUERY",
            null,
            "Eac3toRunner",
            LogContext.Empty.WithOperation("op-1").ToChild().WithItem("item-4"),
            new Dictionary<string, object?> { ["ErrorCode"] = "EXTERNAL_PROCESS_FAILED", ["ExitCode"] = 1 });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string export = service.ReadCurrentLog();

        export.Should().NotContain("SENTINEL-TOKEN");
        export.Should().NotContain("SENTINEL-QUERY");
        export.Should().Contain("ERROR");
        export.Should().Contain("exec.item.failed");
        export.Should().Contain("Eac3toRunner");
        export.Should().Contain("op=op-1");
        export.Should().Contain("item=item-4");
        export.Should().Contain("ErrorCode=EXTERNAL_PROCESS_FAILED");
        export.Should().NotContain("\"schemaVersion\"", "экспорт не является сырым JSON-протоколом");
        export.Should().NotContain("\"message\":", "экспорт не является сырым JSON-протоколом");
    }

    [TestMethod]
    public void ReadCurrentLog_OneLinePerEventForLegacyCompatibility()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        const int count = 120;
        for (int index = 0; index < count; index++)
        {
            service.Info("запись " + index, "Compat");
        }

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines = service.ReadCurrentLog().Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(count, "совместимый экспорт остаётся построчным");
        lines.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void ReadCurrentLog_ExceptionRendersTypeMessageAndStackOnOneLine()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Exception exception;
        try
        {
            throw new InvalidOperationException("причина сбоя");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        service.Exception(exception, "контекст сбоя", "Integration");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        string export = service.ReadCurrentLog();

        export.Should().Contain("контекст сбоя");
        export.Should().Contain("причина сбоя");
        export.Should().Contain("Стек вызовов");
        export.Should().Contain(nameof(ReadCurrentLog_ExceptionRendersTypeMessageAndStackOnOneLine));
        export.Should().Contain("ERROR");
        export.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
    }

    [TestMethod]
    public void ReadCurrentLog_MalformedLineWithSecrets_IsRedactedNotRaw()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("валидная запись", "Compat");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        AppendRaw(
            service.CurrentLogFile!,
            "{\"broken\":\"value\",\"path\":\"C:\\\\Users\\\\ivan\\\\secret.mkv\",\"token\":\"SENTINEL-TOKEN\"}\n");

        string export = service.ReadCurrentLog();

        export.Should().NotContain("SENTINEL-TOKEN", "битая строка также проходит redaction");
        export.Should().NotContain("ivan");
        export.Should().Contain("валидная запись");
    }

    [TestMethod]
    public void ReadCurrentLog_LegacyPlainTextLine_IsRedactedAndPreserved()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("первая новая запись", "Compat");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        AppendRaw(service.CurrentLogFile!, "2024-01-01 10:00:00 | INFO | старая запись password=SENTINEL-OLD\n");

        string export = service.ReadCurrentLog();

        export.Should().Contain("старая запись", "строки предыдущего формата остаются читаемыми");
        export.Should().NotContain("SENTINEL-OLD");
        export.Should().Contain("первая новая запись");
    }

    [TestMethod]
    public void ReadRecentEvents_ReadsAcrossRotatedSegments()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxFileBytes = 8192;
            options.RotationTriggerRatio = 0.5d;
        });
        string payload = new('я', 400);
        const int count = 20;
        for (int index = 0; index < count; index++)
        {
            service.Info(payload + " " + index, "Segments");
        }

        service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();
        string[] files = Directory.GetFiles(Path.Combine(scope.RootPath, "logs"), "ktools_*.log");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(1000);

        files.Should().HaveCountGreaterThan(1, "проверка на нескольких сегментах");
        events.Should().HaveCount(count, "события читаются по всем сегментам сессии");
        events[0].Sequence.Should().Be(1);
        events.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void ReadCurrentLog_BoundedByExportLimit()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.MaxExportLines = 20);
        for (int index = 0; index < 100; index++)
        {
            service.Info("запись " + index, "Bounded");
        }

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines = service.ReadCurrentLog().Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(20, "экспорт ограничен политикой размера");
        lines[^1].Should().Contain("99", "сохраняются последние события текущего сеанса");
    }

    [TestMethod]
    public void LogItem_FromEvent_KeepsStructuredFieldsForBinding()
    {
        LogEvent logEvent = new()
        {
            TimestampUtc = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Sequence = 52,
            SessionId = Guid.NewGuid(),
            EventId = "item.succeeded",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Source = "VideoEncodingScript",
            OperationId = "op-1",
            ItemId = "item-03",
            Message = "Видео закодировано"
        };

        LogItem item = LogItem.FromEvent(logEvent);

        item.Message.Should().Be(logEvent.ToDisplayString());
        item.Level.Should().Be(LogLevel.Info);
        item.Sequence.Should().Be(52);
        item.SessionId.Should().Be(logEvent.SessionId);
        item.EventId.Should().Be("item.succeeded");
        item.Status.Should().Be(LogStatus.Succeeded);
        item.Source.Should().Be("VideoEncodingScript");
        item.OperationId.Should().Be("op-1");
        item.ItemId.Should().Be("item-03");
        item.Event.Should().BeSameAs(logEvent);
        item.Kind.Should().Be(LogItemKind.Event);
        item.HasStructuredEvent.Should().BeTrue();
    }

    [TestMethod]
    public void LogItem_CreateMarker_IsSeparateSemanticType()
    {
        Guid session = Guid.NewGuid();

        LogItem marker = LogItem.CreateMarker("пропущено событий: 5", LogLevel.Warning, 5, session, LogEventMarkerNames.DroppedMarker);

        marker.Level.Should().Be(LogLevel.Warning);
        marker.EventId.Should().Be(LogEventMarkerNames.DroppedMarker);
        marker.SessionId.Should().Be(session);
        marker.Kind.Should().Be(LogItemKind.Marker, "маркер не смешивается с runtime-событиями");
        marker.HasStructuredEvent.Should().BeFalse();
        marker.Message.Should().Contain("5");
    }

    [TestMethod]
    public void LogItem_DefaultInstance_IsUsableForBinding()
    {
        var item = new LogItem { Message = "msg", Level = LogLevel.Warning };

        item.Message.Should().Be("msg");
        item.Level.Should().Be(LogLevel.Warning);
        item.HasStructuredEvent.Should().BeFalse();
        item.Kind.Should().Be(LogItemKind.Event);
    }

    [TestMethod]
    public void LogItem_FromEvent_NullEvent_Throws()
    {
        Action act = () => LogItem.FromEvent(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
