// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Фабрики преднастроенных Moq-моков для всех ключевых зависимостей приложения.
/// Позволяет единообразно создавать изолированные тестовые двойники
/// с корректными значениями по умолчанию.
/// </summary>
public static class MockBuilders
{
    /// <summary>
    /// Создает мок сервиса логирования с фиксацией всех вызовов (Loose-режим).
    /// </summary>
    /// <returns>Настроенный мок ILogService.</returns>
    public static Mock<ILogService> CreateLogServiceMock()
    {
        return new Mock<ILogService>();
    }

    /// <summary>
    /// Создает мок менеджера настроек с безопасными значениями по умолчанию:
    /// GetSetting возвращает defaultValue, GetSafeGroupName возвращает имя как есть,
    /// параллельная обработка выключена.
    /// </summary>
    /// <returns>Настроенный мок ISettingsManager.</returns>
    public static Mock<ISettingsManager> CreateSettingsManagerMock()
    {
        var mock = new Mock<ISettingsManager>();
        mock.Setup(m => m.GetSetting(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns<string, string, string>((g, k, d) => d);
        mock.Setup(m => m.GetSetting(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .Returns<string, string, bool>((g, k, d) => d);
        mock.Setup(m => m.GetSetting(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>()))
            .Returns<string, string, int>((g, k, d) => d);
        mock.Setup(m => m.GetSetting(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<double>()))
            .Returns<string, string, double>((g, k, d) => d);
        mock.Setup(m => m.GetSafeGroupName(It.IsAny<string>()))
            .Returns<string>(name => name);
        mock.Setup(m => m.GetAllSettingsInGroup(It.IsAny<string>()))
            .Returns<string>(_ => new Dictionary<string, object>());
        mock.SetupProperty(m => m.EnableParallel, false);
        mock.SetupProperty(m => m.MaxParallelTasks, 1);
        mock.SetupProperty(m => m.UseAutoSubfolder, false);
        mock.SetupProperty(m => m.DefaultOutputSubfolder, string.Empty);
        mock.SetupProperty(m => m.RenameEnableRegex, false);
        mock.SetupProperty(m => m.RenameRegexSearch, string.Empty);
        mock.SetupProperty(m => m.RenameRegexReplace, string.Empty);
        mock.SetupProperty(m => m.RenameUseRegex, false);
        mock.SetupProperty(m => m.RenameCaseSensitive, false);
        mock.SetupProperty(m => m.OverwriteExisting, false);
        mock.SetupProperty(m => m.ShowLogsTab, false);
        mock.SetupProperty(m => m.AutoCheckUpdates, false);
        mock.SetupProperty(m => m.IncludePreReleases, false);
        return mock;
    }

    /// <summary>
    /// Создает мок сервиса навигации, фиксирующий все переходы.
    /// </summary>
    /// <returns>Настроенный мок INavigationService.</returns>
    public static Mock<INavigationService> CreateNavigationMock()
    {
        var mock = new Mock<INavigationService>();
        var history = new List<(Type PageType, object? Parameter)>();
        mock.Setup(n => n.NavigateTo(It.IsAny<Type>(), It.IsAny<object?>()))
            .Callback<Type, object?>((t, p) => history.Add((t, p)));
        mock.Setup(n => n.CanGoBack).Returns(() => history.Count > 1);
        return mock;
    }

    /// <summary>
    /// Создает мок сервиса системных диалогов.
    /// </summary>
    /// <returns>Настроенный мок IDialogService.</returns>
    public static Mock<IDialogService> CreateDialogMock()
    {
        var mock = new Mock<IDialogService>();
        mock.Setup(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mock.Setup(d => d.ShowConfirmationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        return mock;
    }

    /// <summary>
    /// Создает мок менеджера зависимостей с настраиваемым статусом установки.
    /// </summary>
    /// <param name="allInstalled">Значение, возвращаемое IsInstalled и AreRequiredDependenciesInstalled.</param>
    /// <returns>Настроенный мок IDependencyManager.</returns>
    public static Mock<IDependencyManager> CreateDependencyManagerMock(bool allInstalled = true)
    {
        var mock = new Mock<IDependencyManager>();
        mock.Setup(d => d.IsInstalled(It.IsAny<string>())).Returns(allInstalled);
        mock.Setup(d => d.AreRequiredDependenciesInstalled()).Returns(allInstalled);
        mock.Setup(d => d.IsUpdateAvailable(It.IsAny<string>())).Returns(false);
        mock.Setup(d => d.GetRegistry()).Returns(new List<DependencyInfo>());
        mock.Setup(d => d.RefreshAllStatuses());
        mock.Setup(d => d.GetStatus(It.IsAny<string>()))
            .Returns(DependencyStatus.NotInstalled);
        mock.Setup(d => d.GetInstalledVersion(It.IsAny<string>()))
            .Returns(string.Empty);
        mock.Setup(d => d.CheckAllDependencyUpdatesAsync(It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        mock.Setup(d => d.CheckAndUpdateYtDlpAsync(It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        mock.Setup(d => d.InstallDependencyAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mock.Setup(d => d.RemoveDependency(It.IsAny<string>())).Returns(true);
        return mock;
    }

    /// <summary>
    /// Создает мок сервиса технического анализа медиафайлов.
    /// </summary>
    /// <param name="structure">Структура, возвращаемая ProbeAsync (null по умолчанию).</param>
    /// <returns>Настроенный мок IMediaProbeService.</returns>
    public static Mock<IMediaProbeService> CreateMediaProbeMock(MediaStructure? structure = null)
    {
        var mock = new Mock<IMediaProbeService>();
        mock.Setup(p => p.ProbeAsync(It.IsAny<string>()))
            .ReturnsAsync(structure);
        mock.Setup(p => p.EnrichTrackNamesAsync(It.IsAny<MediaStructure>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    /// <summary>
    /// Создает мок реестра скриптов на основе переданного списка скриптов.
    /// </summary>
    /// <param name="scripts">Скрипты, доступные через реестр.</param>
    /// <returns>Настроенный мок IScriptRegistry.</returns>
    public static Mock<IScriptRegistry> CreateScriptRegistryMock(params AbstractScript[] scripts)
    {
        var mock = new Mock<IScriptRegistry>();
        mock.Setup(r => r.Scripts).Returns(new List<AbstractScript>(scripts));
        mock.Setup(r => r.GetScriptByName(It.IsAny<string>()))
            .Returns<string>(name => scripts.FirstOrDefault(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        return mock;
    }

    /// <summary>
    /// Создает мок сервиса обновлений с настраиваемым результатом проверки.
    /// </summary>
    /// <param name="update">Информация о доступном обновлении или null.</param>
    /// <returns>Настроенный мок IUpdateService.</returns>
    public static Mock<IUpdateService> CreateUpdateMock(UpdateInfo? update = null)
    {
        var mock = new Mock<IUpdateService>();
        mock.Setup(u => u.CheckForUpdatesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(update);
        mock.Setup(u => u.DownloadAndInstallUpdateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Action<double>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    /// <summary>
    /// Создает мок менеджера путей с уникальной временной директорией настроек,
    /// чтобы тесты никогда не пересекались по файловой системе.
    /// </summary>
    /// <returns>Настроенный мок IPathManager.</returns>
    public static Mock<IPathManager> CreatePathManagerMock()
    {
        var mock = new Mock<IPathManager>();
        string uniqueSettingsDir = Path.Combine(
            Path.GetTempPath(),
            "KToolsTests",
            Guid.NewGuid().ToString("N"));
        mock.Setup(p => p.GetSettingsDirectory()).Returns(uniqueSettingsDir);
        mock.Setup(p => p.GetBaseDirectory()).Returns(uniqueSettingsDir);
        mock.Setup(p => p.GetBinDirectory())
            .Returns(Path.Combine(uniqueSettingsDir, "bin"));
        mock.Setup(p => p.GetBinaryPath(It.IsAny<string>()))
            .Returns<string>(bin => Path.Combine(uniqueSettingsDir, "bin", bin));
        mock.Setup(p => p.GetShortPath(It.IsAny<string>()))
            .Returns<string>(path => path);
        return mock;
    }
}
