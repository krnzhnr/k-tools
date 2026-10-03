// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Инварианты качества структурированного журнала production-кода.
/// Проверяются контракт события, приватность полей и полнота миграции.
/// </summary>
[TestClass]
public class StructuredLoggingQualityTests
{
    private static string AppSourceDirectory { get; } = ResolveAppSourceDirectory();

    [TestMethod]
    public void Write_WithStructuredEvent_RecordsEventIdLevelStatusAndContext()
    {
        // Arrange
        var log = new RecordingLogService();

        // Act
        log.Write(
            "test.event.completed",
            LogLevel.Info,
            LogStatus.Succeeded,
            "Операция завершена",
            source: "TestSource",
            properties: LogProps.Create("Count", 3).With("Persisted", true));

        // Assert
        var evt = log.Events.Should().ContainSingle().Subject;
        evt.EventId.Should().Be("test.event.completed");
        evt.Level.Should().Be(LogLevel.Info);
        evt.Status.Should().Be(LogStatus.Succeeded);
        evt.Message.Should().Be("Операция завершена");
        evt.GetProperty<int>("Count").Should().Be(3);
        evt.GetProperty<bool>("Persisted").Should().BeTrue();
    }

    [TestMethod]
    public void Write_WithException_RecordsExceptionExactlyOnce()
    {
        // Arrange
        var log = new RecordingLogService();
        var thrown = new InvalidOperationException("внутренняя причина");

        // Act
        log.Write(
            "test.event.failed",
            LogLevel.Error,
            LogStatus.Failed,
            "Операция не выполнена",
            thrown,
            "TestSource");

        // Assert
        var evt = log.Events.Should().ContainSingle().Subject;
        evt.Exception.Should().NotBeNull();
        evt.Message.Should().NotContain(thrown.Message);
    }

    [TestMethod]
    public void LogPropsFileName_FullPath_KeepsFileNameOnly()
    {
        // Act
        var result = LogProps.FileName(@"C:\Users\ivanov\Videos\secret-project.mkv");

        // Assert
        result.Should().Be("secret-project.mkv");
        result.Should().NotContain("Users");
        result.Should().NotContain(@"\");
    }

    [TestMethod]
    public void LogPropsRootName_FullPath_KeepsRootFolderOnly()
    {
        // Act
        var result = LogProps.RootName(@"C:\Users\ivanov\Videos\file.mkv");

        // Assert
        result.Should().NotContain("ivanov");
        result.Should().NotContain("file.mkv");
    }

    [TestMethod]
    public void LogPropsFingerprint_SameValue_IsStableAndNotReversible()
    {
        // Act
        var first = LogProps.Fingerprint("sensitive-token-value");
        var second = LogProps.Fingerprint("sensitive-token-value");

        // Assert
        first.Should().Be(second);
        first.Should().NotContain("sensitive-token-value");
    }

    [TestMethod]
    public void ProductionCode_ContainsNoLegacyLogCalls()
    {
        // Arrange
        var legacyPattern = new Regex(
            @"(?<![\w.])(?:_?logService|logService|LogService|App\.Services\.GetRequiredService<ILogService>\(\))\s*\.\s*(?:DebugLog|Info|Warn|Error|Fatal|Exception)\s*\(",
            RegexOptions.CultureInvariant);

        // Act
        var offenders = FindProductionSources()
            .Where(file => legacyPattern.IsMatch(File.ReadAllText(file)))
            .Select(path => Relative(path))
            .ToList();

        // Assert
        offenders.Should().BeEmpty("все production-вызовы журнала должны использовать структурированный Write(...)");
    }

    [TestMethod]
    public void ProductionCode_LogEventIds_UseStableDottedNotation()
    {
        // Arrange
        var eventIdPattern = new Regex(
            @"Write\(\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);
        var validPattern = new Regex(
            @"^[a-z][a-z0-9]*([._-][a-z0-9][a-z0-9]*)+$",
            RegexOptions.CultureInvariant);

        // Act
        var invalid = new List<string>();
        foreach (var file in FindProductionSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in eventIdPattern.Matches(text))
            {
                var id = match.Groups["id"].Value;
                if (!validPattern.IsMatch(id))
                {
                    invalid.Add($"{Relative(file)}: {id}");
                }
            }
        }

        // Assert
        invalid.Should().BeEmpty("EventId должны быть в стабильном dotted-виде без пробелов и заглавных букв");
    }

    [TestMethod]
    public void ProductionCode_LogProperties_OnlyUseAllowlistedKeys()
    {
        // Arrange
        var allowlist = ReadAllowedPropertyKeys();
        var keyPattern = new Regex(
            @"(?:LogProps\.Create|\.With)\(\s*""(?<key>[^""]+)""",
            RegexOptions.CultureInvariant);

        // Act
        var unlisted = new List<string>();
        foreach (var file in FindProductionSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in keyPattern.Matches(text))
            {
                var key = match.Groups["key"].Value;
                if (!allowlist.Contains(key))
                {
                    unlisted.Add($"{Relative(file)}: {key}");
                }
            }
        }

        // Assert
        unlisted.Should().BeEmpty("неallowlisted свойства будут отброшены редактором и потеряют диагностическую ценность");
    }

