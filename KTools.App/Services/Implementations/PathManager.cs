// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Класс для управления путями приложения (поиск внешних утилит и рабочих директорий).
/// </summary>
public sealed class PathManager : IPathManager
{
    private const string SourceName = nameof(PathManager);

    // Модель хранения данных (ишью 46): основной путь для всех пользовательских данных —
    // %LocalAppData%\KTools (настройки, bin, кэши, логи). Каталог установки считается
    // неизменяемым: апдейтер перезаписывает его целиком, поэтому данные рядом с exe
    // живут только в явном portable-режиме. Признак режима — пустой файл-маркер
    // portable.txt рядом с исполняемым файлом. Никаких проб записи на старте нет:
    // ответ заранее известен (per-user установка записываема, per-machine — нет,
    // MSIX всегда работает в LocalAppData), поэтому вердикт вычисляется детерминированно.
    private const string PortableMarkerFileName = "portable.txt";

    private readonly string _baseDir;
    private readonly ILogService _logService;
    private string? _binDirectory;
    private readonly object _binDirectoryLock = new();
    private string? _settingsDirectory;
    private readonly object _settingsDirectoryLock = new();
    private bool? _isPortableMode;
    private readonly object _portableModeLock = new();
    private bool _legacyFallback;
    private readonly object _legacyFallbackLock = new();
    private bool _modeLogged;
    private readonly object _modeLoggedLock = new();
    private readonly Dictionary<string, string> _binaryPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortPathCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _customBinaryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _systemPathBinaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _binarySourceLock = new();

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="PathManager"/> с внедрением зависимостей.
    /// </summary>
    public PathManager(ILogService logService)
        : this(logService, AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="PathManager"/> с указанием базовой директории.
    /// </summary>
    /// <param name="logService">Служба ведения системных журналов.</param>
    /// <param name="baseDir">Базовая директория приложения (при null или пустоте используется AppContext.BaseDirectory).</param>
    public PathManager(ILogService logService, string? baseDir)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _baseDir = string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir;
    }

    /// <summary>
    /// Получить базовую директорию сборки приложения.
    /// </summary>
    /// <returns>Абсолютный путь к директории сборки.</returns>
    public string GetBaseDirectory()
    {
        return _baseDir;
    }

    /// <summary>
    /// Возвращает абсолютный путь к директории bin для утилит.
    ///
    /// В portable-режиме (маркер portable.txt рядом с exe): папка bin рядом с исполняемым файлом.
    /// Иначе: %LocalAppData%\KTools\bin. Каталог установки считается неизменяемым
    /// (апдейтер перезаписывает его целиком), проба записи не выполняется.
    /// </summary>
    /// <returns>Абсолютный путь к локальной папке bin утилит.</returns>
    public string GetBinDirectory()
    {
        string? cached = _binDirectory;
        if (cached != null)
        {
            return cached;
        }

        lock (_binDirectoryLock)
        {
            if (_binDirectory != null)
            {
                return _binDirectory;
            }

            string resolved = IsPortableMode() || IsLegacyFallback()
                ? Path.Combine(_baseDir, "bin")
                : Path.Combine(GetManagedDataRoot(), "bin");

            if (!Directory.Exists(resolved))
            {
                Directory.CreateDirectory(resolved);
            }

            LogModeOnce();
            return _binDirectory = resolved;
        }
    }

    /// <summary>
    /// Возвращает путь к директории для хранения конфигурационных файлов.
    ///
    /// В portable-режиме (маркер portable.txt рядом с exe): каталог установки.
    /// Иначе: %LocalAppData%\KTools. Проба записи не выполняется.
    /// </summary>
    /// <returns>Абсолютный путь к папке настроек.</returns>
    public string GetSettingsDirectory()
    {
        string? cached = _settingsDirectory;
        if (cached != null)
        {
            return cached;
        }

        lock (_settingsDirectoryLock)
        {
            if (_settingsDirectory != null)
            {
                return _settingsDirectory;
            }

            string resolved;
            if (IsPortableMode() || IsLegacyFallback())
            {
                resolved = _baseDir;
            }
            else
            {
                resolved = GetManagedDataRoot();
                if (!Directory.Exists(resolved))
                {
                    Directory.CreateDirectory(resolved);
                }
            }

            LogModeOnce();
            return _settingsDirectory = resolved;
        }
    }

