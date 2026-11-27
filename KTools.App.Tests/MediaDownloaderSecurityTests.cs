// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;

using KTools_App.Infrastructure;
using KTools_App.Scripts;

namespace KTools_App.Tests;

/// <summary>
/// Тесты защиты от инъекции аргументов yt-dlp (issue #82).
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MediaDownloaderSecurityTests
{
    /// <summary>
    /// Проверяет отклонение инъекционной строки в поле ссылки.
    /// </summary>
    [TestMethod]
    public void IsValidDownloadUrl_InjectionPayload_ReturnsFalse()
    {
        // Arrange: строка из критериев приёмки ишью.
        string payload = "https://x.com/a\" --exec \"calc";

        // Act.
        bool actual = MediaDownloaderScript.IsValidDownloadUrl(payload);

        // Assert: ссылка отклоняется до запуска.
        actual.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет приём обычной https-ссылки.
    /// </summary>
    [TestMethod]
    public void IsValidDownloadUrl_OrdinaryHttpsUrl_ReturnsTrue()
    {
        // Act.
        bool actual = MediaDownloaderScript.IsValidDownloadUrl("https://example.com/watch?v=abc123");

        // Assert.
        actual.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет отбрасывание вредоносного кода субтитров из JSON сайта.
    /// </summary>
    [TestMethod]
    public void IsSafeSubtitleCode_MaliciousName_ReturnsFalse()
    {
        // Arrange: ключ JSON враждебного сайта с попыткой вырваться из кавычек.
        string malicious = "ru\" --exec \"calc";

        // Act.
        bool actual = MediaDownloaderScript.IsSafeSubtitleCode(malicious);

        // Assert: код не должен попадать в командную строку.
        actual.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет приём обычных кодов языков.
    /// </summary>
    [TestMethod]
    public void IsSafeSubtitleCode_OrdinaryCode_ReturnsTrue()
    {
        // Act / Assert.
        MediaDownloaderScript.IsSafeSubtitleCode("ru").Should().BeTrue();
        MediaDownloaderScript.IsSafeSubtitleCode("en").Should().BeTrue();
    }

    /// <summary>
    /// Проверяет экранирование кавычки: payload остаётся одним аргументом.
    /// </summary>
    [TestMethod]
    public void EscapeArgument_QuotePayload_StaysSingleQuotedArgument()
    {
        // Arrange.
        string payload = "https://x.com/a\" --exec \"calc";

        // Act.
        string escaped = DirectProcessRunner.EscapeArgument(payload);

        // Assert: результат заключён в одну пару кавычек, внутренние кавычки экранированы.
        escaped.Should().StartWith("\"");
        escaped.Should().EndWith("\"");
        escaped.Should().Contain("\\\"");
    }
}
