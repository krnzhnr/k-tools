// -*- coding: utf-8 -*-
using System;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Регрессия пустого экспандера субтитров: дочерние KeywordList-поля
/// sub_keywords/strip_keywords внутри sub_advanced_options возвращали null
/// из CreateSettingInputControl (ветки SettingType.KeywordList в switch не было),
/// поэтому карточки экспандера строились с Content=null.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class ScriptSettingsKeywordListTests
{
    /// <summary>
    /// Прямой вызов CreateSettingInputControl невозможен в headless MSTest-процессе:
    /// конструктор ScriptSettingsControl требует App.Services и InitializeComponent
    /// (XAML-движок, см. WindowHeadlessProbeTests), поэтому тело метода проверяется
    /// по исходнику — приём как в PerformanceGuardTests и MainPagePointerTests.
    /// До фикса падает: в методе нет ветки KeywordList, дочерний контрол = null.
    /// </summary>
    [TestMethod]
    public void CreateSettingInputControl_KeywordList_ReturnsNonNull()
    {
        // Arrange
        string source = File.ReadAllText(GetControlSourceFile());

        // Act — вырезаем только тело CreateSettingInputControl, чтобы не засчитать
        // обработку верхнего уровня (CreateKeywordListContainer вне метода),
        // где KeywordList уже строится отдельным путём.
        int methodStart = source.IndexOf(
            "CreateSettingInputControl(SettingField", StringComparison.Ordinal);
        methodStart.Should().BeGreaterThan(
            -1, "метод CreateSettingInputControl обязан существовать в ScriptSettingsControl");
        int returnPos = source.IndexOf("return inputControl;", methodStart, StringComparison.Ordinal);
        returnPos.Should().BeGreaterThan(
            methodStart, "метод CreateSettingInputControl обязан возвращать inputControl");
        string methodBody = source.Substring(methodStart, returnPos - methodStart);

        // Assert
        methodBody.Should().Contain("case SettingType.KeywordList",
            "дочерний KeywordList внутри Expander обязан иметь ветку в CreateSettingInputControl, иначе Content=null и экспандер пустой");
        methodBody.Should().Contain("CreateKeywordListContainer",
            "ветка KeywordList обязана переиспользовать существующий билдер CreateKeywordListContainer");
    }

    private static string GetControlSourceFile()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "KTools.sln")))
                {
                    string path = Path.Combine(
                        dir.FullName, "KTools.App", "UI", "Controls", "ScriptSettingsControl.xaml.cs");
                    if (File.Exists(path))
                    {
                        return path;
                    }

                    break;
                }

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException(
            "Не удалось найти исходник ScriptSettingsControl.xaml.cs от корня с KTools.sln.");
    }
}