    /// <summary>
    /// Проверяет, работает ли приложение в portable-режиме.
    /// Признак — файл-маркер portable.txt рядом с исполняемым файлом.
    /// В этом режиме все данные хранятся рядом с exe, миграция не выполняется.
    /// </summary>
    /// <returns>True, если рядом с exe есть маркер portable.txt.</returns>
    public bool IsPortableMode()
    {
        if (_isPortableMode.HasValue)
        {
            return _isPortableMode.Value;
        }

        lock (_portableModeLock)
        {
            if (_isPortableMode.HasValue)
            {
                return _isPortableMode.Value;
            }

            bool portable = false;
            try
            {
                string marker = Path.Combine(_baseDir, PortableMarkerFileName);
                portable = File.Exists(marker) || Directory.Exists(marker);
            }
            catch (Exception)
            {
                // Не удалось проверить маркер — считаем режим управляемым:
                // данные в LocalAppData всегда безопасны, рядом с exe — нет.
                portable = false;
            }

            _isPortableMode = portable;
            return portable;
        }
    }

    /// <summary>
    /// Переключает разрешение путей на каталог установки после сбоя миграции.
    /// Приложение стартует «по-старому», повторная попытка — при следующем запуске.
    /// </summary>
    public void EnterLegacyFallbackMode()
    {
        lock (_legacyFallbackLock)
        {
            _legacyFallback = true;
            _binDirectory = null;
            _settingsDirectory = null;
        }

        lock (_modeLoggedLock)
        {
            _modeLogged = false;
        }

        LogModeOnce();
    }

    /// <summary>
    /// Проверяет, включён ли откат на каталог установки после сбоя миграции.
    /// </summary>
    /// <returns>True, если пути разрешаются в каталог установки.</returns>
    private bool IsLegacyFallback()
    {
        lock (_legacyFallbackLock)
        {
            return _legacyFallback;
        }
    }

    /// <summary>
    /// Проверяет, недоступен ли LocalAppData (данные уйдут во временный каталог).
    /// </summary>
    /// <returns>True, если каталог LocalAppData пуст или недоступен.</returns>
    private static bool IsTempDataRoot()
    {
        try
        {
            return string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Возвращает корневой каталог управляемых данных (%LocalAppData%\KTools).
    /// При недоступном LocalAppData откатывается на временный каталог.
    /// </summary>
    /// <returns>Абсолютный путь к корню данных.</returns>
    internal static string GetManagedDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, LogFilePolicy.ApplicationFolderName);
    }

    /// <summary>
    /// Однократно фиксирует в журнале выбранный режим хранения данных.
    /// </summary>
    private void LogModeOnce()
    {
        lock (_modeLoggedLock)
        {
            if (_modeLogged)
            {
                return;
            }

            _modeLogged = true;
        }

        bool portable = IsPortableMode();
        bool fallback = !portable && IsLegacyFallback();
        bool tempData = !portable && !fallback && IsTempDataRoot();
        string mode = portable ? "portable" : (fallback ? "legacy-fallback" : (tempData ? "managed-temp" : "managed"));
        _logService.Write(
            "path.mode.resolved",
            tempData ? LogLevel.Warning : LogLevel.Debug,
            tempData ? LogStatus.Changed : LogStatus.Succeeded,
            portable
                ? "Данные приложения хранятся рядом с исполняемым файлом (portable-режим)"
                : (fallback
                    ? "Миграция не удалась, данные берутся из каталога установки (режим отката)"
                    : (tempData
                        ? "Каталог LocalAppData недоступен, данные хранятся во временном каталоге и могут быть удалены очисткой системы"
                        : "Данные приложения хранятся в каталоге LocalAppData")),
            source: SourceName,
            properties: LogProps
                .Create("FileName", LogProps.FileName(_baseDir))
                .With("Mode", mode));
    }

