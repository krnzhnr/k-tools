// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.ProcessLayer;

[TestClass]
public class PrivacySentinelCorpusTests
{
    private const string QueryTokenMarker = "SENTINEL-QUERY-TOKEN";
    private const string CookieMarker = "SENTINEL-COOKIE-VALUE";
    private const string AdditionalArgsMarker = "SENTINEL-ADDITIONAL-ARGS";
    private const string PathMarker = "SENTINEL-USER-42";
    private const string AuthorizationMarker = "SENTINEL-AUTHZ-VALUE";
    private const string PasswordMarker = "SENTINEL-PASSWORD";

    private const string QueryUrl = "https://sentinel.example.com/watch?v=" + QueryTokenMarker;
    private const string AuthorizationHeader = "Authorization: Bearer " + AuthorizationMarker;
    private const string CookieHeader = "Cookie: session=" + CookieMarker;
    private const string PasswordAssignment = "password=" + PasswordMarker;
    private const string UserPath = @"C:\Users\" + PathMarker + @"\Videos\movie.mkv";

    private static readonly string[] SecretMarkers =
    {
        QueryTokenMarker,
        CookieMarker,
        AdditionalArgsMarker,
        PathMarker,
        AuthorizationMarker,
        PasswordMarker
    };

    private static LogService CreateService(TempDirectoryScope scope, string? logSubDirectory = "logs")
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, logSubDirectory!),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000
        };
        return new LogService(options);
    }

    private static string ReadAllShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AssertNoSecrets(string payload, string channel)
    {
        foreach (string marker in SecretMarkers)
        {
            payload.Should().NotContain(marker, "канал '{0}' не должен содержать маркер '{1}'", channel, marker);
        }
    }

    [TestMethod]
    public void ApplicationLog_SecretShapedPayloads_AreRedactedBeforeWrite()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Info($"Загрузка ссылки {QueryUrl}", "Privacy");
        service.Warn($"{AuthorizationHeader}; {CookieHeader}", "Privacy");
        service.Error($"Файл пользователя {UserPath} и аргументы {PasswordAssignment}", "Privacy");
        service.Exception(
            new InvalidOperationException($"доступ запрещён: {PasswordAssignment}"),
            "Ошибка аутентификации",
            "Privacy");

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        AssertNoSecrets(jsonl, "app_diag");
        jsonl.Should().Contain("sentinel.example.com", "безопасный host остаётся диагностически полезным");
        jsonl.Should().Contain("movie.mkv", "имя файла остаётся диагностически полезным");
    }

    [TestMethod]
    public void ApplicationLog_SecretProperties_AreDroppedByAllowlist()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(
            "privacy.property_allowlist",
            LogLevel.Warning,
            "Проверка allowlist",
            "Privacy",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Password"] = PasswordMarker,
                ["ApiKey"] = AuthorizationMarker,
                ["Cookie"] = CookieMarker,
                ["Reason"] = "безопасное значение",
                ["ValueHash"] = "sha256:0123456789abcdef"
            });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        AssertNoSecrets(jsonl, "app_diag");
        jsonl.Should().Contain("безопасное значение", "allowlisted свойство сохраняется");
        jsonl.Should().Contain(LogRedactor.RedactionReasonProperty);
    }

    [TestMethod]
    public void ApplicationLog_NonAllowlistedPropertyKeys_AreNeverSerialized()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(
            "privacy.unlisted_property",
            LogLevel.Info,
            "Неизвестное свойство",
            "Privacy",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["MyCustomPayload"] = QueryUrl,
                ["Reason"] = "безопасное значение"
            });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        jsonl.Should().NotContain("MyCustomPayload", "неизвестный ключ свойства отбрасывается");
        jsonl.Should().Contain(LogRedactor.MarkedPropertiesProperty);
    }

    [TestMethod]
    public async Task ProcessLayer_CommandWithSecrets_LeavesNoMarkerInJsonl()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        var runner = new DirectProcessRunner(service);
        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        ProcessExecutionContext context = ProcessExecutionContext.Create("cmd", "op-privacy", "item-privacy");

        ProcessResult result = await runner.RunAsync(
            comSpec,
            "cmd",
            "/c echo " + PasswordAssignment + " & echo " + CookieHeader + " & echo " + UserPath + " & exit 4",
            context,
            workingDir: scope.RootPath);

        result.IsSuccess.Should().BeFalse();
        result.ExitCode.Should().Be(4);

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        AssertNoSecrets(jsonl, "app_diag");
        jsonl.Should().Contain("process.started");
        jsonl.Should().Contain("process.exit");
        jsonl.Should().Contain("OutputTail", "хвост вывода остаётся диагностически полезным после редактирования");
    }

    [TestMethod]
    public async Task ProcessLayer_UrlWithCredentials_IsReducedToSafeHost()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        var runner = new DirectProcessRunner(service);
        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        string url = "https://user:" + PasswordMarker + "@sentinel.example.com/watch?v=" + QueryTokenMarker;

        await runner.RunAsync(
            comSpec,
            "cmd",
            "/c echo " + url,
            ProcessExecutionContext.NewOperation("yt-dlp"),
            workingDir: scope.RootPath);

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        AssertNoSecrets(jsonl, "app_diag");
        jsonl.Should().Contain("sentinel.example.com", "безопасный host остаётся диагностически полезным");
    }

    [TestMethod]
    public void ProcessOutputBuffer_RedactsEveryStreamKind()
    {
        foreach (string stream in new[] { "stdout", "stderr" })
        {
            ProcessOutputBuffer buffer = new() { Stream = stream };
            buffer.Append("see " + QueryUrl);
            buffer.Append(AuthorizationHeader);
            buffer.Append(CookieHeader);
            buffer.Append(UserPath);

            string tail = buffer.BuildTail();

            AssertNoSecrets(tail, stream);
            tail.Should().Contain("sentinel.example.com", "поток {0}: host сохраняется", stream);
            tail.Should().Contain("movie.mkv", "поток {0}: имя файла сохраняется", stream);
        }
    }

    [TestMethod]
    public void ExecutionJournal_LineIsRedactedBeforeAppend()
    {
        ProcessOutputBuffer buffer = new();
        buffer.Append("[stderr] error at " + QueryUrl);
        buffer.Append("[stderr] " + AuthorizationHeader);

        string tail = buffer.BuildTail();

        AssertNoSecrets(tail, "execution journal");
        tail.Should().NotContain("?v=");
    }

    [TestMethod]
    public void UiExport_ContainsNoSecretMarkers()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Info($"Загрузка {QueryUrl}", "Privacy");
        service.Warn(CookieHeader, "Privacy");
        service.Error($"Файл {UserPath}", "Privacy");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string export = service.ReadCurrentLog();

        export.Should().NotBeNullOrWhiteSpace();
        AssertNoSecrets(export, "ui export");
    }

    [TestMethod]
    public void CrashEvent_ExceptionMessageIsRedacted()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(
            "privacy.exception",
            LogLevel.Error,
            LogStatus.Failed,
            "Ошибка доступа к удалённому ресурсу",
            new UnauthorizedAccessException(PasswordAssignment + " path=" + UserPath),
            "Privacy");

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        AssertNoSecrets(jsonl, "app_diag");
        jsonl.Should().Contain("UnauthorizedAccessException", "тип исключения остаётся диагностически полезным");
    }

    [TestMethod]
    public void EmergencyChannel_DoesNotReceiveSecretMarkers()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "unwritable-logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            WriteFileEvents = false,
            FlushIntervalMilliseconds = 60000
        };

        using (LogService service = new(options))
        {
            service.Info("Загрузка " + QueryUrl, "Privacy");
            service.Error("сбой " + PasswordAssignment, "Privacy");
            service.Flush(TimeSpan.FromSeconds(30));
        }

        string emergency = string.Empty;
        if (Directory.Exists(options.EmergencyDirectory!))
        {
            foreach (string file in Directory.GetFiles(options.EmergencyDirectory!, "*", SearchOption.AllDirectories))
            {
                try
                {
                    emergency += ReadAllShared(file);
                }
                catch (IOException)
                {
                }
            }
        }

        AssertNoSecrets(emergency, "emergency");
    }

    [TestMethod]
    public void FullPaths_AreReducedToFileNameAcrossChannels()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Info($"Файл для обработки: {UserPath}", "Privacy");
        service.Write(
            "privacy.artifact",
            LogLevel.Info,
            "Артефакт",
            "Privacy",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["FileName"] = "movie.mkv",
                ["OutputName"] = "result.mkv"
            });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        string jsonl = ReadAllShared(service.CurrentLogFile!);

        jsonl.Should().NotContain(PathMarker);
        jsonl.Should().Contain("movie.mkv");
    }

    [TestMethod]
    public void StructuredProcessEvent_RoundTripsThroughReadRecentEvents()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        service.Write(new LogEvent
        {
            EventId = ProcessEventIds.Exit,
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "yt-dlp",
            Message = "Внешний процесс завершился с ошибкой",
            OperationId = "op-roundtrip",
            ItemId = "item-roundtrip",
            ProcessId = "proc-roundtrip",
            Pid = 4242,
            Attempt = 2,
            Tool = "yt-dlp",
            ExitCode = 2,
            DurationMs = 1234.5,
            ErrorCode = "process-exit",
            Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ExitCode"] = 2,
                ["DurationMs"] = 1234.5,
                ["OutputExists"] = false,
                ["WarningCount"] = 1,
                ["CommandHash"] = "abc123",
                ["ArgumentCount"] = 7,
                ["WorkingDirLabel"] = "wd-abc",
                ["HostLabel"] = "sentinel.example.com"
            }
        });

        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(20);

        LogEvent exited = events.First(e => e.EventId == ProcessEventIds.Exit);
        exited.Level.Should().Be(LogLevel.Error);
        exited.Status.Should().Be(LogStatus.Failed);
        exited.OperationId.Should().Be("op-roundtrip");
        exited.ItemId.Should().Be("item-roundtrip");
        exited.ProcessId.Should().Be("proc-roundtrip");
        exited.Pid.Should().Be(4242, "PID заполняется при старте процесса");
        exited.Attempt.Should().Be(2);
        exited.Tool.Should().Be("yt-dlp");
        exited.ExitCode.Should().Be(2);
        exited.DurationMs.Should().Be(1234.5);
        exited.ErrorCode.Should().Be("process-exit");
        exited.Properties.Should().ContainKey("CommandHash");
        exited.Properties.Should().ContainKey("WorkingDirLabel");
        exited.Properties.Should().ContainKey("HostLabel");
    }
}
