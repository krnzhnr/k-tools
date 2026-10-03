// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FluentAssertions;
using KTools_App.Diagnostics;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogRedactorTests
{
    private readonly LogRedactor _redactor = new();

    private static string Control(char raw) => raw.ToString();

    private static string AsString(IReadOnlyDictionary<string, object?> properties, string key) =>
        Convert.ToString(properties[key], CultureInfo.InvariantCulture) ?? string.Empty;

    [TestMethod]
    public void RedactMessage_UrlWithQueryAndFragment_DropsQueryValuesAndFragment()
    {
        const string Sentinel = "SENTINEL-TOKEN-VALUE";
        string message = $"Загрузка https://cdn.example.com/media/video.mp4?token={Sentinel}&sig=zzz#private-fragment";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(Sentinel);
        result.Should().NotContain("sig=zzz");
        result.Should().NotContain("private-fragment");
        result.Should().Contain("https://cdn.example.com/");
        result.Should().Contain("video.mp4");
        result.Should().Contain(LogRedactor.RedactedMarker);
    }

    [TestMethod]
    public void RedactMessage_UrlWithUserInfo_RemovesCredentials()
    {
        const string User = "SENTINEL-USER";
        const string Password = "SENTINEL-PASSWORD";
        string message = $"Подключение https://{User}:{Password}@api.example.com/v1/items?key=abc";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(User);
        result.Should().NotContain(Password);
        result.Should().NotContain("key=abc");
        result.Should().Contain("api.example.com");
    }

    [TestMethod]
    public void RedactMessage_AbsolutePath_ReducesToFileName()
    {
        string message = @"Файл C:\Users\ivan.petrov\Documents\Секретное видео.mkv обработан за 4 с";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain("ivan.petrov");
        result.Should().NotContain(@"\Users\");
        result.Should().NotContain(@"C:\");
        result.Should().Contain("Секретное видео.mkv");
        result.Should().Contain("обработан за 4 с");
    }

    [TestMethod]
    public void RedactMessage_DriveRelativePath_ReducesToFileName()
    {
        string result = _redactor.RedactMessage("Файл C:secret.txt и Z:foo");

        result.Should().Contain("secret.txt");
        result.Should().Contain("foo");
        result.Should().NotContain("C:secret.txt");
        result.Should().NotContain("Z:foo");
    }

    [TestMethod]
    public void SanitizeIdentifier_DriveRelativePath_IsUnknown()
    {
        _redactor.SanitizeIdentifier("C:secret.txt").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier("Z:foo").Should().Be(LogRedactor.UnknownIdentifier);
    }

    [TestMethod]
    public void RedactMessage_ExtendedLengthPath_ReducesToFileName()
    {
        string message = @"Файл \\?\C:\very\long\path\video.mkv обработан";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(@"\\?\");
        result.Should().NotContain("very");
        result.Should().Contain("video.mkv");
    }

    [TestMethod]
    public void RedactMessage_DevicePath_IsFullyRedacted()
    {
        string result = _redactor.RedactMessage(@"Обращение к \\.\PhysicalDrive0 завершено");

        result.Should().NotContain("PhysicalDrive0");
        result.Should().NotContain(@"\\.\");
    }

    [TestMethod]
    public void RedactMessage_UncPath_ReducesToFileName()
    {
        string message = @"Сетевой ресурс \\server\share\private\document.docx недоступен";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain("server");
        result.Should().NotContain("share");
        result.Should().Contain("document.docx");
    }

    [TestMethod]
    public void RedactMessage_SecretAssignments_AreReplaced()
    {
        const string Sentinel = "SENTINEL-SECRET";
        string message = $"password={Sentinel} api_key: {Sentinel} secret=\"{Sentinel}\"";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(Sentinel);
        result.Should().Contain(LogRedactor.RedactedMarker);
    }

    [TestMethod]
    public void RedactMessage_AuthorizationAndCookieHeaders_AreFullyReplaced()
    {
        string message = "Authorization: Bearer SENTINEL-JWT-TOKEN" + "\r\n" + "Cookie: sessionid=SENTINEL-COOKIE; theme=dark";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain("SENTINEL-JWT-TOKEN");
        result.Should().NotContain("SENTINEL-COOKIE");
        result.Should().NotContain("theme=dark");
        result.Should().Contain("Authorization: " + LogRedactor.RedactedMarker);
        result.Should().Contain("Cookie: " + LogRedactor.RedactedMarker);
    }

    [TestMethod]
    public void RedactMessage_ControlCharacters_AreNeutralized()
    {
        string message = "первая" + Control((char)0) + "строка" + Control((char)7) + "вторая\tтретья";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(Control((char)0));
        result.Should().NotContain(Control((char)7));
        result.Should().Contain("первая строка");
    }

    [TestMethod]
    public void RedactMessage_PlainText_IsPreserved()
    {
        const string Message = "Очередь обработки запущена: 12 файлов";

        _redactor.RedactMessage(Message).Should().Be(Message, "обычный текст без секретов не изменяется");
    }

    [TestMethod]
    public void RedactMessage_Oversized_IsBoundedToMessageLimit()
    {
        string oversized = new string('a', LogRedactor.DefaultMaxMessageLength * 4);

        string result = _redactor.RedactMessage(oversized);

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxMessageLength);
        result.Should().Contain(LogRedactor.TruncationMarker);
    }

    [TestMethod]
    public void RedactToken_MultilineValue_IsCollapsedToSingleLine()
    {
        string result = _redactor.RedactToken(@"C:\dir\file.txt`nвторая строка");

        result.Should().NotContain("\n");
        result.Should().NotContain("\r");
        result.Should().Contain("file.txt");
    }

    [TestMethod]
    public void RedactDetail_Oversized_IsBoundedToDetailLimit()
    {
        string result = _redactor.RedactDetail(new string('b', LogRedactor.DefaultMaxDetailLength * 2));

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxDetailLength);
    }

    [TestMethod]
    public void RedactProperties_UnknownKey_UsesSequentialMarkerInsteadOfOriginalName()
    {
        var properties = new Dictionary<string, object?> { ["UnlistedKey"] = "SENTINEL-VALUE" };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        result.Should().NotContainKey("UnlistedKey", "исходное имя неизвестного ключа не сохраняется");
        result.Should().ContainKey(LogRedactor.DroppedPropertyPrefix + "01");
        AsString(result, LogRedactor.DroppedPropertyPrefix + "01").Should().Be(LogRedactor.RedactedMarker);
        result.Values.Should().NotContain("SENTINEL-VALUE");
    }

    [TestMethod]
    public void RedactProperties_SensitiveKey_IsDroppedWithSequentialMarkerAndCount()
    {
        var properties = new Dictionary<string, object?>
        {
            ["Password"] = "SENTINEL-PASSWORD",
            ["QueueSize"] = 12
        };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        result.Should().NotContainKey("Password");
        result.Should().ContainKey(LogRedactor.RedactedPropertyPrefix + "01");
        AsString(result, LogRedactor.RedactedPropertyPrefix + "01").Should().Be(LogRedactor.RedactedMarker);
         Convert.ToInt32(result[LogRedactor.RedactedCountProperty], CultureInfo.InvariantCulture).Should().Be(1);
         AsString(result, LogRedactor.RedactionReasonProperty).Should().Be(LogRedactor.SensitiveKeyCategory);
         result["QueueSize"].Should().Be(12);

        result.Values.Should().NotContain("SENTINEL-PASSWORD");
    }

    [TestMethod]
    public void RedactProperties_AllowlistedValue_IsRedactedAndSingleLine()
    {
        var properties = new Dictionary<string, object?> { ["InputName"] = "C:\\dir\\file.mkv\nsecond" };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        string value = AsString(result, "InputName");
        value.Should().Be("file.mkv second");
        value.Should().NotContain("\n");
    }

    [TestMethod]
    public void RedactProperties_InjectionInKey_IsNeutralized()
    {
        var properties = new Dictionary<string, object?>
        {
            ["Queue\r\nSize\":{\"injected\":\"value"] = "SENTINEL-VALUE"
        };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        foreach (string key in result.Keys)
        {
            key.Should().NotContain("\r");
            key.Should().NotContain("\n");
            key.Should().NotContain("\"");
        }
    }

    [TestMethod]
    public void RedactProperties_AllowedLargePayloadUpToLimit_IsPreserved()
    {
        var properties = new Dictionary<string, object?>
        {
            ["OutputTail"] = new string('c', 4000),
            ["Fingerprint"] = new string('d', 1000)
        };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        AsString(result, "OutputTail").Should().HaveLength(4000, "allowlisted значение меньше лимита сохраняется полностью");
        AsString(result, "Fingerprint").Should().HaveLength(1000);
        result.Should().NotContainKey(LogRedactor.DroppedPropertiesProperty);
    }

    [TestMethod]
    public void RedactProperties_Oversized_IsBoundedToPropertiesLimit()
    {
        string[] allowed = { "OutputTail", "Reason", "FileName", "Stage", "Fingerprint", "CommandHash", "ValueHash", "ErrorCode", "InputName", "OutputName" };
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        foreach (string key in allowed)
        {
            properties[key] = new string('c', 4000);
        }

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        int size = result.Sum(pair => LogRedactor.EstimateSize(pair.Key) + LogRedactor.EstimateSize(pair.Value));
        size.Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxPropertiesLength + 128);
        Convert.ToInt32(result[LogRedactor.DroppedPropertiesProperty], CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void RedactProperties_ManyUnknownKeys_AreBoundedByCountAndBudget()
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        for (int index = 0; index < 2000; index++)
        {
            properties["Unlisted" + index.ToString("D4", CultureInfo.InvariantCulture)] = new string('c', 64);
        }

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        result.Count.Should().BeLessThanOrEqualTo(LogRedactor.MaxMarkerEntries + 8);
        int size = result.Sum(pair => LogRedactor.EstimateSize(pair.Key) + LogRedactor.EstimateSize(pair.Value));
        size.Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxPropertiesLength + 128);
        Convert.ToInt32(result[LogRedactor.MarkedPropertiesProperty], CultureInfo.InvariantCulture).Should().Be(2000);
    }

    [TestMethod]
    public void RedactProperties_NullOrEmpty_ReturnsSharedEmptyDictionary()
    {
        _redactor.RedactProperties(null).Should().BeEmpty();
        _redactor.RedactProperties(new Dictionary<string, object?>()).Should().BeEmpty();
    }

    [TestMethod]
    public void RedactProperties_TypedValues_ArePreserved()
    {
        var properties = new Dictionary<string, object?>
        {
            ["Succeeded"] = 10L,
            ["Retryable"] = true,
            ["Percent"] = 12.5d
        };

        IReadOnlyDictionary<string, object?> result = _redactor.RedactProperties(properties);

        result["Succeeded"].Should().Be(10L);
        result["Retryable"].Should().Be(true);
        result["Percent"].Should().Be(12.5d);
    }

    [TestMethod]
    public void SanitizeIdentifier_RejectsUrlsPathsAndSecrets()
    {
        _redactor.SanitizeIdentifier("https://cdn.example.com/a.mkv?token=abc").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier(@"C:\Users\ivan\a.mkv").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier("password=secret").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier("token: abc").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier("with space").Should().Be("with_space");
        _redactor.SanitizeIdentifier("Log Service/Log\"Service").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier("   ").Should().Be(LogRedactor.UnknownIdentifier);
        _redactor.SanitizeIdentifier(null).Should().Be(LogRedactor.UnknownIdentifier);
    }

    [TestMethod]
    public void IsValidEventId_AcceptsStableTokensAndRejectsEverythingElse()
    {
        LogRedactor.IsValidEventId("exec.item.succeeded").Should().BeTrue();
        LogRedactor.IsValidEventId("EXEC_QUEUE_STARTED").Should().BeTrue();
        LogRedactor.IsValidEventId("legacy.settingsmanager.info").Should().BeTrue();
        LogRedactor.IsValidEventId("app.started").Should().BeTrue();

        LogRedactor.IsValidEventId(null).Should().BeFalse();
        LogRedactor.IsValidEventId("").Should().BeFalse();
        LogRedactor.IsValidEventId("has space").Should().BeFalse();
        LogRedactor.IsValidEventId("\"quoted\"").Should().BeFalse();
        LogRedactor.IsValidEventId("https://host/a").Should().BeFalse();
        LogRedactor.IsValidEventId("C:\\dir\\file").Should().BeFalse();
        LogRedactor.IsValidEventId("1leading.digit").Should().BeFalse();
        LogRedactor.IsValidEventId(new string('a', 100)).Should().BeFalse();
    }

    [TestMethod]
    public void SanitizeEventId_InvalidValue_UsesSafeGeneratedIdentifier()
    {
        _redactor.SanitizeEventId("valid.id", "fallback.id").Should().Be("valid.id");
        _redactor.SanitizeEventId("https://host/a?token=abc", "fallback.id").Should().Be("fallback.id");
        _redactor.SanitizeEventId("bad id", "fallback.id").Should().Be("fallback.id");
        _redactor.SanitizeEventId("bad id", "also bad").Should().Be(LogRedactor.UnknownIdentifier);
    }

    [TestMethod]
    public void RedactException_SecretsInStackAndMessage_AreRemovedAndBounded()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("ошибка доступа https://user:SENTINEL-PASS@host/path/f.mkv?token=SENTINEL-TOKEN");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        ExceptionInfo? result = _redactor.RedactException(ExceptionInfo.FromException(exception));

        result.Should().NotBeNull();
        result!.Message.Should().NotContain("SENTINEL-PASS");
        result.Message.Should().NotContain("SENTINEL-TOKEN");
        Encoding.UTF8.GetByteCount(result.Message).Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxDetailLength);
    }

    [TestMethod]
    public void RedactException_StackWithPathAndSecret_IsRedacted()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("сбой @" + @"C:\Users\ivan\secret\app.dll password=SENTINEL-PASSWORD");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        ExceptionInfo? result = _redactor.RedactException(ExceptionInfo.FromException(exception));

        result!.StackTrace.Should().NotContain("ivan");
        result.StackTrace.Should().NotContain("SENTINEL-PASSWORD");
        result.StackTrace.Should().NotContain(@"C:\");
    }

    [TestMethod]
    public void RedactException_DeepChain_SharesDetailBudget()
    {
        Exception exception = new InvalidOperationException("верхний", new ArgumentException("средний", new FormatException("нижний")));

        ExceptionInfo? result = _redactor.RedactException(ExceptionInfo.FromException(exception, 8));

        result!.ChainLength.Should().Be(3);
        int total = result.EnumerateChain().Sum(node =>
            Encoding.UTF8.GetByteCount(node.Message) + Encoding.UTF8.GetByteCount(node.StackTrace ?? string.Empty));
        total.Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxDetailLength + 512);
    }

    [TestMethod]
    public void RedactException_Null_ReturnsNull()
    {
        _redactor.RedactException(null).Should().BeNull();
    }

    [TestMethod]
    public void Redaction_DoesNotClassifyArbitraryCallerText()
    {
        string text = "Пользователь описал обычную последовательность действий без секретов";

        string result = _redactor.RedactMessage(text);

        result.Should().Be(text, "универсальный redactor не пытается классифицировать произвольный текст вызывающего кода");
    }

    [TestMethod]
    public void RedactMessage_TranscriptBoundedByMessageLimit()
    {
        string transcript = new('я', 20000);

        string result = _redactor.RedactMessage(transcript);

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(LogRedactor.DefaultMaxMessageLength);
        result.Should().Contain(LogRedactor.TruncationMarker);
    }

    [TestMethod]
    public void IsSensitiveKeyName_DetectsSecretLikeNames()
    {
        LogRedactor.IsSensitiveKeyName("Password").Should().BeTrue();
        LogRedactor.IsSensitiveKeyName("api_key").Should().BeTrue();
        LogRedactor.IsSensitiveKeyName("X-Auth-Token").Should().BeTrue();
        LogRedactor.IsSensitiveKeyName("SetCookie").Should().BeTrue();
        LogRedactor.IsSensitiveKeyName("privateKey").Should().BeTrue();
        LogRedactor.IsSensitiveKeyName("QueueSize").Should().BeFalse();
        LogRedactor.IsSensitiveKeyName(null).Should().BeFalse();
    }

    [TestMethod]
    public void SingleLine_ReplacesLineBreaks()
    {
        LogRedactor.SingleLine("a\r\nb\tc").Should().Be("a  b c");
        LogRedactor.SingleLine("plain").Should().Be("plain");
        LogRedactor.SingleLine(string.Empty).Should().BeEmpty();
    }

    [TestMethod]
    public void RedactMessage_ValueWithoutRedactionTriggers_IsPreservedWithoutChanges()
    {
        const string Message = "Элемент очереди clip_0001_1080p.mkv обработан за 4 с (item-0001-7f3ab91c5d2e4860)";

        string result = _redactor.RedactMessage(Message);

        result.Should().Be(Message, "значение без ':' , '=' и пары обратных косых черт не может совпасть ни с одним шаблоном redaction");
        result.Should().NotContain(LogRedactor.RedactedMarker);
        LogRedactor.LooksUnsafe(Message).Should().BeFalse("идентификатор без триггеров redaction остаётся безопасным");
    }

    [TestMethod]
    public void RedactMessage_ControlCharactersWithoutRedactionTriggers_AreNeutralized()
    {
        string message = "первая" + Control((char)0) + "строка" + Control((char)7) + "вторая";

        string result = _redactor.RedactMessage(message);

        result.Should().NotContain(Control((char)0), "быстрый путь обязан нейтрализовать управляющие символы");
        result.Should().NotContain(Control((char)7));
        result.Should().Contain("первая строка");
    }

    [TestMethod]
    public void LooksUnsafe_EveryRedactionTrigger_IsStillDetected()
    {
        LogRedactor.LooksUnsafe("item-0001-7f3ab91c5d2e4860").Should().BeFalse();
        LogRedactor.LooksUnsafe("https://cdn.example.com/a.mp4").Should().BeTrue();
        LogRedactor.LooksUnsafe(@"\\server\share\doc.docx").Should().BeTrue();
        LogRedactor.LooksUnsafe(@"C:\Users\ivan\doc.docx").Should().BeTrue();
        LogRedactor.LooksUnsafe("C:secret.txt").Should().BeTrue();
        LogRedactor.LooksUnsafe("password=secret").Should().BeTrue();
        LogRedactor.LooksUnsafe("api_key: secret").Should().BeTrue();
        LogRedactor.LooksUnsafe("Authorization: Bearer token").Should().BeTrue();
        LogRedactor.LooksUnsafe("token?x=1").Should().BeTrue();
    }

    [TestMethod]
    public void RedactMessage_TriggeredPatterns_AreStillRedactedAfterFastPath()
    {
        const string UrlSentinel = "SENTINEL-URL";
        const string AssignmentSentinel = "SENTINEL-ASSIGN";
        const string HeaderSentinel = "SENTINEL-HEADER";
        const string UncSentinel = "SENTINEL-UNC";
        const string DriveSentinel = "SENTINEL-DRIVE";

        _redactor.RedactMessage($"https://cdn.example.com/a.mp4?token={UrlSentinel}")
            .Should().NotContain(UrlSentinel, "URL-шаблон обязан сработать");
        _redactor.RedactMessage($"password={AssignmentSentinel}")
            .Should().NotContain(AssignmentSentinel, "шаблон присваивания секрета обязан сработать");
        _redactor.RedactMessage($"Authorization: Bearer {HeaderSentinel}")
            .Should().NotContain(HeaderSentinel, "шаблон заголовка обязан сработать");
        _redactor.RedactMessage($@"\\server\share\{UncSentinel}.docx")
            .Should().NotContain("server", "UNC-шаблон обязан сработать");
        _redactor.RedactMessage($@"Файл C:secret-{DriveSentinel}.txt")
            .Should().NotContain($"C:secret-{DriveSentinel}.txt", "шаблон относительного пути обязан сработать");
    }

    [TestMethod]
    public void ReadableMachineValue_PreservesSeparators_SoFieldsStayMachineReadable()
    {
        LogRedactor.ReadableMachineValue("Microsoft Windows 10.0.26200")
            .Should().Be("microsoft windows 10.0.26200");
        LogRedactor.ReadableMachineValue("ru-RU").Should().Be("ru-ru");
        LogRedactor.ReadableMachineValue("script:кодирование видео")
            .Should().Be("script:кодирование видео");
    }

    [TestMethod]
    public void ReadableMachineValue_UnsafeOrEmpty_ReturnsUnknown()
    {
        LogRedactor.ReadableMachineValue("https://example.com/a.mp4?token=SENTINEL")
            .Should().Be("unknown");
        LogRedactor.ReadableMachineValue(@"C:\Users\someone\file.mkv").Should().Be("unknown");
        LogRedactor.ReadableMachineValue("   ").Should().Be("unknown");
        LogRedactor.ReadableMachineValue(null).Should().Be("unknown");
    }

    [TestMethod]
    public void CompactSafeToken_StillCompactsSeparators_ForPrivacySensitiveTokens()
    {
        LogRedactor.CompactSafeToken("pending_args_write").Should().Be("pendingargswrite");
    }
}
