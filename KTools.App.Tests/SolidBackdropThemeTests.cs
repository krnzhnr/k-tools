// -*- coding: utf-8 -*-
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Проверки контракта сплошного фона режима «Обычный» (ишью 43).
/// Тёмная тема — единый #080808 вместо чистого чёрного,
/// светлая — стандартный цвет палитры SolidBackgroundFillColorBase.
/// </summary>
[TestClass]
public class SolidBackdropThemeTests
{
    [TestMethod]
    public void ResolveBackgroundHex_LightTheme_ReturnsPaletteBase()
    {
        string actual = SolidBackdropTheme.ResolveBackgroundHex(true);

        actual.Should().Be("#F3F3F3");
    }

    [TestMethod]
    public void ResolveBackgroundHex_DarkTheme_ReturnsCustomDark()
    {
        string actual = SolidBackdropTheme.ResolveBackgroundHex(false);

        actual.Should().Be("#080808");
    }

    [TestMethod]
    public void DarkBackgroundHex_IsNotPureBlack()
    {
        SolidBackdropTheme.DarkBackgroundHex.Should().NotBe("#000000");
    }

    [TestMethod]
    public void LightBackgroundHex_IsNotPureWhite()
    {
        SolidBackdropTheme.LightBackgroundHex.Should().NotBe("#FFFFFF");
    }

    [TestMethod]
    [DataRow("Light", "Dark", true)]
    [DataRow("Dark", "Light", false)]
    [DataRow("System", "Light", true)]
    [DataRow("System", "Dark", false)]
    [DataRow(null, "Light", true)]
    [DataRow(null, "Dark", false)]
    public void IsLightEffective_VariousInputs_ResolvesExpected(string? requested, string? actualTheme, bool expected)
    {
        SolidBackdropTheme.IsLightEffective(requested, actualTheme).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("None", true)]
    [DataRow("Solid", true)]
    [DataRow("Mica", false)]
    [DataRow("Acrylic", false)]
    [DataRow(null, false)]
    [DataRow("вздор", false)]
    public void RequiresContentBackgroundOverride_VariousTypes_ResolvesExpected(string? backdrop, bool expected)
    {
        SolidBackdropTheme.RequiresContentBackgroundOverride(backdrop).Should().Be(expected);
    }

    [TestMethod]
    public void AppXaml_SolidBackdropBrush_UsesThemeResources()
    {
        string dir = AppContext.BaseDirectory;
        string? appXaml = null;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "App.xaml");
            if (File.Exists(candidate))
            {
                appXaml = candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir)!;
        }

        appXaml.Should().NotBeNull("корень проекта KTools.App обязан находиться");
        string content = File.ReadAllText(appXaml!);
        content.Should().Contain("SolidBackdropBackgroundBrush");
        content.Should().Contain("#080808");
        content.Should().Contain("#F3F3F3");
    }

    [TestMethod]
    public void MainWindowXaml_SolidBackdropOverlay_UsesThemeResource()
    {
        string dir = AppContext.BaseDirectory;
        string? mainWindowXaml = null;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "KTools.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                mainWindowXaml = candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir)!;
        }

        mainWindowXaml.Should().NotBeNull("корень проекта KTools.App обязан находиться");
        string content = File.ReadAllText(mainWindowXaml!);
        content.Should().Contain("SolidBackdropOverlay");
        content.Should().Contain("{ThemeResource SolidBackdropBackgroundBrush}");
    }
}