    /// <summary>
    /// Найти путь к исполняемому файлу утилиты (ffmpeg, mkvmerge, eac3to и др.).
    /// Приоритет (ишью 54): явный путь пользователя, затем системный PATH
    /// (если включён для бинарника), затем цепочка bin/ приложения.
    /// Проверяет как папку bin приложения, так и пользовательскую директорию LocalAppData.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <returns>Полный абсолютный путь к файлу утилиты на диске.</returns>
    public string GetBinaryPath(string binaryName)
    {
        binaryName = NormalizeBinaryName(binaryName);

        lock (_binaryPathCache)
        {
            if (_binaryPathCache.TryGetValue(binaryName, out string? cached))
            {
                // Кэш актуален, только если файл на месте: после удаления или
                // переустановки зависимости путь нужно искать заново.
                if (File.Exists(cached))
                {
                    return cached;
                }

                _binaryPathCache.Remove(binaryName);
            }
        }

        string? customPath;
        bool useSystemPath;
        lock (_binarySourceLock)
        {
            _customBinaryPaths.TryGetValue(binaryName, out customPath);
            useSystemPath = _systemPathBinaries.Contains(binaryName);
        }

        // Явный путь пользователя (высший приоритет): возвращается как есть,
        // наличие файла проверяет статус зависимости, а не резолв.
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            lock (_binaryPathCache)
            {
                _binaryPathCache[binaryName] = customPath;
            }

            return customPath;
        }

        // Замена имени в соответствии с соглашением именования K-Tools
        string targetName = binaryName.ToLowerInvariant() switch
        {
            "ffmpeg.exe" => "kt-ffmpeg.exe",
            "ffprobe.exe" => "kt-ffprobe.exe",
            _ => binaryName
        };

        // Поиск в системном PATH (только при явном включении для бинарника):
        // при отсутствии файла откатываемся на встроенную цепочку.
        if (useSystemPath)
        {
            string? fromPath = ResolveFromSystemPath(binaryName, targetName);
            if (fromPath != null)
            {
                lock (_binaryPathCache)
                {
                    _binaryPathCache[binaryName] = fromPath;
                }

                return fromPath;
            }
        }

        // Определение подпапки в зависимости от типа утилиты
        string subfolder = GetSubfolderName(targetName);
        string binDir = GetBinDirectory();
        string localAppDataBinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KTools", "bin");
        string baseBinDir = Path.Combine(_baseDir, "bin");

        // Кандидаты поиска утилиты (приоритет: текущая рабочая bin -> LocalAppData -> BaseDir/bin)
        string[] candidateDirs = new[] { binDir, localAppDataBinDir, baseBinDir };

        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            // 1. Поиск непосредственно в папке bin
            string pathInBin = Path.Combine(dir, targetName);
            if (File.Exists(pathInBin))
            {
                lock (_binaryPathCache)
                {
                    _binaryPathCache[binaryName] = pathInBin;
                }
                return pathInBin;
            }