    [TestMethod]
    public void ProductionCode_LogMessages_DoNotEmbedExceptionMessages()
    {
        // Arrange
        var pattern = new Regex(
            @"\{(?:ex|e|inner|exception)\.Message\}",
            RegexOptions.CultureInvariant);

        // Act
        var offenders = EnumerateLogCalls()
            .Where(call => pattern.IsMatch(call.Statement))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.Statement)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty("исключение передаётся один раз через параметр exception, а не через текст сообщения");
    }

    [TestMethod]
    public void ProductionCode_LogMessages_DoNotEmbedFullPathsOrUrls()
    {
        // Arrange
        var urlPattern = new Regex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.CultureInvariant);
        // Абсолютный путь в тексте сообщения выглядит как @"C:\..." или "C:\\...".
        // Префикс @" перед кавычкой требовать нельзя: строка собирается интерполяцией,
        // поэтому искомое начало — сама буква диска, а не маркер интерполированного литерала.
        var drivePathPattern = new Regex(@"[A-Za-z]:[\\/]{1,2}[^\s""']*", RegexOptions.CultureInvariant);
        var queryPattern = new Regex(@"\?[a-zA-Z][a-zA-Z0-9_.\-]*=", RegexOptions.CultureInvariant);
        var uncPathPattern = new Regex(@"\\\\[A-Za-z0-9_.\-]+\\", RegexOptions.CultureInvariant);

        // Act
        var scanned = EnumerateLogCalls().ToList();
        var offenders = scanned
            .Where(call =>
            {
                var statement = call.Statement;
                var eventId = StripEventId(statement);
                return urlPattern.IsMatch(eventId)
                    || drivePathPattern.IsMatch(eventId)
                    || queryPattern.IsMatch(eventId)
                    || uncPathPattern.IsMatch(eventId);
            })
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.Statement)}")
            .ToList();

        // Assert
        scanned.Should().HaveCountGreaterThan(
            100,
            "скан должен охватывать реальные вызовы журнала, иначе проверка вакуумна");
        offenders.Should().BeEmpty("пути, URL и query-параметры не должны попадать в текст сообщений журнала");
        FindLeakingPath(
                "Write(\"test.path\", LogLevel.Info, $\"Файл C:\\\\Users\\\\SENTINEL\\\\a.mkv обработан\")")
            .Should().BeTrue("детектор обязан ловить абсолютный путь в тексте сообщения");
        FindLeakingPath(
                "Write(\"test.url\", LogLevel.Info, $\"Скачан https://cdn.example.com/a.mkv?Expires=1\")")
            .Should().BeTrue("детектор обязан ловить URL с query-строкой в тексте сообщения");
        FindLeakingPath("Write(\"test.unc\", LogLevel.Info, $\"\\\\\\\\server\\\\share\\\\a.mkv\")")
            .Should().BeTrue("детектор обязан ловить UNC-путь в тексте сообщения");
    }

    /// <summary>
    /// Прогоняет детектор утечек по синтетическому фрагменту: без самопроверки
    /// ослабленный паттерн тихо перестал бы ловить реальные нарушения.
    /// </summary>
    private static bool FindLeakingPath(string statement)
    {
        var urlPattern = new Regex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.CultureInvariant);
        var drivePathPattern = new Regex(@"[A-Za-z]:[\\/]{1,2}[^\s""']*", RegexOptions.CultureInvariant);
        var queryPattern = new Regex(@"\?[a-zA-Z][a-zA-Z0-9_.\-]*=", RegexOptions.CultureInvariant);
        var uncPathPattern = new Regex(@"\\\\[A-Za-z0-9_.\-]+\\", RegexOptions.CultureInvariant);
        string message = StripEventId(statement);
        return urlPattern.IsMatch(message)
            || drivePathPattern.IsMatch(message)
            || queryPattern.IsMatch(message)
            || uncPathPattern.IsMatch(message);
    }

    [TestMethod]
    public void ProductionCode_LogMessages_DoNotLeakClipboardOrTranscript()
    {
        // Arrange
        var pattern = new Regex(
            @"Get-Clipboard|Clipboard\.GetText|GetContentFromDataPackage|GetTextFromClipboard|clipboardText|transcriptText|rawTranscript",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Act
        var offenders = EnumerateLogCalls()
            .Where(call => pattern.IsMatch(StripEventId(call.Statement)))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.Statement)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty("содержимое буфера обмена и транскрипты не логируются");
    }

    [TestMethod]
    public void ProductionCode_LogMessages_DoNotUseCyrillicGarbageMarkers()
    {
        // Arrange
        var pattern = new Regex(
            "[\u8E29\u9759]",
            RegexOptions.CultureInvariant);

        // Act
        var offenders = EnumerateLogCalls()
            .Where(call => pattern.IsMatch(StripEventId(call.Statement)))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.Statement)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty("в сообщениях журнала не должно быть посторонних иероглифов");
    }

    [TestMethod]
    public void LogViewModelExport_BoundedExport_IsLimitedByEventCountAndCharBudget()
    {
        // Assert
        global::KTools_App.ViewModels.LogViewModel.ExportEventLimit.Should().BePositive();
        global::KTools_App.ViewModels.LogViewModel.ExportCharLimit.Should().BePositive();
        global::KTools_App.ViewModels.LogViewModel.RingLimit.Should().BePositive();
    }

    [TestMethod]
    public void LogViewModelExport_UsesRedactedFormattedEventsNotRawBuffer()
    {
        // Act
        var source = File.ReadAllText(
            Path.Combine(AppSourceDirectory, "ViewModels", "LogViewModel.cs"));
        var exportBody = ExtractMethodBody(source, "private string BuildBoundedExportText()");

        // Assert
        exportBody.Should().Contain("ReadRecentEvents", "экспорт должен читать структурированные события");
        exportBody.Should().NotContain("ReadCurrentLog", "экспорт не должен читать сырой буфер файла журнала");
        source.Should().Contain("LogEvent.Format", "экспорт должен использовать редактирование и маскирование");
    }

    [TestMethod]
    public void ProductionCode_LogCallReceivers_AreKnownLoggers()
    {
        // Arrange
        var knownReceivers = new HashSet<string>(StringComparer.Ordinal)
        {
            "_logService", "Log", "logService", "_log",
            "App.Services.GetRequiredService<ILogService>()",
            "EmergencyLogSink.Shared", "_emergencySink", "_emergency", "sink",
            "Volatile", "writer", "_writer", "stream"
        };

        // Act
        var offenders = EnumerateWriteCalls()
            .Where(call => call.Receiver.Length > 0)
            .Where(call => !knownReceivers.Contains(call.Receiver))
            .Select(call => $"{Relative(call.Path)}: {call.Receiver}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "запись в журнал должна выполняться только через известный получатель ILogService или диагностический sink");
    }

    [TestMethod]
    public void ProductionCode_ExplicitWriteOverload_UsesLiteralEventId()
    {
        // Arrange
        var constants = ReadDeclaredStringConstants();
        var validIdPattern = new Regex(
            @"^[a-z][a-z0-9]*([._-][a-z0-9][a-z0-9]*)+$",
            RegexOptions.CultureInvariant);

        // Act
        var offenders = EnumerateWriteCalls()
            .Where(call => call.IsExplicitOverload)
            .Select(call => (Call: call, EventIdArgument: call.Arguments[0]))
            .Where(item => !Regex.IsMatch(item.EventIdArgument, @"^\s*""", RegexOptions.CultureInvariant))
            .Where(item => !IsDeclaredEventIdConstant(item.EventIdArgument, constants, validIdPattern))
            .Select(item => $"{Relative(item.Call.Path)}: {FirstLine(item.EventIdArgument)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "явная перегрузка Write должна получать литерал или именованную константу EventId, иначе проверки схемы не видят идентификатор");
    }

    private static bool IsDeclaredEventIdConstant(
        string argument,
        IReadOnlyDictionary<string, List<string>> constants,
        Regex validIdPattern)
    {
        string name = argument.Trim().TrimStart('"');
        int separator = name.LastIndexOf('.');
        if (separator >= 0 && separator < name.Length - 1)
        {
            name = name[(separator + 1)..];
        }

        if (!constants.TryGetValue(name, out List<string>? values) || values.Count == 0)
        {
            return false;
        }

        return values.All(validIdPattern.IsMatch);
    }

    private static IReadOnlyDictionary<string, List<string>> ReadDeclaredStringConstants()
    {
        var pattern = new Regex(
            @"const\s+string\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*""(?<value>[^""]+)""",
            RegexOptions.CultureInvariant);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in FindProductionSources())
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var name = Regex.Match(match.Value, @"const\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)");
                if (!name.Success)
                {
                    continue;
                }

                var key = name.Groups["name"].Value;
                if (!result.TryGetValue(key, out List<string>? values))
                {
                    values = new List<string>();
                    result[key] = values;
                }

                values.Add(match.Groups["value"].Value);
            }
        }

        return result;
    }

    [TestMethod]
    public void ProductionCode_LogProperties_AllowlistedForIndexerWritesToo()
    {
        // Arrange
        var allowlist = ReadAllowedPropertyKeys();
        var indexerPattern = new Regex(
            @"\[\s*""(?<key>[^""]+)""\s*\]\s*=",
            RegexOptions.CultureInvariant);

        // Act
        var unlisted = new List<string>();
        foreach (var call in EnumerateWriteCalls())
        {
            foreach (Match match in indexerPattern.Matches(call.ArgumentsText))
            {
                var key = match.Groups["key"].Value;
                if (!allowlist.Contains(key))
                {
                    unlisted.Add($"{Relative(call.Path)}: {key}");
                }
            }
        }

        // Assert
        unlisted.Should().BeEmpty(
            "свойства, заданные через индексатор словаря, так же попадают в allowlist и не должны его обходить");
    }

    [TestMethod]
    public void ProductionCode_LogArguments_DoNotReadExceptionMessages()
    {
        // Arrange
        var pattern = new Regex(
            @"\b[A-Za-z_][A-Za-z0-9_]*(?:Ex|ex|Exception|exception|err|Err)\s*\.\s*Message\b",
            RegexOptions.CultureInvariant);

        // Act
        var offenders = EnumerateWriteCalls()
            .Where(call => pattern.IsMatch(call.ArgumentsText))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.ArgumentsText)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "включая deleteEx.Message: текст исключения передаётся только структурным полем");
    }

    [TestMethod]
    public void ProductionCode_LogWriteArguments_DoNotReadAnyMessageMember()
    {
        // Act
        var scanned = EnumerateWriteCalls().ToList();
        var offenders = FindMessageMemberReads(scanned)
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.ArgumentsText)}")
            .ToList();

        // Assert
        scanned.Should().HaveCountGreaterThan(
            100,
            "скан должен охватывать реальные вызовы журнала, иначе проверка вакуумна");
        offenders.Should().BeEmpty(
            "типизированный владелец exception:/ExceptionInfo уже несёт message, поэтому <ident>.Message не читается в аргументах Write(...)");

        string probePath = Path.Combine(AppSourceDirectory, "ExceptionMessageProbe.cs");
        FindMessageMemberReads(new[]
        {
            new WriteCall(probePath, "_logService", "$\"Сбой операции: {ex.Message}\", ex, source: Name", Array.Empty<string>())
        }).Should().HaveCount(1, "детектор обязан ловить чтение <ident>.Message внутри вызова Write");
        FindMessageMemberReads(new[]
        {
            new WriteCall(probePath, "EmergencyLogSink.Shared", "\"Необработанное исключение\", ex, source, crashId, session", Array.Empty<string>())
        }).Should().BeEmpty("валидный вызов Write с типизированным владельцем не считается нарушением");
    }

    private static IReadOnlyList<WriteCall> FindMessageMemberReads(IEnumerable<WriteCall> calls)
    {
        var pattern = new Regex(
            @"\b[A-Za-z_][A-Za-z0-9_]*\s*\.\s*Message\b",
            RegexOptions.CultureInvariant);
        return calls
            .Where(call => pattern.IsMatch(call.ArgumentsText))
            .ToList();
    }

    [TestMethod]
    public void ProductionCode_LogArguments_DoNotCarryClipboardOrTranscriptValues()
    {
        // Act
        var offenders = EnumerateWriteCalls()
            .Where(call => CarriesClipboardOrTranscriptValue(call.ArgumentsText))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.ArgumentsText)}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "содержимое буфера обмена и распознанная речь не должны попадать в аргументы записи журнала даже косвенно");
    }

    [TestMethod]
    public void App_Startup_ArmsMinLevelAndPublishesDiagnosticsStatus()
    {
        // Arrange
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory, "App.xaml.cs"));
        var eventIds = File.ReadAllText(Path.Combine(AppSourceDirectory, "AppEventIds.cs"));

        // Assert
        source.Should().Contain("ResolveStartupLogLevel(",
            "запуск обязан вычислять и применять минимальный уровень журналирования");
        source.Should().Contain("logService.MinLevel = startupMinLevel",
            "вычисленный уровень должен реально применяться к сервису, а не только вычисляться");
        source.Should().Contain("MinLevelEnvironmentVariable",
            "диагностический уровень Debug должен включаться явным переопределением");
        source.Should().Contain("AppEventIds.DiagnosticsStatus",
            "счётчики журналирования должны публиковаться в production-диагностику");
        eventIds.Should().Contain("DiagnosticsStatus = \"app.diagnostics.status\"",
            "идентификатор события счётчиков должен оставаться стабильной именованной константой в holder *EventIds");
        source.Should().Contain("logService.Status",
            "диагностическая поверхность должна читать снимок счётчиков сервиса");
    }

    [TestMethod]
    public void WorkPanel_ReservedEventIds_AlwaysCarryContext()
    {
        // Arrange
        var reservedPrefixes = new[] { "exec.item.", "exec.queue.", "batch.", "process." };

        // Act
        var offenders = EnumerateWriteCalls()
            .Where(call => call.IsExplicitOverload)
            .Where(call => reservedPrefixes.Any(prefix => call.Arguments[0].Contains($"\"{prefix}", StringComparison.Ordinal)))
            .Where(call => !call.ArgumentsText.Contains("context:"))
            .Select(call => $"{Relative(call.Path)}: {FirstLine(call.Arguments[0])}")
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "зарезервированные схемы журнала отклоняются без OperationId/ItemId, поэтому контекст обязателен");
    }

    /// <summary>
    /// Определяет, что в аргументы записи попало значение из буфера обмена или распознанной речи.
    /// Строковые литералы без интерполяции скрываются, чтобы событие с именем "ui.clipboard.copied"
    /// не считалось утечкой, а идентификаторы переменных и вызовы API оставались видимыми.
    /// </summary>
    private static bool CarriesClipboardOrTranscriptValue(string arguments)
    {
        string bare = StripPlainLiterals(arguments);
        if (bare.Contains("Clipboard.Get", StringComparison.Ordinal)
            || bare.Contains("GetTextAsync(", StringComparison.Ordinal)
            || bare.Contains("GetTextFromClipboard(", StringComparison.Ordinal)
            || bare.Contains("DataTransferManager", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Match token in IdentifierTokenPattern().Matches(bare))
        {
            string name = token.Value;
            if (name.Contains("clip", StringComparison.OrdinalIgnoreCase)
                || name.Contains("transcript", StringComparison.OrdinalIgnoreCase)
                || name.Contains("segmenttext", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Regex IdentifierToken = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    private static Regex IdentifierTokenPattern() => IdentifierToken;

    private static string StripPlainLiterals(string text)
    {
        return Regex.Replace(text, @"(?<!\$)""(?:[^""\\]|\\.)*""", "\"\"", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<WriteCall> EnumerateWriteCalls()
    {
        foreach (var file in FindProductionSources())
        {
            var text = File.ReadAllText(file);
            int index = 0;
            while (true)
            {
                int call = text.IndexOf(".Write(", index, StringComparison.Ordinal);
                if (call < 0)
                {
                    break;
                }

                int openParen = call + ".Write".Length;
                string? arguments = ExtractArguments(text, openParen);
                if (arguments is not null)
                {
                    yield return new WriteCall(
                        file,
                        ExtractReceiver(text, call),
                        arguments,
                        SplitTopLevelArguments(arguments));
                }

                index = openParen + 1;
            }
        }
    }

    private static string ExtractReceiver(string text, int callIndex)
    {
        int i = callIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        if (i < 0 || text[i] != '.')
        {
            return string.Empty;
        }

        int dotIndex = i;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        if (i >= 0 && text[i] == ')')
        {
            int depth = 0;
            int cursor = i;
            while (cursor >= 0)
            {
                if (text[cursor] == ')')
                {
                    depth++;
                }
                else if (text[cursor] == '(')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                cursor--;
            }

            cursor--;
            while (cursor >= 0 && (char.IsLetterOrDigit(text[cursor]) || text[cursor] is '_' or '.' or '>'))
            {
                cursor--;
            }

            return text[(cursor + 1)..dotIndex].Trim();
        }

        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '?'))
        {
            i--;
        }

        return text[(i + 1)..dotIndex].Trim();
    }

    private static string? ExtractArguments(string text, int openParen)
    {
        int depth = 0;
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
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(openParen + 1)..i];
                }
            }

            i++;
        }

        return null;
    }

    private static IReadOnlyList<string> SplitTopLevelArguments(string arguments)
    {
        List<string> result = new();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c is '(' or '[' or '{' or '<')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(arguments[start..i].Trim());
                start = i + 1;
            }
        }

        string tail = arguments[start..].Trim();
        if (tail.Length > 0 || result.Count > 0)
        {
            result.Add(tail);
        }

        return result;
    }

    private sealed record WriteCall(
        string Path,
        string Receiver,
        string ArgumentsText,
        IReadOnlyList<string> Arguments)
    {
        /// <summary>
        /// Вызов явной перегрузки Write(eventId, level, ...), где первый аргумент — EventId.
        /// </summary>
        public bool IsExplicitOverload =>
            Arguments.Count > 1 && Arguments[1].StartsWith("LogLevel.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Возвращает тело метода по сигнатуре, чтобы проверять только целевой участок кода.
    /// </summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BePositive($"метод '{signature}' должен существовать");

        var bodyStart = source.IndexOf('{', start);
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
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

    private static IEnumerable<string> FindProductionSources()
    {
        return Directory.EnumerateFiles(AppSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<(string Path, string Statement)> EnumerateLogCalls()
    {
        foreach (var file in FindProductionSources())
        {
            var text = File.ReadAllText(file);
            foreach (var statement in SplitStatements(text))
            {
                if (statement.Contains(".Write(", StringComparison.Ordinal))
                {
                    yield return (file, statement);
                }
            }
        }
    }

    /// <summary>
    /// Делит исходник на операторы по ';', игнорируя разделители внутри строк, символьных литералов и комментариев.
    /// Это исключает ложные срабатывания, когда проверяемая конструкция находится в соседнем операторе.
    /// </summary>
    private static IEnumerable<string> SplitStatements(string text)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

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
                var quote = c;
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

            if (c == ';')
            {
                yield return text[start..i];
                start = i + 1;
            }

            i++;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>
    /// Удаляет литерал EventId из оператора, чтобы ключевые слова проверялись только по тексту сообщения.
    /// </summary>
    private static string StripEventId(string statement)
    {
        return Regex.Replace(
            statement,
            @"Write\(\s*""[^""]+""",
            "Write(",
            RegexOptions.CultureInvariant);
    }

    private static string FirstLine(string statement)
    {
        var line = statement.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }

    private static string Relative(string path)
    {
        var root = Path.GetFullPath(AppSourceDirectory + Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : Path.GetFileName(full);
    }

    private static HashSet<string> ReadAllowedPropertyKeys()
    {
        var redactorPath = Path.Combine(AppSourceDirectory, "Diagnostics", "LogRedactor.cs");
        var text = File.ReadAllText(redactorPath);
        var block = Regex.Match(
            text,
            "AllowedPropertyKeys = new\\(StringComparer\\.OrdinalIgnoreCase\\)\\s*\\{(?<body>.*?)\\};",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        block.Success.Should().BeTrue("allowlist свойств должен быть найден в LogRedactor");

        return Regex.Matches(block.Groups["body"].Value, "\"(?<key>[^\"]+)\"")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveAppSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "KTools.App");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "KTools.App.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Каталог KTools.App не найден");
    }
}
