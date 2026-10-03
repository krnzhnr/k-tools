// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogServiceJsonlTests
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

    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string[] ReadLinesShared(string path) =>
        ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void AppendRaw(string path, string content)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
    }

    [TestMethod]
    public void Write_ManyEvents_ProduceOnePhysicalLinePerEvent()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        const int count = 250;

        for (int index = 0; index < count; index++)
        {
            service.Info("событие " + index, "JsonlTest");
        }

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(count, "одно событие — одна физическая строка");
        lines.Should().OnlyContain(line => line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal));
        lines.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void Write_MultilineMessage_StaysOnSinglePhysicalLine()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string multiline = "первая строка\nвторая строка\r\nтретья строка\tс табуляцией";

        service.Error(multiline, "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(1, "переводы строк внутри сообщения экранируются сериализатором");
        lines[0].Should().Contain("\\n");
        lines[0].Should().Contain("\\r\\n");
        lines[0].Should().Contain("\\t");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].Message.Should().Be(multiline, "после разбора multiline восстанавливается полностью");
    }

    [TestMethod]
    public void Write_LargeBoundedEvent_RoundTripsThroughRecentReader()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxMessageLength = 128 * 1024;
            options.MaxPropertiesLength = 128 * 1024;
            options.MaxDetailLength = 128 * 1024;
            options.MaxQueuedBytes = 4 * 1024 * 1024;
        });
        string message = new('m', 100_000);

        service.Write(new LogEvent
        {
            EventId = "app.large",
            Level = LogLevel.Info,
            Source = "JsonlTest",
            Message = message,
            Properties = new Dictionary<string, object?> { ["OutputTail"] = new string('p', 100_000) }
        });
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        LogEvent result = service.ReadRecentEvents(1).Should().ContainSingle().Subject;

        result.EventId.Should().Be("app.large");
        result.Message.Should().Be(message);
        Encoding.UTF8.GetByteCount((string)result.Properties["OutputTail"]!).Should().Be(100_000);
    }

    [TestMethod]
    public void Write_SingleLineOverMaxFileBytes_WritesOnlyBoundedRejection()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxFileBytes = 4096;
            options.RotationTriggerRatio = 0.99d;
        });

        service.Write("app.oversized", LogLevel.Info, new string('я', 3000), "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().ContainSingle();
        new FileInfo(service.CurrentLogFile!).Length.Should().BeLessThanOrEqualTo(4096);
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        document.RootElement.GetProperty("eventId").GetString().Should().Be(LogEventMarkerNames.RejectedEvent);
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("LINE_TOO_LARGE");
        lines[0].Should().NotContain("app.oversized");
    }

    [TestMethod]
    public void Write_LogInjectionAttempt_StaysInsideJsonString()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string injection = "{\"schemaVersion\":999,\"eventId\":\"forged.event\",\"message\":\"подделка\"}\n{\"schemaVersion\":1}";

        service.Info(injection, "JsonlTest");
        service.Info("обычное событие", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(2, "инъекция не создаёт дополнительных физических строк");

        using JsonDocument forged = JsonDocument.Parse(lines[0]);
        forged.RootElement.GetProperty("eventId").GetString().Should().Be("legacy.jsonltest.info", "поддельное событие не интерпретируется как протокол");
        forged.RootElement.GetProperty("message").GetString().Should().Be(injection);

        using JsonDocument real = JsonDocument.Parse(lines[1]);
        real.RootElement.GetProperty("message").GetString().Should().Be("обычное событие");
    }

    [TestMethod]
    public void Write_EventEnvelope_ContainsRequiredSchemaFields()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Guid session = service.SessionId;

        service.Write(new LogEvent
        {
            EventId = "process.exit",
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "MediaDownloader",
            Message = "yt-dlp завершился с ошибкой",
            OperationId = "op-1",
            ItemId = "item-7",
            ProcessId = "process-07",
            Pid = 4321,
            Attempt = 2,
            Tool = "yt-dlp",
            ExitCode = 1,
            DurationMs = 4000d,
            Properties = new Dictionary<string, object?> { ["Tool"] = "yt-dlp", ["ExitCode"] = 1 }
        });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(1);
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(LogService.SchemaVersion);
        root.GetProperty("channel").GetString().Should().Be(LogService.ApplicationChannel);
        root.GetProperty("app").GetString().Should().Be(LogService.ApplicationName);
        root.GetProperty("appVersion").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("session").GetString().Should().Be(session.ToString("D"));
        root.GetProperty("timestampUtc").GetString().Should().MatchRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$");
        root.GetProperty("sequence").GetInt64().Should().Be(1);
        root.GetProperty("eventId").GetString().Should().Be("process.exit");
        root.GetProperty("level").GetString().Should().Be("Error");
        root.GetProperty("status").GetString().Should().Be("Failed");
        root.GetProperty("source").GetString().Should().Be("MediaDownloader");
        root.GetProperty("message").GetString().Should().Be("yt-dlp завершился с ошибкой");
        root.GetProperty("properties").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("operation").GetString().Should().Be("op-1");
        root.GetProperty("item").GetString().Should().Be("item-7");
        root.GetProperty("process").GetString().Should().Be("process-07");
        root.GetProperty("attempt").GetInt32().Should().Be(2);
        root.GetProperty("properties").GetProperty("Tool").GetString().Should().Be("yt-dlp");
        root.GetProperty("properties").GetProperty("ExitCode").GetInt32().Should().Be(1);
        root.TryGetProperty("exception", out _).Should().BeTrue();
    }

    [TestMethod]
    public void Write_ProcessLifecycle_RoundTripsOsPidAndExitCode()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "process.started",
            Level = LogLevel.Info,
            Status = LogStatus.Running,
            Source = "MediaDownloader",
            Message = "Запущен yt-dlp",
            OperationId = "op-1",
            ProcessId = "process-07",
            Pid = 1234,
            Tool = "yt-dlp",
            ToolVersion = "2026.09.25",
            Properties = new Dictionary<string, object?> { ["ArgumentCount"] = 8 }
        });
        service.Write(new LogEvent
        {
            EventId = "process.exit",
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "MediaDownloader",
            Message = "yt-dlp завершился с ошибкой",
            OperationId = "op-1",
            ProcessId = "process-07",
            Pid = 1234,
            ExitCode = 1,
            DurationMs = 4000d,
            Tool = "yt-dlp",
            ErrorCode = "EXTERNAL_PROCESS_FAILED",
            Output = "ERROR: [redacted]"
        });
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(2);
        events[0].Pid.Should().Be(1234);
        events[0].ToolVersion.Should().Be("2026.09.25");
        events[0].ExitCode.Should().BeNull("у события старта нет кода завершения");
        events[1].ProcessId.Should().Be(events[0].ProcessId, "внутренний идентификатор связывает запуск и выход");
        events[1].Pid.Should().Be(events[0].Pid, "OS Pid не подменяется внутренним идентификатором");
        events[1].ExitCode.Should().Be(1);
        events[1].DurationMs.Should().Be(4000d);
        events[1].ErrorCode.Should().Be("EXTERNAL_PROCESS_FAILED");
    }

    [TestMethod]
    public void ReadRecentEvents_LatestLineMalformed_BackfillsOlderValidLines()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Write("app.first", LogLevel.Info, "первое", "JsonlTest");
        service.Write("app.second", LogLevel.Info, "второе", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        AppendRaw(service.CurrentLogFile!, "{\"schemaVersion\":1,\"channel\":\"app_diag\",\"session\":\"broken\"\n");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);

        events.Should().HaveCount(2, "битая последняя строка не скрывает валидные предыдущие");
        events.Select(e => e.EventId).Should().Equal("app.first", "app.second");
    }

    [TestMethod]
    public void ReadRecentEvents_ConditionallyInvalidLine_IsRejectedAndBackfillsOlderValidLines()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Write("app.first", LogLevel.Info, "первое", "JsonlTest");
        service.Write("app.second", LogLevel.Info, "второе", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] valid = ReadLinesShared(service.CurrentLogFile!);
        using JsonDocument template = JsonDocument.Parse(valid[0]);
        string session = template.RootElement.GetProperty("session").GetString()!;
        long sequence = template.RootElement.GetProperty("sequence").GetInt64();
        AppendRaw(service.CurrentLogFile!, BuildConditionalLine(session, sequence + 1) + "\n");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);

        events.Select(e => e.EventId).Should().Equal("app.first", "app.second");
    }

    [TestMethod]
    public void ReadRecentEvents_UnknownSchemaChannelOrEnum_IsRejected()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Write("app.valid", LogLevel.Info, "валидное", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] valid = ReadLinesShared(service.CurrentLogFile!);
        using JsonDocument template = JsonDocument.Parse(valid[0]);
        string session = template.RootElement.GetProperty("session").GetString()!;
        long sequence = template.RootElement.GetProperty("sequence").GetInt64();

        AppendRaw(service.CurrentLogFile!, string.Join("\n", new[]
        {
            BuildLine(session, sequence + 1, 2, "app_diag", "Info", "app.wrong.level", "уровень", "S"),
            BuildLine(session, sequence + 2, 1, "other_channel", "Info", "app.wrong.channel", "канал", "S"),
            BuildLine(session, sequence + 3, 1, "app_diag", "Info", "has space", "пробел в id", "S"),
            BuildLine(session, sequence + 4, 1, "app_diag", "Info", "app.ok.level", "хороший уровень", "S"),
            "{ not json at all",
            BuildLine(Guid.Empty.ToString("D"), sequence + 5, 1, "app_diag", "Info", "app.empty.session", "сессия", "S"),
            BuildLine(session, 0, 1, "app_diag", "Info", "app.zero.sequence", "нулевая последовательность", "S", sequenceOverride: 0)
        }) + "\n");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(20);

        events.Select(e => e.EventId).Should().Equal("app.valid", "app.ok.level");
    }

    private static string BuildConditionalLine(string session, long sequence)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["channel"] = LogService.ApplicationChannel,
            ["app"] = LogService.ApplicationName,
            ["appVersion"] = "test",
            ["session"] = session,
            ["timestampUtc"] = "2026-09-25T12:00:00.000Z",
            ["sequence"] = sequence,
            ["eventId"] = "process.started",
            ["level"] = "Info",
            ["status"] = "Running",
            ["source"] = "JsonlTest",
            ["message"] = "невалидный process без tool",
            ["properties"] = new Dictionary<string, object?>(),
            ["exception"] = null,
            ["process"] = "process-1",
            ["pid"] = 1
        });
    }

    private static string BuildLine(
        string session,
        long sequence,
        int schemaVersion,
        string channel,
        string level,
        string eventId,
        string message,
        string source,
        long? sequenceOverride = null)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["channel"] = channel,
            ["app"] = LogService.ApplicationName,
            ["appVersion"] = "test",
            ["session"] = session,
            ["timestampUtc"] = "2026-09-25T12:00:00.000Z",
            ["sequence"] = sequenceOverride ?? sequence,
            ["eventId"] = eventId,
            ["level"] = level,
            ["status"] = "Succeeded",
            ["source"] = source,
            ["message"] = message,
            ["properties"] = new Dictionary<string, object?>(),
            ["exception"] = null
        });
    }

    [TestMethod]
    public void ReadRecentEvents_SecretInSourceOrEventIdOnDisk_IsRedactedOnRead()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Write("app.valid", LogLevel.Info, "валидное", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] valid = ReadLinesShared(service.CurrentLogFile!);
        using JsonDocument template = JsonDocument.Parse(valid[0]);
        string session = template.RootElement.GetProperty("session").GetString()!;
        long sequence = template.RootElement.GetProperty("sequence").GetInt64();

        AppendRaw(service.CurrentLogFile!, BuildLine(session, sequence + 1, 1, "app_diag", "Info", @"C:\Users\ivan\secret.exe", "секретный источник", @"\\.\PhysicalDrive0") + "\n");

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(20);

        events.Should().HaveCount(1, "событие с небезопасным источником отбрасывается при чтении");
        events[0].EventId.Should().Be("app.valid");
        service.ReadCurrentLog().Should().NotContain("ivan");
    }

    [TestMethod]
    public void Write_FileHasNoByteOrderMark()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Info("проверка кодировки", "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        using FileStream stream = new(service.CurrentLogFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] header = new byte[3];
        int read = stream.Read(header, 0, 3);
        read.Should().Be(3);
        (header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF).Should().BeFalse("BOM не должен попадать в JSONL-файл");
    }

    [TestMethod]
    public void Write_ControlCharactersInMessage_AreEscapedBySerializer()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string message = "первая" + (char)0 + "строка" + (char)7 + "вторая";

        service.Warn(message, "JsonlTest");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(1);
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        string stored = document.RootElement.GetProperty("message").GetString()!;
        stored.Should().NotContain(((char)0).ToString());
        stored.Should().NotContain(((char)7).ToString());
        stored.Should().Contain("первая");
    }

    [TestMethod]
    public void Write_LargeAllowedProperties_AreSerializedAndBounded()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        var properties = new Dictionary<string, object?>
        {
            ["OutputTail"] = new string('я', 1000),
            ["Fingerprint"] = new string('d', 200),
            ["Reason"] = new string('e', 8000)
        };

        service.Write("app.large.properties", LogLevel.Warning, "большие свойства", "JsonlTest", LogStatus.Failed, null, properties);
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1);
        events[0].Properties["OutputTail"].Should().Be(new string('я', 1000), "allowlisted значение в пределах лимита сохраняется полностью");
        events[0].Properties["Fingerprint"].Should().Be(new string('d', 200));
        string reason = Convert.ToString(events[0].Properties["Reason"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        Encoding.UTF8.GetByteCount(reason).Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxMessageLength);
        reason.Should().Contain(LogRedactor.TruncationMarker, "превышение лимита помечается маркером усечения");
    }
}
