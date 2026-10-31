// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class EmergencyLogSinkTests
{
    private static EmergencyLogSink CreateSink(TempDirectoryScope scope, Action<LogServiceOptions>? configure = null)
    {
        LogServiceOptions options = new()
        {
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency")
        };
        configure?.Invoke(options);
        return new EmergencyLogSink(Path.Combine(scope.RootPath, "emergency"), options);
    }

    [TestMethod]
    public void EmergencyLogSink_HasNoDependencyOnLogService()
    {
        Type sinkType = typeof(EmergencyLogSink);

        var memberTypes = sinkType
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(member => member switch
            {
                FieldInfo field => new[] { field.FieldType },
                PropertyInfo property => new[] { property.PropertyType },
                MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
                ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
                _ => Array.Empty<Type>()
            })
            .Distinct()
            .ToList();

        sinkType.GetInterfaces().Should().NotContain(typeof(ILogService));
        memberTypes.Should().NotContain(typeof(ILogService));
        memberTypes.Should().NotContain(typeof(LogService));
    }

    [TestMethod]
    public void Write_CrashBeforeInitialization_CreatesBoundedRedactedRecord()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        Guid session = Guid.NewGuid();

        bool written = sink.Write(
            "Необработанное исключение в UI: https://user:SENTINEL-PASS@host/f.mkv?token=SENTINEL-TOKEN",
            new InvalidOperationException(@"сбой в C:\Users\ivan\file.mkv с password=SENTINEL-PASSWORD"),
            "App.UnhandledException",
            "crash-0001",
            session,
            LogLevel.Fatal);
        sink.Write("вторая запись", null, "App", "crash-0002", session, LogLevel.Error);

        written.Should().BeTrue("аварийная запись работает до инициализации основного логгера");
        string[] files = Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern);
        files.Should().HaveCount(2, "каждый CrashId даёт ровно одну копию отчёта");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(files.Single(file => Path.GetFileName(file).Contains("crash-0001", StringComparison.Ordinal))));
        JsonElement root = document.RootElement;
        root.GetProperty("channel").GetString().Should().Be("crash_emergency");
        root.GetProperty("crashId").GetString().Should().Be("crash-0001");
        root.GetProperty("session").GetString().Should().Be(session.ToString("D"));
        root.GetProperty("source").GetString().Should().Be("App.UnhandledException");
        root.GetProperty("level").GetString().Should().Be("Fatal");
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();
        root.GetProperty("timestampUtc").GetString().Should().MatchRegex("^\\d{4}-\\d{2}-\\d{2}T.*Z$");
        root.GetProperty("reason").GetString().Should().NotContain("SENTINEL-TOKEN");
        root.GetProperty("exception").GetProperty("message").GetString().Should().NotContain("SENTINEL-PASSWORD");
        root.GetProperty("exception").GetProperty("hresult").GetInt32().Should().NotBe(0);
    }

    [TestMethod]
    public void Write_SameCrashIdTwice_DoesNotDuplicateCrashReport()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);

        bool first = sink.Write("первый вызов", null, "App", "crash-dup", Guid.NewGuid());
        bool second = sink.Write("повторный вызов", null, "App", "crash-dup", Guid.NewGuid());

        first.Should().BeTrue();
        second.Should().BeFalse("повторная запись того же CrashId не создаёт вторую копию");
        Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern).Should().HaveCount(1);
        sink.WrittenCount.Should().Be(1);
        sink.DroppedCount.Should().Be(1);
    }

    [TestMethod]
    public void BuildPayload_MaxFileBytesTooSmall_StillProducesValidJson()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options => options.MaxEmergencyFileBytes = 1024);
        Exception exception;
        try
        {
            throw new InvalidOperationException(new string('z', 100_000));
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        string payload = sink.BuildPayload(new string('r', 100_000), exception, "App", "crash-big", Guid.NewGuid(), LogLevel.Fatal)!;

        Encoding.UTF8.GetByteCount(payload).Should().BeLessThanOrEqualTo(1024);
        Action parse = () => JsonDocument.Parse(payload);
        parse.Should().NotThrow("JSON аварийного канала остаётся валидным при любом лимите");

        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue("превышение лимита помечается флагом truncated");
        document.RootElement.GetProperty("crashId").GetString().Should().Be("crash-big");
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [TestMethod]
    public void BuildPayload_DeepChain_SetsInnerTruncatedFlag()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options =>
        {
            options.EmergencyMaxInnerDepth = 1;
            options.MaxEmergencyFileBytes = 32 * 1024;
        });
        Exception exception = new InvalidOperationException(
            "верхний",
            new ArgumentException("средний", new FormatException("внутренний")));

        string payload = sink.BuildPayload("причина", exception, "App", "crash-inner-flag", Guid.NewGuid(), LogLevel.Error)!;

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement exceptionElement = document.RootElement.GetProperty("exception");
        exceptionElement.GetProperty("inner").GetArrayLength().Should().Be(1);
        exceptionElement.GetProperty("innerTruncated").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void BuildPayload_NullFields_ProducesBoundedValidJson()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options => options.MaxEmergencyFileBytes = 1024);

        string payload = sink.BuildPayload(null, null, null, null, Guid.NewGuid(), LogLevel.Error)!;

        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("crashId").GetString().Should().Be(LogRedactor.UnknownIdentifier);
        document.RootElement.GetProperty("source").GetString().Should().Be(LogRedactor.UnknownIdentifier);
        document.RootElement.GetProperty("exception").ValueKind.Should().Be(JsonValueKind.Null);
        Encoding.UTF8.GetByteCount(payload).Should().BeLessThanOrEqualTo(1024);
    }

    [TestMethod]
    public void BuildPayload_LongEnvelopeFields_AreBounded()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options => options.MaxEmergencyFileBytes = 4096);

        string payload = sink.BuildPayload(
            new string('r', 100_000),
            null,
            new string('s', 10_000),
            new string('c', 10_000),
            Guid.NewGuid(),
            LogLevel.Error)!;

        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("crashId").GetString()!.Length.Should().BeLessThanOrEqualTo(64);
        document.RootElement.GetProperty("source").GetString()!.Length.Should().BeLessThanOrEqualTo(128);
        Encoding.UTF8.GetByteCount(payload).Should().BeLessThanOrEqualTo(4096);
    }

    [TestMethod]
    public void BuildPayload_ExceptionWithDeepChain_BoundsInnerChain()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options =>
        {
            options.EmergencyMaxInnerDepth = 2;
            options.MaxEmergencyFileBytes = 32 * 1024;
        });
        Exception current = new FormatException("корень");
        for (int index = 0; index < 10; index++)
        {
            current = new InvalidOperationException("уровень " + index, current);
        }

        string payload = sink.BuildPayload("причина", current, "App", "crash-chain", Guid.NewGuid(), LogLevel.Error)!;

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement exception = document.RootElement.GetProperty("exception");
        exception.GetProperty("type").GetString().Should().Be("System.InvalidOperationException");
        exception.GetProperty("inner").GetArrayLength().Should().Be(2, "внутренняя цепочка ограничена бюджетом");
    }

    [TestMethod]
    public void BuildPayload_ExceptionText_IsOwnedOnceByStructuredExceptionNode()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        Exception exception;
        try
        {
            throw new InvalidOperationException("наружная-причина", new ArgumentException("внутренняя-причина"));
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        string payload = sink.BuildPayload(
            "Необработанное исключение в обработчике",
            exception,
            "App.UnhandledException",
            "crash-owner",
            Guid.NewGuid(),
            LogLevel.Fatal)!;

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        root.GetProperty("reason").GetString().Should().NotContain("наружная-причина");
        JsonElement node = root.GetProperty("exception");
        node.GetProperty("type").GetString().Should().Be(typeof(InvalidOperationException).FullName);
        node.GetProperty("message").GetString().Should().Be("наружная-причина");
        node.GetProperty("stack").GetString().Should().NotBeNullOrEmpty();
        node.GetProperty("hresult").GetInt32().Should().NotBe(0);
        JsonElement inner = node.GetProperty("inner")[0];
        inner.GetProperty("type").GetString().Should().Be(typeof(ArgumentException).FullName);
        inner.GetProperty("message").GetString().Should().Be("внутренняя-причина");

        CountOccurrences(payload, "наружная-причина").Should().Be(
            1,
            "текст исключения присутствует в bounded crash trail ровно один раз — в типизированном узле");
        CountOccurrences(payload, "внутренняя-причина").Should().Be(
            1,
            "сообщение inner-исключения не дублируется в reason или где-либо ещё");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [TestMethod]
    public void Write_DistinctCrashIds_InSameSecondCreateDistinctFiles()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);

        sink.Write("первый", null, "App", "crash-000000000001", Guid.NewGuid()).Should().BeTrue();
        sink.Write("второй", null, "App", "crash-000000000002", Guid.NewGuid()).Should().BeTrue();

        Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern)
            .Should().HaveCount(2);
    }

    [TestMethod]
    public void Write_RecordSizeStaysUnderConfiguredBound()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options => options.MaxEmergencyFileBytes = 4096);
        Exception exception;
        try
        {
            throw new InvalidOperationException(new string('z', 100_000));
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        sink.Write(new string('r', 100_000), exception, "App", "crash-size", Guid.NewGuid());

        string[] files = Directory.GetFiles(Path.Combine(scope.RootPath, "emergency"), EmergencyLogSink.FileSearchPattern);
        files.Should().HaveCount(1);
        new FileInfo(files[0]).Length.Should().BeLessThanOrEqualTo(4096);
    }

    [TestMethod]
    public void Write_UnavailableDirectory_ReturnsFalseWithoutThrowing()
    {
        using var scope = new TempDirectoryScope();
        string blocked = scope.GetFullPath("blocked");
        File.WriteAllText(blocked, "file-instead-of-directory");
        var sink = new EmergencyLogSink(Path.Combine(blocked, "nested"), new LogServiceOptions());

        Action act = () => sink.Write("причина", null, "App", Guid.NewGuid().ToString("N"), Guid.NewGuid());

        act.Should().NotThrow("отказ аварийного канала не должен бросать наружу");
        sink.DroppedCount.Should().BeGreaterThan(0, "отказ измеряется счётчиком");
    }

    [TestMethod]
    public void Write_EmptyCrashId_IsRejected()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);

        bool written = sink.Write("причина", null, "App", "   ", Guid.NewGuid());

        written.Should().BeFalse();
        sink.DroppedCount.Should().Be(1);
    }

    [TestMethod]
    public void Retention_KeepsAtMostConfiguredFiles()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope, options => options.MaxEmergencyFiles = 3);
        string directory = Path.Combine(scope.RootPath, "emergency");

        for (int index = 0; index < 10; index++)
        {
            sink.Write("сбой " + index, null, "App", "crash-" + index.ToString("D2", CultureInfo.InvariantCulture), Guid.NewGuid());
            System.Threading.Thread.Sleep(5);
        }

        Directory.GetFiles(directory, EmergencyLogSink.FileSearchPattern).Should().HaveCountLessThanOrEqualTo(3);
    }

    [TestMethod]
    public void Retention_RemovesFilesOlderThanConfiguredDays()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "emergency");
        Directory.CreateDirectory(directory);
        EmergencyLogSink sink = CreateSink(scope, options => options.EmergencyRetentionDays = 1);
        string old = Path.Combine(directory, EmergencyLogSink.FileNamePrefix + "old" + EmergencyLogSink.FileNameExtension);
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-45));

        sink.Write("актуальный сбой", null, "App", "crash-new", Guid.NewGuid());

        File.Exists(old).Should().BeFalse("устаревшие аварийные отчёты удаляются");
        Directory.GetFiles(directory, EmergencyLogSink.FileSearchPattern).Should().HaveCount(1);
    }

    [TestMethod]
    public void BuildPayload_ProducesSingleLineValidJson()
    {
        using var scope = new TempDirectoryScope();
        EmergencyLogSink sink = CreateSink(scope);
        Exception exception;
        try
        {
            throw new InvalidOperationException("многострочный\nсбой\r\nвторая строка");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        string payload = sink.BuildPayload("причина\nвторая строка", exception, "App", "crash-json", Guid.NewGuid(), LogLevel.Fatal)!;

        payload.Should().NotContain("\n", "запись аварийного канала остаётся однострочной");
        payload.Should().NotContain("\r");
        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("crashId").GetString().Should().Be("crash-json");
        Encoding.UTF8.GetByteCount(payload).Should().BeLessThanOrEqualTo(EmergencyLogSink.DefaultReasonLimit * 8);
    }

    [TestMethod]
    public void EmergencyLogSink_SharedInstance_IsAvailableWithoutInitialization()
    {
        EmergencyLogSink shared = EmergencyLogSink.Shared;

        shared.Should().NotBeNull("общий аварийный канал доступен до инициализации основного логгера");
        shared.WrittenCount.Should().BeGreaterThanOrEqualTo(0);
        shared.DroppedCount.Should().BeGreaterThanOrEqualTo(0);
    }
}
