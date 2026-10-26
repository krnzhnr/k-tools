// -*- coding: utf-8 -*-
using System;
using System.IO;
using FluentAssertions;
using KTools_App.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Converters;

/// <summary>
/// Проверки раскраски строк журнала стандартными кистями темы.
/// Чистый маппинг тестируется напрямую, соответствие шаблонов в LogPage.xaml —
/// текстовой проверкой разметки без XAML-движка.
/// </summary>
[TestClass]
public class LogLevelThemeTests
{
    [TestMethod]
    [DataRow(LogLevel.Debug, "TextFillColorTertiaryBrush")]
    [DataRow(LogLevel.Info, "TextFillColorPrimaryBrush")]
    [DataRow(LogLevel.Warning, "SystemFillColorCautionBrush")]
    [DataRow(LogLevel.Error, "SystemFillColorCriticalBrush")]
    [DataRow(LogLevel.Fatal, "SystemFillColorCriticalBrush")]
    [DataRow((LogLevel)999, "TextFillColorPrimaryBrush")]
    public void ResolveBrushResourceKey_Level_ReturnsStandardBrushKey(LogLevel level, string expected)
    {
        LogLevelStyle.ResolveBrushResourceKey(level).Should().Be(expected);
    }

    [TestMethod]
    [DataRow(LogLevel.Fatal, true)]
    [DataRow(LogLevel.Error, false)]
    [DataRow(LogLevel.Warning, false)]
    [DataRow(LogLevel.Info, false)]
    [DataRow(LogLevel.Debug, false)]
    public void IsEmphasized_Level_ResolvesExpected(LogLevel level, bool expected)
    {
        LogLevelStyle.IsEmphasized(level).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("DebugLogTemplate", "TextFillColorTertiaryBrush")]
    [DataRow("InfoLogTemplate", "TextFillColorPrimaryBrush")]
    [DataRow("WarningLogTemplate", "SystemFillColorCautionBrush")]
    [DataRow("ErrorLogTemplate", "SystemFillColorCriticalBrush")]
    [DataRow("FatalLogTemplate", "SystemFillColorCriticalBrush")]
    public void LogPageXaml_Template_UsesStandardThemeBrush(string templateKey, string brushKey)
    {
        string content = ReadLogPageXaml();

        ExtractTemplateBlock(content, templateKey)
            .Should().Contain("{ThemeResource " + brushKey + "}");
    }

    [TestMethod]
    public void LogPageXaml_FatalTemplate_IsEmphasized()
    {
        string content = ReadLogPageXaml();

        ExtractTemplateBlock(content, "FatalLogTemplate")
            .Should().Contain("SemiBold");
    }

    [TestMethod]
    public void LogPageXaml_LogList_UsesTemplateSelector()
    {
        string content = ReadLogPageXaml();

        content.Should().Contain("LogLevelTemplateSelector");
        content.Should().NotContain("LogLevelToBrushConverter");
    }

    private static string ReadLogPageXaml()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "UI", "Pages", "LogPage.xaml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = Path.GetDirectoryName(dir)!;
        }

        throw new InvalidOperationException("Не удалось найти LogPage.xaml от " + AppContext.BaseDirectory);
    }

    private static string ExtractTemplateBlock(string content, string templateKey)
    {
        string marker = "x:Key=\"" + templateKey + "\"";
        int start = content.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "шаблон {0} обязан существовать", templateKey);
        int end = content.IndexOf("x:Key=\"", start + marker.Length, StringComparison.Ordinal);
        return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
    }
}
