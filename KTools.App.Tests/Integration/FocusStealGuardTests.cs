// -*- coding: utf-8 -*-
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Integration;

/// <summary>
/// Регрессионные тесты сброса фокуса при клике (поле шаблона имени теряло фокус
/// сразу после клика: OriginalSource — внутренний элемент шаблона TextBox,
/// а не сам TextBox, поэтому проверка обязана обходить визуальное дерево вверх).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class FocusStealGuardTests
{
    /// <summary>
    /// Находит корень репозитория от каталога запуска тестов.
    /// </summary>
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "KTools.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        Assert.Fail("Корень репозитория с KTools.sln не найден от каталога запуска тестов");
        throw new InvalidOperationException("Unreachable");
    }

    [TestMethod]
    public void IsInsideTextInputElement_NullSource_ReturnsFalse()
    {
        KTools_App.Core.FocusHelper.IsInsideTextInputElement(null).Should().BeFalse(
            "пустой источник нажатия не может находиться внутри поля ввода");
    }

    [TestMethod]
    [DataRow("KTools.App/MainPage.xaml.cs")]
    [DataRow("KTools.App/UI/Controls/ScriptSettingsControl.xaml.cs")]
    [DataRow("KTools.App/UI/Pages/SettingsPage.xaml.cs")]
    public void PointerPressedHandler_UsesVisualTreeWalk_DoesNotStealFocusFromTextBox(string relativePath)
    {
        string fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).Should().BeTrue($"файл {relativePath} обязан существовать");
        string source = File.ReadAllText(fullPath);
        source.Should().Contain("IsInsideTextInputElement",
            $"обработчик в {relativePath} обязан проверять дерево вверх, иначе клик в поле ввода сразу теряет фокус");
    }
}
