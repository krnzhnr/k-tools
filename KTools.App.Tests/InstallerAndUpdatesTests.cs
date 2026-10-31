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
    /// Проверяет, что в скрипте Inno Setup задача desktopicon по умолчанию выключена:
    /// ярлык создаётся только по явной отметке пользователя или при обновлении поверх существующего.
    /// </summary>
    [TestMethod]
    public void InnoSetupScript_DesktopIconTask_IsUncheckedByDefault()
    {
        // Arrange
        string issPath = GetInstallerScriptPath();

        // Act
        string issContent = File.ReadAllText(issPath);

        // Assert
        issContent.Should().Contain("Name: \"desktopicon\"", "скрипт должен определять задачу desktopicon");
        issContent.Should().MatchRegex(
            @"(?s)Name:\s*""desktopicon"";.*?Flags:\s*unchecked\b",
            "задача desktopicon обязана быть выключена по умолчанию (Flags: unchecked)");
        issContent.Should().NotContain("checkedonce",
            "флаг checkedonce запрещён: он включает отметку при первой установке");
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
        pyContent.Should().Contain("Flags: unchecked", "шаблон генерации в build_csharp.py должен выключать задачу ярлыка по умолчанию");
        pyContent.Should().NotContain("checkedonce", "шаблон генерации не должен включать отметку ярлыка при первой установке");
        pyContent.Should().Contain("Check: ShouldCreateDesktopIcon", "шаблон генерации должен содержать вызов функции проверки ярлыка");
        pyContent.Should().Contain("function ShouldCreateDesktopIcon", "шаблон генерации должен определять функцию ShouldCreateDesktopIcon");
        pyContent.Should().Contain("function DesktopIconExists", "шаблон генерации должен определять функцию DesktopIconExists");
        pyContent.Should().Contain("WizardSelectTasks('!desktopicon')", "шаблон генерации должен снимать задачу при отсутствии ярлыка");
    }

    /// <summary>
    /// Проверяет, что миграция покрывает все исторические идентификаторы приложения:
    /// без полного списка старые установки остаются второй копией.
    /// </summary>
    [TestMethod]
    public void BuildScript_Migration_LegacyAppIdsPresent_ListsAllKnownIds()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("krnzhnr.ktools.v1", "миграция должна удалять версию 1.x (Python)");
        pyContent.Should().Contain("krnzhnr.ktools.csharp.v2", "миграция должна удалять промежуточную C#-версию");
        pyContent.Should().Contain("krnzhnr.ktools.v2", "миграция должна удалять промежуточную версию 2.x");
    }

    /// <summary>
    /// Проверяет, что результат запуска старого деинсталлятора проверяется и пишется в лог:
    /// молчаливый провал Exec/DelTree — причина параллельных копий из issue #44.
    /// </summary>
    [TestMethod]
    public void BuildScript_Migration_UninstallerResult_IsCheckedAndLogged()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("iResultCode <> 0", "код возврата деинсталлятора обязан проверяться");
        pyContent.Should().Contain("/LOG=", "каждый запуск старого деинсталлятора обязан писать лог");
        pyContent.Should().Contain("DelTree(sInstallDir, True, True, True)", "остатки каталога обязаны удаляться");
        pyContent.Should().Contain("DirExists(sInstallDir)", "после удаления обязана проверяться фактическая уборка каталога");
    }

    /// <summary>
    /// Проверяет, что для per-machine остатков запрашивается повышение прав:
    /// тихий Exec из непривилегированного процесса не может снести установку в Program Files.
    /// </summary>
    [TestMethod]
    public void BuildScript_Migration_PerMachineLeftovers_RequestElevationViaRunas()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("IsPerMachineEntryPresent", "миграция должна различать per-machine следы в HKLM");
        pyContent.Should().Contain("ShellExec('runas'", "для per-machine остатков обязана быть попытка запуска с повышением прав");
    }

    /// <summary>
    /// Проверяет шлагбаум PrepareToInstall: неудалённая старая версия блокирует установку
    /// вместо тихой параллельной копии при любом режиме запуска.
    /// </summary>
    [TestMethod]
    public void BuildScript_Migration_OldVersionRemains_PrepareToInstallBlocksSetup()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("function PrepareToInstall(var NeedsRestart: Boolean): String;",
            "миграция обязана выполняться в PrepareToInstall с возможностью блокировки установки");
        pyContent.Should().Contain("RemoveAppIdEverywhere('krnzhnr.ktools.v1')",
            "PrepareToInstall обязан вычищать версию 1.x до старта установки");
        pyContent.Should().Contain("sCurrentModeCopy",
            "PrepareToInstall обязан учитывать неудалённую копию текущего AppId в чужом режиме");
        pyContent.Should().Contain("Установка заблокирована, чтобы не плодить две копии программы",
            "при неудалённой старой версии пользователь обязан видеть явный текст блокировки");
    }

    /// <summary>
    /// Проверяет, что раннее предупреждение о нужде в правах администратора показывается
    /// до старта установки, а не после тихого провала миграции.
    /// </summary>
    [TestMethod]
    public void BuildScript_Migration_ElevationNeeded_InitializeSetupWarnsUser()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("function InitializeSetup: Boolean;",
            "установщик обязан определять InitializeSetup");
        pyContent.Should().Contain("NeedsElevationForMigration",
            "установщик обязан заранее определять нужду в повышении прав для уборки старых версий");
    }

    /// <summary>
    /// Проверяет закреплённые гарантии единственной копии: AppId неизменен,
    /// предыдущий каталог переиспользуется, привилегии осознанно per-user.
    /// </summary>
    [TestMethod]
    public void BuildScript_Setup_SingleCopyGuards_ArePinned()
    {
        // Arrange
        string root = GetRepositoryRoot();
        string pyPath = Path.Combine(root, "build_csharp.py");

        // Act
        string pyContent = File.ReadAllText(pyPath);

        // Assert
        pyContent.Should().Contain("AppId=krnzhnr.ktools", "идентификатор приложения зафиксирован: его смена плодит параллельные установки");
        pyContent.Should().Contain("UsePreviousAppDir=yes", "установщик обязан переиспользовать предыдущий каталог вместо создания второго");
        pyContent.Should().NotContain("[Components]", "страница выбора типа установки (полная/выборочная) удалена: ставить нечего выбирать");
        pyContent.Should().NotContain("Components: decoders", "встраиваемые декодеры удалены из установщика: они качаются как зависимость");
        pyContent.Should().Contain("RemoveUninstallEntry('krnzhnr.ktools', HKCU)", "копия текущего AppId в чужом режиме обязана вычищаться при установке для всех");
        pyContent.Should().Contain("RemoveUninstallEntry('krnzhnr.ktools', HKLM)", "копия текущего AppId в чужом режиме обязана вычищаться при установке per-user");
    }
}
