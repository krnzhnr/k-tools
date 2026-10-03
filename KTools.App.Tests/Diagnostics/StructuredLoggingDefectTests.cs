// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Diagnostics;

/// <summary>
/// Регрессионные проверки по устранённым дефектам журналирования:
/// потеря целого батча при отказе одного элемента, утечка query-строки URL,
/// регрессия фильтра баннеров ffmpeg, слабая обезличка машинных значений,
/// обязательный ErrorCode у Error/Fatal и обязательный CleanupState при отмене.
/// </summary>
[TestClass]
public sealed class StructuredLoggingDefectTests
{
    private static string AppSourceDirectory { get; } = ResolveAppSourceDirectory();

    // ---------- Пункт 1: тихая потеря событий в ProcessBatch ----------

    /// <summary>
    /// Регрессия не была наблюдаема извне: счётчик потерь рос, но остальные события
    /// батча всё равно должны доходить до файла и подписчиков. Проверяем контракт
    /// кода: у ProcessBatch нет общей попытки, а обработка элемента изолирована.
    /// </summary>
    [TestMethod]
    public void LogService_ProcessBatch_IsolatesPerItemFailureAndAlwaysDrains()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Act
        string body = ExtractMethodBody(source, "private void ProcessBatch(");

        // Assert
        body.Should().Contain("ProcessWorkItem(",
            "каждый элемент батча обязан обрабатываться собственным методом с собственным try/catch");
        body.Should().Contain("finally",
            "DrainLineBatch() обязан выполняться в finally, иначе отказ элемента теряет остаток буфера");
        string workItem = ExtractMethodBody(source, "private void ProcessWorkItem(");
        workItem.Should().NotContain("for (", "обработка не должна снова становиться общим циклом по батчу");
        workItem.Should().Contain("DropEvent()",
            "потеря элемента обязана учитываться существующим счётчиком потерь");
        workItem.Should().Contain("item.Completion?.TrySetResult(",
            "ожидание Flush/Clear обязано завершаться даже при потере, иначе вызывающий висит до таймаута");
    }

    /// <summary>
    /// Событие, которое не удалось записать, не должно оставлять <c>Sequence</c>
    /// с молчаливой дырой: номер расходуется, но потеря немедленно публикуется
    /// маркером <c>log.queue.dropped</c> со счётчиком.
    /// </summary>
    [TestMethod]
    public void LogService_DroppedEvents_AreExplainedByDroppedMarkerInFile()
    {
        // Arrange
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = scope.GetFullPath("logs"),
            EmergencyDirectory = scope.GetFullPath("emergency"),
            FlushIntervalMilliseconds = 60000,
            QueueCapacity = 8
        };
        using LogService service = new(options);

        // Act — переполняем очередь, чтобы сработал счётчик потерь
        for (int index = 0; index < 300; index++)
        {
            service.Info("переполнение " + index, "Defect");
        }

        service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();

        // Assert
        service.DroppedEventCount.Should().BeGreaterThan(0, "очередь должна переполниться");
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(50);
        events.Should().Contain(
            e => e.EventId == LogEventMarkerNames.DroppedMarker,
            "дыра в Sequence не бывает молчаливой: потеря объясняется маркером в самом файле");
        LogEvent marker = events.First(e => e.EventId == LogEventMarkerNames.DroppedMarker);
        marker.Properties.Should().ContainKey("DroppedCount");
        Convert.ToInt64(marker.Properties!["DroppedCount"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0);
    }

    // ---------- Пункт 2: утечка query-строки URL ----------

    [TestMethod]
    [DataRow("https://host/a.mkv?Expires=1893456000&Policy=xyz&Key-Pair-Id=abc", "a.mkv")]
    [DataRow("https://host/a.mkv?AWSAccessKeyId=AKIA123&Signature=deadbeef", "a.mkv")]
    [DataRow("https://host/a.mkv?X-Amz-Signature=deadbeef&X-Amz-Credential=AKIA#frag", "a.mkv")]
    [DataRow("https://host/download?token=SENTINEL", "download")]
    public void FileName_SignedUrl_StripsQueryAndFragment(string url, string expected)
    {
        // Act
        string name = LogProps.FileName(url);

        // Assert
        name.Should().Be(expected);
        name.Should().NotContain("?");
        name.Should().NotContain("#");
        name.Should().NotContain("Expires");
        name.Should().NotContain("Policy");
        name.Should().NotContain("Key-Pair-Id");
        name.Should().NotContain("AWSAccessKeyId");
        name.Should().NotContain("Signature");
        name.Should().NotContain("SENTINEL");
    }

    [TestMethod]
    public void FileName_UnknownParameterNames_AreAlsoStripped()
    {
        // Arrange — параметры вне списка известных паттернов подписи
        string url = "https://host/movie.mkv?someUnlistedToken=abcdef&another=zzz";

        // Act
        string name = LogProps.FileName(url);

        // Assert
        name.Should().Be("movie.mkv", "отсечение query не зависит от списка известных имён параметров");
        name.Should().NotContain("someUnlistedToken");
        name.Should().NotContain("abcdef");
    }

    [TestMethod]
    public void FileName_SinglePolicyMatchesFileNameOnly()
    {
        // Assert — одна политика, а не две расходящиеся реализации
        string[] urls =
        {
            "https://host/a.mkv?Expires=1&Policy=p",
            @"C:\Users\SENTINEL\Videos\a.mkv",
            "\\\\server\\share\\SENTINEL\\a.mkv",
            "relative/dir/a.mkv"
        };

        foreach (string url in urls)
        {
            LogProps.FileName(url).Should().Be(
                LogProps.FileNameOnly(url),
                "FileName и FileNameOnly обязаны реализовывать одну и ту же политику для «{0}»", url);
        }
    }

    /// <summary>
    /// Ни один production-вызов LogProps.FileName/FileNameOnly не должен идти
    /// в обход политики (сырой Path.GetFileName для allowlisted path-свойств).
    /// </summary>
    [TestMethod]
    public void ProductionCode_AllowlistedPathProperties_UseLogPropsPolicy()
    {
        // Arrange
        string[] keys = { "FileName", "InputName", "OutputName" };
        var offenders = new List<string>();

        // Act
        foreach (string file in Directory.EnumerateFiles(AppSourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            string content = File.ReadAllText(file);
            foreach (string key in keys)
            {
                foreach (Match match in Regex.Matches(content, "\"" + key + "\"\\s*=>\\s*(?<value>[^\\r\\n;,]+)"))
                {
                    string value = match.Groups["value"].Value;
                    if (value.Contains("item.FileName", StringComparison.Ordinal)
                        || value.Contains(".FileName)", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {key} => {value.Trim()}");
                    }
                }
            }
        }

        // Assert
        offenders.Should().BeEmpty(
            "item.FileName может содержать query-строку URL, поэтому в allowlisted path-свойства журнала идёт только LogProps.FileName(item.FilePath)");
    }

    // ---------- Пункт 3: фильтр баннеров ffmpeg ----------

    [TestMethod]
    [DataRow("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'movie.mkv': Invalid data found when processing input")]
    [DataRow("Output #0, mp4, to 'out.mp4':")]
    [DataRow("Metadata:")]
    [DataRow("encoder: Lavf58.76.100")]
    [DataRow("Stream #0:0(eng): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s")]
    [DataRow("libavutil      59. 8.100 / 59. 8.100")]
    [DataRow("libswresample   4.12.100 /  4.12.100")]
    public void BannerFilter_KeepsRealDiagnostics(string line)
    {
        // Assert
        ProcessOutputPolicy.IsBuildBannerLine(line).Should().BeFalse(
            "строка «{0}» является диагностикой, а не баннером", line);
    }

    [TestMethod]
    [DataRow("ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers")]
    [DataRow("built with gcc 14.2.0 (GCC)")]
    [DataRow("configuration: --enable-gpl --enable-version3")]
    [DataRow("Stream mapping: Stream #0:0 -> #0:0 (aac (native) -> aac (native))")]
    [DataRow("Press [q] to stop, [?] for help")]
    public void BannerFilter_StillRemovesRealBanners(string line)
    {
        // Assert
        ProcessOutputPolicy.IsBuildBannerLine(line).Should().BeTrue(
            "настоящий баннер ffmpeg «{0}» обязан отсекаться", line);
    }

    [TestMethod]
    [DataRow("mkvmerge v72.0.0 ('Beyond the Boundaries') 64-bit")]
    [DataRow("MKVToolNix v9.9.9")]
    [DataRow("qaac 0.6.1")]
    [DataRow("ATSC aacEnc 1.0")]
    public void BannerFilter_DoesNotAffectThirdPartyTools(string line)
    {
        // Assert
        ProcessOutputPolicy.IsBuildBannerLine(line).Should().BeFalse(
            "у сторонних утилит баннерных префиксов ffmpeg нет, их вывод фильтр не режет");
    }

    [TestMethod]
    public void OutputBuffer_FilteredBanners_AreVisibleThroughDroppedLines()
    {
        // Arrange
        var buffer = new ProcessOutputBuffer();

        // Act
        buffer.Append("ffmpeg version 7.1 Copyright (c) 2000-2024");
        buffer.Append("Input #0, mov,mp4: Invalid data found when processing input");
        string tail = buffer.BuildTail();

        // Assert
        tail.Should().NotContain("ffmpeg version");
        tail.Should().Contain("Invalid data found when processing input",
            "диагностика отказа ffmpeg обязана пережить фильтр баннеров");
        ProcessOutputMetrics metrics = buffer.Metrics();
        metrics.DroppedLines.Should().BeGreaterThan(0,
            "отфильтрованные строки обязаны попадать в счётчик, иначе пустой OutputTail выглядит как «процесс ничего не вывел»");
        buffer.Truncated.Should().BeFalse("отсечение баннеров не является усечением диагностического хвоста");
    }

    [TestMethod]
    public void OutputBuffer_AllLinesFiltered_TailIsEmptyButCounted()
    {
        // Arrange
        var buffer = new ProcessOutputBuffer();

        // Act
        buffer.Append("ffmpeg version 7.1");
        buffer.Append("configuration: --enable-gpl");
        string tail = buffer.BuildTail();

        // Assert
        tail.Should().BeEmpty();
        buffer.Metrics().DroppedLines.Should().Be(2, "полное отфильтровывание не должно исчезать из события без следа");
    }

    // ---------- Пункт 4: ReadableMachineValue ----------

    [TestMethod]
    [DataRow("password=SENTINEL-SECRET")]
    [DataRow("token=SENTINEL-SECRET")]
    [DataRow("Expires=1893456000&Policy=SENTINEL-POLICY&Key-Pair-Id=abc")]
    [DataRow("AWSAccessKeyId=AKIASENTINEL&Signature=SENTINEL-SIGN")]
    [DataRow("api_key: SENTINEL-SECRET")]
    [DataRow("https://cdn.example.com/a.mkv?token=SENTINEL")]
    [DataRow(@"C:\Users\SENTINEL\a.mkv")]
    [DataRow(@"\\server\share\SENTINEL.mkv")]
    [DataRow("C:SENTINEL.txt")]
    public void ReadableMachineValue_SecretShapedValues_NeverPassThrough(string value)
    {
        // Act
        string result = LogRedactor.ReadableMachineValue(value);

        // Assert
        result.Should().Be("unknown", "значение «{0}» похоже на секрет или путь и не должно проходить дословно", value);
        result.Should().NotContain("SENTINEL");
    }

    [TestMethod]
    public void ReadableMachineValue_MatchesCompactSafeTokenOnPrivacy()
    {
        // Arrange — те же значения, что и в CompactSafeToken
        string[] unsafeValues =
        {
            "password=SENTINEL",
            "https://host/a.mkv?Expires=1",
            @"C:\Users\ivan\a.mkv",
            @"\\server\share\a.mkv"
        };

        // Act / Assert
        foreach (string value in unsafeValues)
        {
            LogRedactor.ReadableMachineValue(value).Should().Be(
                LogRedactor.CompactSafeToken(value),
                "приватность обоих обезличивателей обязана совпадать для «{0}»", value);
        }
    }

    [TestMethod]
    public void ReadableMachineValue_SamePageField_UsesOneBehaviourInProduction()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "MainPage.xaml.cs"));

        // Act
        var pageAssignments = Regex.Matches(source, "\"Page\",\\s*LogRedactor\\.(?<fn>\\w+)\\(")
            .Select(m => m.Groups["fn"].Value)
            .Distinct()
            .ToList();

        // Assert
        pageAssignments.Should().HaveCount(
            1,
            "одно и то же свойство Page журнала не должно обезличиваться двумя разными правилами");
        pageAssignments.Should().Contain("ReadableMachineValue");
    }

    [TestMethod]
    public void Truncation_ByChars_DoesNotSplitSurrogatePairs()
    {
        // Arrange — эмодзи занимает 2 char, поэтому наивный срез даёт невалидный UTF-16
        string emoji = new(new[] { (char)0xD83D, (char)0xDE00 });
        string value = string.Concat(Enumerable.Repeat(emoji, 40));

        // Act
        string token = LogRedactor.CompactSafeToken(value);
        string readable = LogRedactor.ReadableMachineValue(value);
        string bounded = LogRedactor.TruncateChars(value, 5);

        // Assert
        bounded.Should().HaveLength(4, "срез 5 символов должен отступить назад, не разрывая суррогатную пару");
        bounded.Should().NotContain("\uFFFD");
        char.IsHighSurrogate(bounded[^1]).Should().BeFalse("последний символ не может быть старшим суррогатом");
        token.Should().NotBeNull();
        readable.Should().NotBeNull();
        foreach (string produced in new[] { token, readable, bounded })
        {
            for (int index = 0; index < produced.Length; index++)
            {
                if (char.IsHighSurrogate(produced[index]))
                {
                    (index + 1 < produced.Length && char.IsLowSurrogate(produced[index + 1]))
                        .Should().BeTrue("обрезка не должна оставлять висячий старший суррогат");
                }
            }
        }
    }

    [TestMethod]
    public void RedactionPipeline_ShutdownStateToken_IsNotTreatedAsLeak()
    {
        // Arrange — реальный токен состояния завершения из CrashCoordinator
        string token = "requested=True;operations=True;settings=True;processes=True;watcher=True;flush=True;disposed=True;exit=True";

        // Act
        string redacted = LogRedactor.Default.RedactToken(token);

        // Assert
        redacted.Should().Be(token,
            "машинное состояние «key=value» из булевых признаков не должно редактироваться: иначе теряется диагностика завершения");
        redacted.Should().NotContain(LogRedactor.RedactedMarker);
        redacted.Should().NotContain("password");
        token.Should().NotContain("token=");
        LogRedactor.ReadableMachineValue("exit-request-pending").Should().Be("exit-request-pending",
            "машинный код состояния остаётся читаемым, а не превращается в unknown");
    }

    // ---------- Пункт 5: ErrorCode у Error/Fatal ----------

    [TestMethod]
    public void ProductionCode_EveryErrorOrFatalWrite_CarriesNonEmptyErrorCode()
    {
        // Arrange
        var missing = new List<string>();
        var scanned = 0;

        // Act
        foreach (string file in EnumerateProductionSources())
        {
            string text = File.ReadAllText(file);
            foreach ((string Receiver, int Index, string Arguments) call in EnumerateWriteCalls(text))
            {
                if (!IsKnownLogger(call.Receiver))
                {
                    continue;
                }

                if (!MentionsErrorOrFatalLevel(call.Arguments, out string levelName))
                {
                    continue;
                }

                scanned++;
                if (!CarriesErrorCode(call.Arguments) && !EnclosingScopeMentions(text, call.Index, "\"ErrorCode\""))
                {
                    int line = 1 + text[..call.Index].Count(c => c == '\n');
                    missing.Add($"{Relative(file)}:{line}: {levelName} -> {call.Arguments.Trim()}");
                }
            }
        }

        // Assert
        scanned.Should().BeGreaterThan(
            20,
            "скан должен охватывать реальные вызовы Error/Fatal, иначе проверка вакуумна");
        missing.Should().BeEmpty(
            "каждый production-вызов LogLevel.Error/Fatal обязан нести непустой машиночитаемый ErrorCode");
        FindErrorCodeViolation(
                "Write(\"test.event\", LogLevel.Error, LogStatus.Failed, \"Сбой\", new Dictionary<string, object?>())")
            .Should().BeTrue("детектор обязан ловить Error/Fatal без ErrorCode");
        FindErrorCodeViolation(
                "Write(\"test.event\", LogLevel.Fatal, LogStatus.Failed, \"Авария\", properties: LogProps.Create(\"ErrorCode\", \"app-startup-failed\"))")
            .Should().BeFalse("валидный вызов с ErrorCode нарушением не считается");
    }

    private static bool FindErrorCodeViolation(string arguments) =>
        MentionsErrorOrFatalLevel(arguments, out _) && !CarriesErrorCode(arguments);

    [TestMethod]
    public void CrashCoordinator_FatalEvents_CarryMachineReadableErrorCode()
    {
        // Act
        Dictionary<string, object?> properties = ReadPrivateBuildProperties();

        // Assert
        properties.Should().ContainKey("ErrorCode");
        properties.GetValueOrDefault("ErrorCode").Should().Be("crash-appdomain-terminating");
        properties.GetValueOrDefault("Stage").Should().Be(nameof(CrashEventKind.AppDomainTerminating));
    }

    [TestMethod]
    public void AppErrorCodes_AreUniqueSnakeKebabMachineTokens()
    {
        // Act
        var values = new List<string>();
        foreach (System.Reflection.FieldInfo field in typeof(AppErrorCodes)
                     .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            string value = (string)field.GetValue(null)!;
            values.Add(value);
        }

        // Assert
        values.Should().NotBeEmpty("коды отказов объявлены");
        values.Should().OnlyHaveUniqueItems("один код на один сайт: переиспользование скрывает источник отказа");
        foreach (string value in values)
        {
            value.Should().MatchRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)+$",
                "код «{0}» обязан быть машиночитаемым kebab-case латиницей", value);
        }
    }

    // ---------- Пункт 6: CleanupState при отмене ----------

    /// <summary>
    /// Источник отмены в production-коде обязан нести CleanupState: по нему видно,
    /// что не осталось ни процесса, ни артефактов, ни незаписанного вывода.
    /// </summary>
    [TestMethod]
    public void ProductionCode_CancelledWarningOrAbove_AlwaysCarriesCleanupState()
    {
        // Arrange
        var offenders = new List<string>();
        var scanned = 0;

        // Act
        foreach (string file in EnumerateProductionSources())
        {
            string text = File.ReadAllText(file);
            foreach ((string Receiver, int Index, string Arguments) call in EnumerateWriteCalls(text))
            {
                if (!IsKnownLogger(call.Receiver))
                {
                    continue;
                }

                if (!MentionsCancellationOutcome(call.Arguments)
                    || !MentionsWarningOrAbove(call.Arguments))
                {
                    continue;
                }

                scanned++;
                if (!MentionsCleanupState(call.Arguments) && !EnclosingScopeMentions(text, call.Index, "CleanupState"))
                {
                    int line = 1 + text[..call.Index].Count(c => c == '\n');
                    offenders.Add($"{Relative(file)}:{line}: {call.Arguments.Trim()}");
                }
            }
        }

        // Assert
        scanned.Should().BeGreaterThan(0, "скан должен охватывать отмену в production-коде");
        offenders.Should().BeEmpty(
            "любое событие об отмене уровня Warning и выше обязано нести CleanupState");
        FindCleanupStateViolation(
                "Write(\"process.cancelled\", LogLevel.Warning, LogStatus.Cancelled, \"Отмена\")")
            .Should().BeTrue("детектор обязан ловить Warning+Cancelled без CleanupState");
        FindCleanupStateViolation(
                "Write(\"process.cancelled\", LogLevel.Info, LogStatus.Cancelled, \"Отмена\")")
            .Should().BeFalse("уровень ниже Warning не попадает под правило");
        FindCleanupStateViolation(
                "Write(\"process.cancelled\", LogLevel.Warning, LogStatus.Cancelled, \"Отмена\", properties: LogProps.Create(\"CleanupState\", \"Completed\"))")
            .Should().BeFalse("валидный вызов с CleanupState нарушением не считается");
    }

    private static bool FindCleanupStateViolation(string arguments) =>
        MentionsCancellationOutcome(arguments)
        && MentionsWarningOrAbove(arguments)
        && !MentionsCleanupState(arguments);

    [TestMethod]
    public void ProcessResult_Cancelled_ReachesLogWithCleanupState()
    {
        // Arrange
        ProcessExecutionContext context = ProcessExecutionContext.NewOperation("ffmpeg");

        // Act
        ProcessResult verified = ProcessResult.Cancelled(
            context,
            "отмена подтверждена",
            "proc-1",
            pid: 42,
            terminationVerified: true,
            errorCode: ProcessResult.ErrorCancelled,
            cleanupState: CleanupState.Completed,
            outputMetrics: new ProcessOutputMetrics { DroppedLines = 3, DroppedBytes = 120, RetainedLines = 2, RetainedBytes = 40 });

        ProcessResult unverified = ProcessResult.Cancelled(
            context,
            "отмена не подтверждена",
            "proc-2",
            terminationVerified: false,
            errorCode: ProcessResult.ErrorTerminationUnverified,
            cleanupState: CleanupState.Partial);

        // Assert
        Dictionary<string, object?> verifiedProperties = verified.ToLogProperties();
        verifiedProperties["CleanupState"].Should().Be(nameof(CleanupState.Completed));
        verifiedProperties["ErrorCode"].Should().Be(ProcessResult.ErrorCancelled);
        verifiedProperties["DroppedLines"].Should().Be(3);
        verifiedProperties["RetainedLines"].Should().Be(2);

        Dictionary<string, object?> unverifiedProperties = unverified.ToLogProperties();
        unverifiedProperties["CleanupState"].Should().Be(nameof(CleanupState.Partial),
            "неподтверждённая отмена — это частичная уборка, а не завершённая");
        unverifiedProperties["ErrorCode"].Should().Be(ProcessResult.ErrorTerminationUnverified);
    }

    [TestMethod]
    public void ProcessResult_ToLogProperties_CarriesOutputBudgetCounters()
    {
        // Arrange
        ProcessExecutionContext context = ProcessExecutionContext.NewOperation("ffmpeg");
        ProcessResult result = ProcessResult.Failed(context, ProcessResult.ErrorNonZeroExit, "сбой")
            with
            {
                OutputMetrics = ProcessOutputMetrics.Combine(
                    new ProcessOutputMetrics { Stream = "stdout", DroppedLines = 2, DroppedBytes = 40, RetainedLines = 5, RetainedBytes = 100, TotalBytes = 200 },
                    new ProcessOutputMetrics { Stream = "stderr", DroppedLines = 1, DroppedBytes = 20, RetainedLines = 3, RetainedBytes = 60, TotalBytes = 120 },
                    suppressedCount: 4)
            };

        // Act
        Dictionary<string, object?> properties = result.ToLogProperties();

        // Assert
        properties["DroppedLines"].Should().Be(3, "суммарные счётчики stdout и stderr обязаны доходить до журнала");
        properties["DroppedBytes"].Should().Be(60);
        properties["RetainedLines"].Should().Be(8);
        properties["RetainedBytes"].Should().Be(160);
        properties["SuppressedCount"].Should().Be(4);
    }

    [TestMethod]
    public void ProcessResult_ExitWithoutExitCode_UsesCompletedEventWithMarker()
    {
        // Arrange
        string abstractRunner = File.ReadAllText(Path.Combine(AppSourceDirectory, "Infrastructure", "AbstractProcessRunner.cs"));
        string directRunner = File.ReadAllText(Path.Combine(AppSourceDirectory, "Infrastructure", "DirectProcessRunner.cs"));

        // Assert
        abstractRunner.Should().Contain("osPid > 0 && outcome.ExitCode.HasValue ? ProcessEventIds.Exit : ProcessEventIds.Completed",
            "без кода возврата событие уходит под process.completed и не отклоняется схемой");
        directRunner.Should().Contain("exitCodeKnown ? ProcessEventIds.Exit : ProcessEventIds.Completed");
        abstractRunner.Should().Contain("[\"ExitCode\"] = \"unavailable\"",
            "недоступность кода возврата фиксируется явно, чтобы потеря кода не выглядела как нулевой код");
        directRunner.Should().Contain("[\"ExitCode\"] = \"unavailable\"");
    }

    [TestMethod]
    public void EventSchema_ProcessExitStillRequiresExitCode()
    {
        // Arrange — схема для нормального случая не ослаблена
        LogEvent missing = new()
        {
            EventId = "process.exit",
            Level = LogLevel.Error,
            Status = LogStatus.Failed,
            Source = "ffmpeg",
            Message = "Выход",
            OperationId = "op-1",
            Tool = "ffmpeg",
            ProcessId = "proc-1",
            Pid = 42,
            DurationMs = 10
        };

        // Assert
        LogService.ValidateEventSchema(missing).Should().Be("process-exit-missing-exit-code");
    }

    // ---------- Пункт 7: успех без ErrorCode ----------

    [TestMethod]
    public void ProcessTerminationSummary_Success_OmitsErrorCodeProperty()
    {
        // Arrange
        ProcessTerminationSummary summary = new(attempted: 2, succeeded: 2, failed: 0, alreadyExited: 0, results:
        [
            new ProcessTerminationResult("proc-1", 10, true, true, false, false, 0, []),
            new ProcessTerminationResult("proc-2", 11, true, true, false, false, 0, [])
        ]);

        // Act
        Dictionary<string, object?> properties = summary.ToLogProperties();

        // Assert
        properties.Should().NotContainKey("ErrorCode",
            "потребитель, ищущий отказы по errorCode != null, не должен получать ложное срабатывание на успехе");
    }

    [TestMethod]
    public void ProcessResult_Succeeded_OmitsErrorCode()
    {
        // Arrange / Act
        ProcessResult result = ProcessResult.Succeeded(ProcessExecutionContext.NewOperation("ffmpeg"));

        // Assert
        result.ErrorCode.Should().BeNull();
        result.ToLogProperties().Should().NotContainKey("ErrorCode");
    }

    // ---------- Пункт 8 / 13: мёртвый код в production ----------

    [TestMethod]
    public void ProductionCode_HasNoDeadApplyRetentionIfBudgetAged()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Arrange
        var calls = Regex.Matches(source, @"ApplyRetentionIfBudgetAged\(").Count;

        // Assert
        calls.Should().BeGreaterThan(1, "проверка бюджета каталога между ротациями обязана быть задействована, а не мертва");
        source.Should().Contain("_retentionCheckTick",
            "проверка бюджета каталога ограничена по частоте, иначе она подписывает файл сам у себя на каждой записи");
    }

    [TestMethod]
    public void ProductionCode_HasNoDeadProtectedPathsParameter()
    {
        // Arrange
        string policy = File.ReadAllText(Path.Combine(AppSourceDirectory, "Diagnostics", "LogFilePolicy.cs"));
        string service = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Assert
        policy.Should().NotContain("protectedPaths",
            "параметр, который в production всегда null, удалён вместе со своим мёртвым тестом");
        service.Should().NotContain("ApplyRetention(directory, CurrentLogFile, null)");
    }

    [TestMethod]
    public void ProductionCode_HasNoDeadNetworkSchemaBranch()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Assert
        source.Should().NotContain("\"network.\"",
            "событий network.* в production нет, поэтому ветка схемы удалена как мёртвая");
    }

    [TestMethod]
    public void ProductionCode_FlusherLoop_CountsScheduleFailures()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Act
        string body = ExtractMethodBody(source, "private void FlusherLoop()");

        // Assert
        body.Should().Contain("_flushScheduleFailureCount",
            "потеря периодического сброса обязана учитываться счётчиком");
        body.Should().Contain("if (!scheduled)",
            "цикл продолжает работу после неудачной постановки Flush-запроса, иначе flusher погибает навсегда");    }

    [TestMethod]
    public void ProductionCode_HasNoDeadMarkerBytesCheck()
    {
        // Arrange
        string source = File.ReadAllText(Path.Combine(AppSourceDirectory, "Services", "Implementations", "LogService.cs"));

        // Assert
        ExtractMethodBody(source, "private void AppendOversizedMarkerNoLock()")
            .Should().NotContain("markerBytes",
                "переменная, которая только проверялась на >= 0, удалена вместе с мёртвой проверкой");
    }

    // ---------- Пункт 16: ProcessLayerTests: невакуумный путь ----------

    [TestMethod]
    public async Task ProcessLayer_BoundedOutput_IsObservableThroughRealRunner()
    {
        // Arrange — реальный процесс, реальные события, а не выключенная фича
        var log = new RecordingLogService();
        DirectProcessRunner runner = new(log);
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        // Act
        ProcessResult result = await runner.RunAsync(
            comspec,
            "cmd",
            "/c for /L %i in (1,1,800) do @echo marker-%i",
            ProcessExecutionContext.NewOperation("cmd"));

        // Assert
        result.OutputTail.Should().NotBeNull();
        result.OutputTail!.Length.Should().BeLessThanOrEqualTo(ProcessOutputPolicy.DefaultMaxTailBytes);
        result.OutputMetrics.RetainedLines.Should().BeGreaterThan(0, "хвост реально наполнен");
        result.OutputMetrics.RetainedBytes.Should().BeGreaterThan(0);

        RecordedLogEvent terminal = log.Events
            .Where(e => e.EventId is ProcessEventIds.Exit or ProcessEventIds.Completed)
            .Should().ContainSingle().Which;
        terminal.GetProperty<string>("OutputTail").Should().NotBeNullOrWhiteSpace(
            "хвост вывода обязан доходить до журнала, иначе усечение невидимо");
    }

    // ---------- Вспомогательные функции ----------

    private static Dictionary<string, object?> ReadPrivateBuildProperties()
    {
        System.Reflection.MethodInfo build = typeof(CrashCoordinator)
            .GetMethod("BuildProperties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("BuildProperties не найден");

        var sink = new EmergencyLogSink();
        using var coordinator = new CrashCoordinator(sink, new RecordingLogService());
        CrashRecord record = new(
            "crash-1",
            Guid.NewGuid(),
            "App.Domain",
            DateTimeOffset.UtcNow,
            "reason",
            LogLevel.Fatal,
            CrashEventKind.AppDomainTerminating,
            null,
            emergencyRequired: true);

        return (Dictionary<string, object?>)build.Invoke(coordinator, new object[] { record })!;
    }

    private static IEnumerable<string> EnumerateProductionSources() =>
        Directory.EnumerateFiles(AppSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Сайты записи в журнал: прямой <c>ILogService.Write(...)</c> и private-forwarder
    /// <c>App.WriteAppEvent(...)</c>, через который проходят все события уровня приложения.
    /// </summary>
    private static readonly string[] WriteCallMarkers = [".Write(", "WriteAppEvent("];

    private static IEnumerable<(string Receiver, int Index, string Arguments)> EnumerateWriteCalls(string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            int next = -1;
            string marker = string.Empty;
            foreach (string candidate in WriteCallMarkers)
            {
                int found = text.IndexOf(candidate, index, StringComparison.Ordinal);
                if (found >= 0 && (next < 0 || found < next))
                {
                    next = found;
                    marker = candidate;
                }
            }

            if (next < 0)
            {
                yield break;
            }

            int openParen = next + marker.Length - 1;
            string? arguments = ExtractArguments(text, openParen);
            if (arguments is not null)
            {
                yield return (marker == "WriteAppEvent(" ? "WriteAppEvent" : ExtractReceiver(text, next), next, arguments);
            }

            index = openParen + 1;
        }
    }

    /// <summary>
    /// Проверяет, что нужный маркер встречается в аргументах вызова либо в тексте
    /// текущего метода до него: свойства события часто собираются в локальный словарь,
    /// и тогда ErrorCode или CleanupState видны не в аргументах, а на пару строк выше.
    /// Область поиска — строго тело одного члена класса: подмена одного сайта не должна
    /// прятаться за чужой ErrorCode в соседнем методе того же класса.
    /// </summary>
    private static bool EnclosingScopeMentions(string text, int callIndex, string token) =>
        FindEnclosingMemberStart(text, callIndex) is int start && start >= 0
        && text[start..callIndex].Contains(token, StringComparison.Ordinal);

    /// <summary>
    /// Признак передачи машиночитаемого кода отказа. Сравнение идёт по ключу в кавычках
    /// или по имени типизированного поля: подстрока <c>ErrorCode</c> без кавычек совпала бы
    /// ещё и с именем типа-агрегатора кодов, и проверка стала бы вакуумной.
    /// </summary>
    private static bool CarriesErrorCode(string text) =>
        text.Contains("\"ErrorCode\"", StringComparison.Ordinal)
        || text.Contains("ErrorCode =", StringComparison.Ordinal)
        || text.Contains("ErrorCode,", StringComparison.Ordinal);

    private static readonly Regex MemberDeclaration = new(
        @"^    (?:(?:public|private|internal|protected|static|sealed|override|virtual|async|new|partial|extern|unsafe)[ \t]+)+[A-Za-z_][\w<>\[\],\.\?]*[ \t]+[A-Za-z_]\w*[ \t]*(?:<[^>(]*>)?[ \t]*\(",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Находит начало ближайшего члена класса (метода) над позицией вызова.
    /// Члены уровня класса в этом проекте всегда отступают на четыре пробела,
    /// поэтому вложенные локальные функции и лямбды отсекаются автоматически.
    /// </summary>
    private static int FindEnclosingMemberStart(string text, int callIndex)
    {
        int found = -1;
        Match match = MemberDeclaration.Match(text, 0);
        while (match.Success && match.Index < callIndex)
        {
            found = match.Index;
            match = MemberDeclaration.Match(text, match.Index + match.Length);
        }

        return found;
    }
    private static string ExtractBalancedBody(string text, int bodyStart)
    {
        int depth = 0;
        int i = bodyStart;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        break;
                    }

                    i++;
                }
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[bodyStart..i];
                }
            }

            i++;
        }

        return text[bodyStart..];
    }

    /// <summary>
    /// Получатели структурированного журнала. Аварийный файловый канал
    /// (<c>EmergencyLogSink</c>) сюда не входит: у него другой контракт — поле
    /// <c>reason</c> вместо ErrorCode, и он пишет не в журнал событий.
    /// </summary>
    private static bool IsKnownLogger(string receiver) => receiver
        is "_logService"
        or "Log"
        or "logService"
        or "_log"
        or "log"
        or "WriteAppEvent";

    private static bool MentionsErrorOrFatalLevel(string arguments, out string levelName)
    {
        Match match = Regex.Match(arguments, @"LogLevel\.(?<level>Error|Fatal)\b", RegexOptions.CultureInvariant);
        levelName = match.Success ? match.Groups["level"].Value : string.Empty;
        return match.Success;
    }

    private static bool MentionsCancellationOutcome(string arguments) =>
        arguments.Contains("LogStatus.Cancelled", StringComparison.Ordinal)
        || arguments.Contains("ProcessEventIds.Cancelled", StringComparison.Ordinal)
        || arguments.Contains("ProcessEventIds.TerminationUnverified", StringComparison.Ordinal)
        || arguments.Contains("ProcessResult.ErrorCancelled", StringComparison.Ordinal)
        || arguments.Contains("ProcessResult.ErrorTerminationUnverified", StringComparison.Ordinal)
        || arguments.Contains("\"queue-cancelled\"", StringComparison.Ordinal)
        || arguments.Contains("ExecutionStatus.Cancelled", StringComparison.Ordinal);

    private static bool MentionsWarningOrAbove(string arguments) =>
        arguments.Contains("LogLevel.Warning", StringComparison.Ordinal)
        || arguments.Contains("LogLevel.Error", StringComparison.Ordinal)
        || arguments.Contains("LogLevel.Fatal", StringComparison.Ordinal);

    private static bool MentionsCleanupState(string arguments) =>
        arguments.Contains("CleanupState", StringComparison.Ordinal);

    private static string ExtractReceiver(string text, int callIndex)
    {
        int end = callIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return string.Empty;
        }

        if (text[end] == ')')
        {
            int depth = 0;
            while (end >= 0)
            {
                if (text[end] == ')')
                {
                    depth++;
                }
                else if (text[end] == '(')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end--;
                        break;
                    }
                }

                end--;
            }

            while (end >= 0 && char.IsWhiteSpace(text[end]))
            {
                end--;
            }
        }

        int start = end;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] is '_' or '.' or '>'))
        {
            start--;
        }

        return text[(start + 1)..(end + 1)].Trim();
    }

    private static string? ExtractArguments(string text, int openParen)
    {
        int parens = 0;
        int braces = 0;
        int brackets = 0;
        int i = openParen;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, text.Length);
                continue;
            }

            if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '(')
            {
                parens++;
            }
            else if (c == ')')
            {
                parens--;
            }
            else if (c == '{')
            {
                braces++;
            }
            else if (c == '}')
            {
                braces--;
            }
            else if (c == '[')
            {
                brackets++;
            }
            else if (c == ']')
            {
                brackets--;
            }

            if (parens == 0 && braces <= 0 && brackets <= 0 && i > openParen)
            {
                return text[(openParen + 1)..i];
            }

            i++;
        }

        return null;
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BePositive("метод '{0}' должен существовать", signature);
        int bodyStart = source.IndexOf('{', start);
        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..i];
                }
            }
        }

        return source[bodyStart..];
    }

    private static string FirstLine(string statement)
    {
        string line = statement.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }

    private static string Relative(string path)
    {
        string root = Path.GetFullPath(AppSourceDirectory + Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : Path.GetFileName(full);
    }

    private static string ResolveAppSourceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "KTools.App");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "KTools.App.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Каталог KTools.App не найден");
    }
}