            // 2. Поиск в соответствующей подпапке (например, bin/ffmpeg/kt-ffmpeg.exe)
            string pathInSubfolder = Path.Combine(dir, subfolder, targetName);
            if (File.Exists(pathInSubfolder))
            {
                lock (_binaryPathCache)
                {
                    _binaryPathCache[binaryName] = pathInSubfolder;
                }
                return pathInSubfolder;
            }
        }

        // 3. Резервный возврат имени файла (поиск в системном PATH операционной системы)
        return targetName;
    }

    /// <summary>
    /// Задать путь к исполняемому файлу, указанный вручную (ишью 54).
    /// Наличие файла не проверяется: статус зависимости определяется
    /// проверкой файла, а смена настройки инвалидирует кэш резолва.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <param name="fullPath">Абсолютный путь к файлу или null/пусто для сброса.</param>
    public void SetCustomBinaryPath(string binaryName, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            throw new ArgumentException("Имя бинарного файла не задано.", nameof(binaryName));
        }

        string key = NormalizeBinaryName(binaryName);
        bool cleared;
        lock (_binarySourceLock)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                cleared = _customBinaryPaths.Remove(key);
            }
            else
            {
                _customBinaryPaths[key] = fullPath;
                cleared = false;
            }
        }

        _logService.Write(
            "path.binary_source.changed",
            LogLevel.Debug,
            LogStatus.Changed,
            cleared
                ? "Источник бинарника сброшен к встроенным из bin/"
                : "Для бинарника задан путь, указанный вручную",
            source: SourceName,
            properties: cleared
                ? LogProps.Create("Key", key)
                : LogProps.Create("Key", key).With("FileName", LogProps.FileName(fullPath)));

        InvalidateBinaryPathCache(binaryName);
    }

    /// <summary>
    /// Включить или выключить поиск бинарника в системном PATH (ишью 54).
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <param name="enabled">True — искать в системном PATH.</param>
    public void SetUseSystemPath(string binaryName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            throw new ArgumentException("Имя бинарного файла не задано.", nameof(binaryName));
        }

        string key = NormalizeBinaryName(binaryName);
        lock (_binarySourceLock)
        {
            if (enabled)
            {
                _systemPathBinaries.Add(key);
            }
            else
            {
                _systemPathBinaries.Remove(key);
            }
        }

        _logService.Write(
            "path.binary_source.changed",
            LogLevel.Debug,
            LogStatus.Changed,
            enabled
                ? "Для бинарника включён поиск в системном PATH"
                : "Поиск бинарника в системном PATH выключен, используются встроенные из bin/",
            source: SourceName,
            properties: LogProps
                .Create("Key", key)
                .With("Mode", enabled ? "system" : "builtin"));

        InvalidateBinaryPathCache(binaryName);
    }

    /// <summary>
    /// Инвалидировать кэш резолва бинарных путей (ишью 54).
    /// </summary>
    /// <param name="binaryName">Имя бинарника или null для очистки всего кэша.</param>
    public void InvalidateBinaryPathCache(string? binaryName = null)
    {
        if (binaryName == null)
        {
            lock (_binaryPathCache)
            {
                _binaryPathCache.Clear();
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(binaryName))
        {
            throw new ArgumentException("Имя бинарного файла не задано.", nameof(binaryName));
        }

        lock (_binaryPathCache)
        {
            _binaryPathCache.Remove(NormalizeBinaryName(binaryName));
        }
    }

    /// <summary>
    /// Приводит имя бинарника к каноническому виду (на Windows добавляет .exe).
    /// </summary>
    /// <param name="binaryName">Исходное имя бинарного файла.</param>
    /// <returns>Нормализованное имя для кэша и переопределений.</returns>
    private static string NormalizeBinaryName(string binaryName)
    {
        if (OperatingSystem.IsWindows() && !binaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return binaryName + ".exe";
        }

        return binaryName;
    }

    /// <summary>
    /// Ищет бинарник в каталогах системного PATH (аналог where.exe, без запуска процессов).
    /// </summary>
    /// <param name="binaryName">Запрошенное имя бинарного файла.</param>
    /// <param name="targetName">Имя после маппинга K-Tools (kt-ffmpeg.exe и т.д.).</param>
    /// <returns>Полный путь к первому найденному файлу или null.</returns>
    private static string? ResolveFromSystemPath(string binaryName, string targetName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        List<string> candidates = new(3) { binaryName };
        if (!targetName.Equals(binaryName, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(targetName);
        }

        string? alias = GetSystemAlias(binaryName);
        if (alias != null && !candidates.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(alias);
        }

        foreach (string rawDir in pathEnv.Split(Path.PathSeparator))
        {
            string dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.Combine(dir, candidate);
                }
                catch (Exception)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(full))
                    {
                        return Path.GetFullPath(full);
                    }
                }
                catch (Exception)
                {
                    // Недоступный каталог PATH пропускается.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Возвращает системный псевдоним встроенного бинарника K-Tools
    /// (kt-ffmpeg.exe в PATH лежит как ffmpeg.exe).
    /// </summary>
    /// <param name="binaryName">Запрошенное имя бинарного файла.</param>
    /// <returns>Системное имя или null, если псевдонима нет.</returns>
    private static string? GetSystemAlias(string binaryName)
    {
        return binaryName.ToLowerInvariant() switch
        {
            "kt-ffmpeg.exe" => "ffmpeg.exe",
            "kt-ffprobe.exe" => "ffprobe.exe",
            _ => null
        };
    }

    /// <summary>
    /// Возвращает короткий путь в формате 8.3 для операционной системы Windows.
    /// Это необходимо для совместимости с устаревшими 32-битными утилитами (например, eac3to),
    /// которые некорректно работают с путями, содержащими пробелы или символы кириллицы.
    /// </summary>
    /// <param name="path">Исходный длинный путь к файлу или папке.</param>
    /// <returns>Короткий путь в формате 8.3, либо исходный путь, если преобразование невозможно.</returns>
    public string GetShortPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        lock (_shortPathCache)
        {
            if (_shortPathCache.TryGetValue(path, out string? cached))
            {
                return cached;
            }
        }

        string result = ComputeShortPath(path);

        lock (_shortPathCache)
        {
            _shortPathCache[path] = result;
        }

        return result;
    }

    private string ComputeShortPath(string path)
    {
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint result = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (result > 0)
            {
                string shortPath = sb.ToString();
                _logService.Write(
                    "path.short_name.resolved",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Путь преобразован в короткую форму 8.3",
                    source: SourceName,
                    properties: LogProps
                        .Create("FileName", LogProps.FileName(path))
                        .With("Extension", Path.GetExtension(path)));
                return shortPath;
            }

            if (result > sb.Capacity)
            {
                sb.EnsureCapacity((int)result);
                result = GetShortPathName(path, sb, result);
                if (result > 0)
                {
                    string shortPath = sb.ToString();
                    _logService.Write(
                        "path.short_name.resolved_retry",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        "Путь преобразован в короткую форму 8.3 после расширения буфера",
                        source: SourceName,
                        properties: LogProps
                            .Create("FileName", LogProps.FileName(path))
                            .With("Extension", Path.GetExtension(path)));
                    return shortPath;
                }
            }

            int lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _logService.Write(
                "path.short_name.unavailable",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Короткая форма пути 8.3 недоступна, используется исходный путь",
                source: SourceName,
                properties: LogProps
                    .Create("FileName", LogProps.FileName(path))
                    .With("ErrorCode", "SHORT_PATH_UNAVAILABLE")
                    .With("ExitCode", lastError));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "path.short_name.failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Короткая форма пути 8.3 не получена, используется исходный путь",
                ex,
                SourceName,
                properties: LogProps
                    .Create("FileName", LogProps.FileName(path))
                    .With("ErrorCode", "SHORT_PATH_FAILED"));
        }

        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Получить имя подпапки для группировки бинарных утилит в папке bin/.
    /// </summary>
    private string GetSubfolderName(string binaryName)
    {
        string name = binaryName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return name switch
        {
            "mkvmerge" => "mkvtoolnix",
            "ffprobe" => "ffmpeg",
            "ffmpeg" => "ffmpeg",
            "kt-ffmpeg" => "ffmpeg",
            "kt-ffprobe" => "ffmpeg",
            "deew" => "DEE",
            "dee" => "DEE",
            "qaac64" => "ffmpeg",
            "whisper-cli" => "whisper-cpu",
            _ => name
        };
    }
}

