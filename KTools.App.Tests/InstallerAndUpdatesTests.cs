// -*- coding: utf-8 -*-
using System;
using System.IO;
using FluentAssertions;
using KTools_App.Services.Implementations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Комплексные тесты механизмов обновления и сценариев Inno Setup,
/// предотвращающих нежелательное пересоздание ярлыка приложения на рабочем столе.
/// Все комментарии и тестовые утверждения оформлены строго на русском языке.
/// </summary>
[TestClass]
public sealed class InstallerAndUpdatesTests
{
    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KTools.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Не удалось найти корень репозитория с файлом KTools.sln.");
    }

    /// <summary>
    /// Возвращает путь к сгенерированному скрипту инсталлятора либо помечает тест неприменимым:
    /// KTools_CSharp.iss генерируется build_csharp.py и не отслеживается в Git.
    /// </summary>
    private static string GetInstallerScriptPath()
    {
        string root = GetRepositoryRoot();
        string issPath = Path.Combine(root, "KTools_CSharp.iss");

        if (!File.Exists(issPath))
        {
            Assert.Inconclusive(
                "KTools_CSharp.iss отсутствует: файл генерируется скриптом build_csharp.py " +
                "и не отслеживается в Git. Запустите сборку инсталлятора для выполнения проверки.");
        }

        return issPath;
    }

    /// <summary>
    /// Проверяет, что аргументы фонового обновления в UpdateService содержат флаг исключения задачи ярлыка рабочего стола.
    /// </summary>
    [TestMethod]
    public void SilentUpdateArguments_DefaultConfiguration_ContainsDesktopIconExclusion()
    {
        // Arrange & Act
        string arguments = UpdateService.SilentUpdateInstallerArguments;

        // Assert
        arguments.Should().NotBeNullOrWhiteSpace("аргументы установщика должны быть объявлены");
        arguments.Should().Contain("/SILENT", "установка обновлений должна запускаться в тихом режиме");
        arguments.Should().Contain("/SUPPRESSMSGBOXES", "диалоговые окна установщика должны быть подавлены");
        arguments.Should().Contain("/MERGETASKS=\"!desktopicon\"", "задача desktopicon должна быть явно исключена при тихом обновлении");
    }

    /// <summary>
    /// Проверяет, что в скрипте Inno Setup задача desktopicon содержит флаг checkedonce:
    /// он снимает отметку при обнаружении ранее установленной версии приложения.
    /// </summary>
    [TestMethod]
    public void InnoSetupScript_DesktopIconTask_ContainsCheckedOnceFlag()
    {
        // Arrange
        string issPath = GetInstallerScriptPath();

        // Act
        string issContent = File.ReadAllText(issPath);

        // Assert
        issContent.Should().Contain("Name: \"desktopicon\"", "скрипт должен определять задачу desktopicon");
        issContent.Should().MatchRegex(
            @"(?s)Name:\s*""desktopicon"";.*?Flags:\s*[^;\r\n]*checkedonce",
            "задача desktopicon обязана содержать флаг checkedonce для снятия отметки при обновлении");
    }

    /// <summary>
    /// Проверяет, что запись ярлыка на рабочем столе содержит условие Check: ShouldCreateDesktopIcon.
    /// </summary>
    [TestMethod]
    public void InnoSetupScript_DesktopIconEntry_ContainsConditionalCheck()
    {
        // Arrange
        string issPath = GetInstallerScriptPath();

        // Act
        string issContent = File.ReadAllText(issPath);

        // Assert
        issContent.Should().MatchRegex(
            @"(?s)Name:\s*""\{autodesktop\}\\KTools"";.*?Tasks:\s*desktopicon;.*?Check:\s*ShouldCreateDesktopIcon",
            "запись ярлыка {autodesktop}\\KTools обязана иметь проверку Check: ShouldCreateDesktopIcon");
    }

    /// <summary>
    /// Проверяет реализацию вспомогательных функций проверки и синхронизации ярлыка в [Code] скрипта Inno Setup.
    /// </summary>
    [TestMethod]
    public void InnoSetupScript_CodeSection_ImplementsProtectionRoutines()
    {
        // Arrange
        string issPath = GetInstallerScriptPath();

        // Act
        string issContent = File.ReadAllText(issPath);

        // Assert
        issContent.Should().Contain("function ShouldCreateDesktopIcon: Boolean;", "должна быть объявлена функция ShouldCreateDesktopIcon");
        issContent.Should().Contain("function DesktopIconExists: Boolean;", "должна быть объявлена функция DesktopIconExists");
        issContent.Should().Contain("function IsAppInstalled: Boolean;", "должна быть объявлена функция IsAppInstalled");
        issContent.Should().Contain("procedure CurPageChanged(CurPageID: Integer);", "должна быть объявлена процедура CurPageChanged");
        issContent.Should().Contain("wpSelectTasks", "должна обрабатываться страница выбора задач wpSelectTasks");
        issContent.Should().Contain("WizardSelectTasks('!desktopicon')", "при отсутствии ярлыка задача должна программно сниматься");
    }

    /// <summary>
    /// Проверяет, что python-скрипт сборки build_csharp.py генерирует скрипт Inno Setup с защитой от пересоздания ярлыка.
    /// </summary>
    [TestMethod]
    public void BuildScript_InnoSetupTemplate_ContainsDesktopIconGuards()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");
        File.Exists(pyPath).Should().BeTrue("файл build_csharp.py должен существовать в корне проекта");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("checkedonce", "шаблон генерации в build_csharp.py должен содержать флаг checkedonce");
        pyContent.Should().Contain("Check: ShouldCreateDesktopIcon", "шаблон генерации должен содержать вызов функции проверки ярлыка");
        pyContent.Should().Contain("function ShouldCreateDesktopIcon", "шаблон генерации должен определять функцию ShouldCreateDesktopIcon");
        pyContent.Should().Contain("function DesktopIconExists", "шаблон генерации должен определять функцию DesktopIconExists");
        pyContent.Should().Contain("WizardSelectTasks('!desktopicon')", "шаблон генерации должен снимать задачу при отсутствии ярлыка");
    }
}
