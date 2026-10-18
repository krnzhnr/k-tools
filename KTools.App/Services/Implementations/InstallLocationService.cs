// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Служба определения папки зарегистрированной установки приложения
/// по данным деинсталлятора Inno Setup в реестре Windows.
/// Все комментарии, логи и исключения реализованы строго на русском языке.
/// </summary>
public sealed class InstallLocationService : IInstallLocationService
{
    private const string SourceName = nameof(InstallLocationService);

    /// <summary>
    /// Идентификатор приложения в реестре деинсталлятора (должен совпадать с AppId в скрипте Inno Setup).
    /// </summary>
    internal const string UninstallKeyName = "krnzhnr.ktools_is1";

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>
    /// Имя исполняемого файла приложения в папке установки.
    /// </summary>
    internal const string ExecutableFileName = "KTools.App.exe";

    private readonly ILogService _logService;

    /// <summary>
    /// Инициализирует новый экземпляр службы с внедрением логгера.
    /// </summary>
    public InstallLocationService(ILogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    /// <inheritdoc/>
    public StaleCopyInfo? DetectStaleCopy()
    {
        // Диагностические сборки проверку пропускают: запуск из bin-каталога
        // никогда не должен считаться неосновной копией.
        if (App.IsDiagnosticBuild)
        {
            return null;
        }

        try
        {
            string currentDir = NormalizeDirectory(AppContext.BaseDirectory);
            string currentVersion = GetCurrentVersion();
            string? registeredDir = ReadRegisteredInstallDir();
            if (registeredDir is null)
            {
                _logService.Write(
                    "app.install_location.main_copy",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Зарегистрированная установка не найдена, текущий запуск считается основным",
                    source: SourceName,
                    properties: LogProps.Create("FileName", LogProps.RootName(currentDir)).With("Version", currentVersion));
                return null;
            }

            string registeredExe = Path.Combine(registeredDir, ExecutableFileName);
            if (!File.Exists(registeredExe))
            {
                _logService.Write(
                    "app.install_location.main_copy",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Файлы зарегистрированной установки отсутствуют, текущий запуск считается основным",
                    source: SourceName,
                    properties: LogProps.Create("FileName", LogProps.RootName(currentDir)).With("Version", currentVersion));
                return null;
            }

            string registeredVersion = ReadExecutableVersion(registeredExe) ?? string.Empty;
            var info = EvaluateStaleCopy(currentDir, currentVersion, registeredDir, registeredExe, registeredVersion);
            if (info is null)
            {
                _logService.Write(
                    "app.install_location.main_copy",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Запуск выполнен из основной папки установки",
                    source: SourceName,
                    properties: LogProps.Create("FileName", LogProps.RootName(currentDir)).With("Version", currentVersion));
                return null;
            }

            _logService.Write(
                "app.install_location.stale_copy_detected",
                LogLevel.Warning,
                info.ShouldWarn ? LogStatus.Changed : LogStatus.Skipped,
                $"Обнаружен запуск из неосновной копии: текущая папка отличается от зарегистрированной установки (версия {registeredVersion})",
                source: SourceName,
                properties: LogProps
                    .Create("FileName", LogProps.RootName(info.CurrentDirectory))
                    .With("Version", info.CurrentVersion)
                    .With("Reason", "StaleCopy"));
            return info;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "app.install_location.detect_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось определить папку зарегистрированной установки, проверка неосновной копии пропущена",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "INSTALL_LOCATION_DETECT_FAILED").With("Retryable", false));
            return null;
        }
    }

    /// <inheritdoc/>
    public bool TryLaunchRegisteredCopy(StaleCopyInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = info.RegisteredExecutablePath,
                Arguments = Program.TakeoverArgument,
                UseShellExecute = true
            });

            _logService.Write(
                "app.install_location.redirected",
                LogLevel.Info,
                LogStatus.Succeeded,
                "Запущена основная копия приложения из зарегистрированной папки установки",
                source: SourceName,
                properties: LogProps.Create("FileName", LogProps.RootName(info.RegisteredDirectory)).With("Version", info.RegisteredVersion));
            return true;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "app.install_location.redirect_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Не удалось запустить основную копию приложения из зарегистрированной папки установки",
                ex,
                SourceName,
                properties: LogProps.Create("ErrorCode", "INSTALL_LOCATION_REDIRECT_FAILED").With("FileName", LogProps.RootName(info.RegisteredDirectory)));
            return false;
        }
    }

    /// <summary>
    /// Чистая функция сопоставления папки запуска и зарегистрированной установки.
    /// Возвращает сведения о неосновной копии либо null, если запуск основной.
    /// Предупреждение подавляется, когда запущенная копия строго новее зарегистрированной
    /// (пользователь осознанно тестирует новую сборку из другой папки).
    /// </summary>
    /// <param name="currentDir">Папка запущенного процесса.</param>
    /// <param name="currentVersion">Версия запущенной копии.</param>
    /// <param name="registeredDir">Папка зарегистрированной установки (null, если неизвестна).</param>
    /// <param name="registeredExePath">Путь к исполняемому файлу установки (null, если неизвестен).</param>
    /// <param name="registeredVersion">Версия зарегистрированной установки.</param>
    public static StaleCopyInfo? EvaluateStaleCopy(
        string currentDir,
        string? currentVersion,
        string? registeredDir,
        string? registeredExePath,
        string? registeredVersion)
    {
        string normalizedCurrent = NormalizeDirectory(currentDir);
        if (string.IsNullOrEmpty(normalizedCurrent)
            || string.IsNullOrWhiteSpace(registeredDir)
            || string.IsNullOrWhiteSpace(registeredExePath))
        {
            return null;
        }

        string normalizedRegistered = NormalizeDirectory(registeredDir);
        if (string.IsNullOrEmpty(normalizedRegistered)
            || normalizedCurrent.Equals(normalizedRegistered, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string current = NormalizeVersion(currentVersion);
        string registered = NormalizeVersion(registeredVersion);

        // Предупреждаем во всех случаях, кроме строго более новой запущенной копии.
        // При неизвестной версии установки предупреждение показывается:
        // доказать, что запущенная копия новее, невозможно.
        bool shouldWarn = string.IsNullOrEmpty(registered)
            || VersionComparer.CompareVersions(registered, current) >= 0;

        return new StaleCopyInfo(
            normalizedCurrent,
            current,
            normalizedRegistered,
            registeredExePath,
            registered,
            shouldWarn);
    }

    /// <summary>
    /// Читает папку зарегистрированной установки из реестра деинсталлятора:
    /// сначала значение InstallLocation, затем каталог из UninstallString.
    /// Проверяются ветви текущего пользователя и локальной машины.
    /// </summary>
    private static string? ReadRegisteredInstallDir()
    {
        string[] hives = ["HKCU", "HKLM"];
        foreach (string hive in hives)
        {
            try
            {
                Microsoft.Win32.RegistryKey? root = hive == "HKCU"
                    ? Microsoft.Win32.Registry.CurrentUser
                    : Microsoft.Win32.Registry.LocalMachine;
                using Microsoft.Win32.RegistryKey? key = root?.OpenSubKey(UninstallKeyPath + UninstallKeyName);
                if (key is null)
                {
                    continue;
                }

                string? installDir = key.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
                {
                    return installDir;
                }

                string? uninstallString = key.GetValue("UninstallString")?.ToString()?.Trim().Trim('"');
                string? dirFromUninstall = string.IsNullOrWhiteSpace(uninstallString)
                    ? null
                    : Path.GetDirectoryName(uninstallString);
                if (!string.IsNullOrWhiteSpace(dirFromUninstall) && Directory.Exists(dirFromUninstall))
                {
                    return dirFromUninstall;
                }
            }
            catch (Exception)
            {
                // Ошибки доступа к отдельной ветви реестра не прерывают проверку.
            }
        }

        return null;
    }

    /// <summary>
    /// Читает версию исполняемого файла зарегистрированной установки.
    /// Возвращает null, если файл отсутствует или версию прочитать нельзя.
    /// </summary>
    private static string? ReadExecutableVersion(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            string? productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                return NormalizeVersion(productVersion);
            }

            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Возвращает информационную версию текущей сборки без суффикса метаданных сборки.
    /// </summary>
    private static string GetCurrentVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion))
            {
                return NormalizeVersion(infoVersion);
            }

            return assembly.GetName().Version?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Нормализует путь каталога для надёжного сравнения: полный путь без завершающего разделителя.
    /// </summary>
    private static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>
    /// Нормализует строку версии: убирает ведущий 'v' и отсекает метаданные сборки после '+' или пробела.
    /// </summary>
    internal static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        string result = version.Trim().TrimStart('v');
        int plusIdx = result.IndexOf('+');
        if (plusIdx >= 0)
        {
            result = result.Substring(0, plusIdx);
        }

        int spaceIdx = result.IndexOf(' ');
        if (spaceIdx >= 0)
        {
            result = result.Substring(0, spaceIdx);
        }

        return result.Trim();
    }
}
