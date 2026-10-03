// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Тесты человекочитаемого текстового формата журнала по умолчанию (LogFileFormat.Text).
/// </summary>
[TestClass]
public class LogServiceTextFormatTests
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

    private static string[] ReadLinesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('\r'))
            .ToArray();
    }

    [TestMethod]
    public void Options_DefaultFormat_IsText()
    {
        LogServiceOptions options = new();
        options.FileFormat.Should().Be(LogFileFormat.Text, "по умолчанию формат журнала должен быть человекочитаемым текстом");
    }

    [TestMethod]
    public void Write_DefaultOptions_ProducesHumanReadableTextFile()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "app.lifecycle.startup",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Message = "Приложение успешно запущено",
            Source = "App"
        });

        service.Write(new LogEvent
        {
            EventId = "process.ffmpeg.started",
            Level = LogLevel.Info,
            Status = LogStatus.Running,
            Message = "Запуск процесса кодирования",
            Source = "DirectProcessRunner",
            OperationId = "op-1",
            ProcessId = "proc-1",
            Tool = "kt-ffmpeg",
            Pid = 1234,
            Properties = LogProps.Create("CommandLine", "\"C:\\tools\\kt-ffmpeg.exe\" -i \"video.mkv\" -c:v libx265")
                .With("Tool", "kt-ffmpeg")
                .With("Pid", 1234)
        });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().HaveCount(2);

        // Строки не должны быть сырым JSON (не начинаются с '{', разделены ' | ')
        lines.Should().AllSatisfy(line =>
        {
            line.Should().NotStartWith("{", "строка должна быть классическим текстом, а не JSON");
            line.Should().Contain(" | ");
        });

        lines[0].Should().Contain("INFO");
        lines[0].Should().Contain("App");
        lines[0].Should().Contain("app.lifecycle.startup");
        lines[0].Should().Contain("Succeeded");
        lines[0].Should().Contain("Приложение успешно запущено");

        lines[1].Should().Contain("process.ffmpeg.started");
        lines[1].Should().Contain("pid=1234");
        lines[1].Should().Contain("tool=kt-ffmpeg");
        lines[1].Should().Contain("CommandLine=");
        lines[1].Should().Contain("-c:v libx265");
    }

    [TestMethod]
    public void Write_SettingChange_ContainsReadableValueAndMessage()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "settings.key.updated",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Message = "Параметр 'Encoder' сохранён в настройках: 'libx265'",
            Source = "SettingsManager",
            Properties = LogProps.Create("Key", "Encoder")
                .With("Value", "libx265")
        });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string[] lines = ReadLinesShared(service.CurrentLogFile!);
        lines.Should().ContainSingle();

        string line = lines[0];
        line.Should().Contain("Параметр 'Encoder' сохранён в настройках: 'libx265'");
        line.Should().Contain("Key=Encoder");
        line.Should().Contain("Value=libx265");
    }

    [TestMethod]
    public void ReadRecentEvents_FromTextLogFile_CorrectlyParsesEvents()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "process.ffmpeg.completed",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Message = "Кодирование успешно завершено",
            Source = "ProcessRunner",
            OperationId = "op-100",
            ItemId = "item-200",
            ProcessId = "proc-300",
            Tool = "kt-ffmpeg",
            Properties = LogProps.Create("ExitCode", 0)
                .With("DurationMs", 4500)
                .With("CommandLine", "kt-ffmpeg.exe -i in.mkv out.mkv")
        });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        IReadOnlyList<LogEvent> recentEvents = service.ReadRecentEvents(10);
        recentEvents.Should().ContainSingle();

        LogEvent ev = recentEvents[0];
        ev.EventId.Should().Be("process.ffmpeg.completed");
        ev.Level.Should().Be(LogLevel.Info);
        ev.Status.Should().Be(LogStatus.Succeeded);
        ev.Source.Should().Be("ProcessRunner");
        ev.Message.Should().Be("Кодирование успешно завершено");
        ev.OperationId.Should().Be("op-100");
        ev.ItemId.Should().Be("item-200");
        ev.ProcessId.Should().Be("proc-300");
        ev.Tool.Should().Be("kt-ffmpeg");
        ev.Properties.Should().NotBeNull();
        ev.Properties.Should().ContainKey("CommandLine");
    }

    [TestMethod]
    public void ReadCurrentLog_ReturnsRenderedLinesForTextFormat()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = "test.event.one",
            Level = LogLevel.Info,
            Status = LogStatus.Succeeded,
            Message = "Первое тестовое сообщение",
            Source = "Tester"
        });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string logText = service.ReadCurrentLog();
        logText.Should().Contain("INFO");
        logText.Should().Contain("Первое тестовое сообщение");
    }
}
