// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Класс для управления путями приложения (поиск внешних утилит и рабочих директорий).
/// </summary>
public sealed class PathManager : IPathManager
{
    private const string SourceName = nameof(PathManager);

    // Маркер решения о записи в каталог установки (PERF-06).
    // Каталог: %LocalAppData%\KTools.
    // Имя: write-probe-&lt;отпечаток&gt;.json, где отпечаток — первые 16 hex-символов
    // SHA256 от нормализованного пути установки в нижнем регистре (один файл на путь
    // установки, чтобы разные установки и параллельные тесты не затирали друг друга).
    // Содержимое JSON: { "BaseDir": "...", "IsWritable": true, "AppVersion": "2.1.0",
    // "ProbedAtUtc": "2026-10-14T00:00:00Z" }.
    // Маркер валиден, если путь совпадает (без учёта регистра), версия совпадает
    // и возраст не старше ProbeCacheTtl. При валидном маркере пробная запись
    // не выполняется; при смене пути, версии, отсутствии или протухании маркера
    // выполняется одна проба с атомарной перезаписью маркера (временный файл
    // и перемещение). MSIX-установки маркер не используют (всегда LocalAppData
    // без проб). Все ошибки чтения и записи маркера тихие: откат к пробе,
    // без диалогов и запросов прав.
    private const string ProbeCacheFilePrefix = "write-probe-";

    private const string ProbeCacheFileSuffix = ".json";

    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromDays(30);

    private readonly string _baseDir;
    private readonly ILogService _logService;
    private string? _binDirectory;
    private readonly object _binDirectoryLock = new();
    private string? _settingsDirectory;
    private readonly object _settingsDirectoryLock = new();
    private readonly object _probeLock = new();
    private bool? _cachedIsWritable;
    private readonly Dictionary<string, string> _binaryPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortPathCache = new(StringComparer.Ordinal);

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
    /// Для обычных приложений: использует папку bin рядом с исполняемым файлом.
    /// Для MSIX приложений: использует LocalAppData (так как Program Files доступен только для чтения).
    /// Это обеспечивает работу скачивания и распаковки зависимостей в MSIX приложениях.
    /// <summary>
    /// Возвращает абсолютный путь к директории bin для утилит.
    /// 
    /// Для обычных приложений: при наличии прав использует папку bin рядом с исполняемым файлом.
    /// Если прав записи нет (например, установка в Program Files) - автоматически переключается на LocalAppData.
    /// Это гарантирует беспрепятственную загрузку и обновление утилит без ошибок доступа.
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

            // Проверка: если приложение запущено из защищенной системной папки MSIX
            if (IsMsixInstallation())
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return _binDirectory = Path.Combine(appData, "KTools", "bin");
            }

            // Вердикт о записи берётся из межзапускового маркера; проба — только при
            // отсутствии, смене пути или версии, протухании маркера.
            bool writable = ResolveBaseDirWritable();
            if (writable)
            {
                return _binDirectory = Path.Combine(_baseDir, "bin");
            }

            // При отсутствии прав записи в папку установки (Program Files) используем LOCALAPPDATA
            string fallbackDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallbackPath = Path.Combine(fallbackDir, "KTools", "bin");

            if (!Directory.Exists(fallbackPath))
            {
                Directory.CreateDirectory(fallbackPath);
            }
            return _binDirectory = fallbackPath;
        }
    }

    /// <summary>
    /// Возвращает путь к директории для хранения конфигурационных файлов.
    /// 
    /// Для обычных приложений (Portable): попытается использовать папку приложения,
    /// если нет прав - использует LOCALAPPDATA.
    /// Для MSIX приложений: всегда использует LOCALAPPDATA (так как Program Files доступен только для чтения).
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

            if (IsMsixInstallation())
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string msixSettingsPath = Path.Combine(appData, "KTools");
                if (!Directory.Exists(msixSettingsPath))
                {
                    Directory.CreateDirectory(msixSettingsPath);
                }
                return _settingsDirectory = msixSettingsPath;
            }

            // Общий с GetBinDirectory вердикт о записи: валидный маркер исключает
            // повторную пробу, инвалидация приводит к одной пробе и обновлению маркера.
            bool writable = ResolveBaseDirWritable();
            if (writable)
            {
                return _settingsDirectory = _baseDir;
            }

            // 2. Fallback в LOCALAPPDATA при отсутствии прав
            string fallbackDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallbackPath = Path.Combine(fallbackDir, "KTools");

            if (!Directory.Exists(fallbackPath))
            {
                Directory.CreateDirectory(fallbackPath);
            }
            return _settingsDirectory = fallbackPath;
        }
    }

    /// <summary>
    /// Возвращает путь к файлу-маркеру решения о записи в каталог установки.
    /// Маркер лежит в %LocalAppData%\KTools и называется write-probe-&lt;отпечаток&gt;.json.
    /// </summary>
    /// <param name="baseDir">Базовая директория установки (при null или пустоте используется AppContext.BaseDirectory).</param>
    /// <returns>Абсолютный путь к файлу маркера для указанного пути установки.</returns>
    public static string GetProbeCacheFilePath(string? baseDir)
    {
        string effective = string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir;
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools");
        return Path.Combine(cacheDir, ProbeCacheFilePrefix + ComputeBaseDirFingerprint(effective) + ProbeCacheFileSuffix);
    }

    /// <summary>
    /// Проверяет, запущено ли приложение из защищённой системной папки MSIX.
    /// </summary>
    /// <returns>True для MSIX-установок, иначе false.</returns>
    private bool IsMsixInstallation()
    {
        return _baseDir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Возвращает сохранённый вердикт о записи в каталог установки.
    /// При валидном маркере проба не выполняется; иначе выполняется одна проба
    /// с обновлением маркера. Результат запоминается до конца жизни процесса.
    /// </summary>
    /// <returns>True, если каталог установки доступен для записи.</returns>
    private bool ResolveBaseDirWritable()
    {
        lock (_probeLock)
        {
            if (_cachedIsWritable.HasValue)
            {
                _logService.Write(
                    "path.probe.cache_hit",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Проверка записи в каталог установки пропущена, использовано сохранённое решение",
                    source: SourceName,
                    properties: LogProps
                        .Create("FileName", LogProps.FileName(_baseDir))
                        .With("Version", GetCurrentAppVersion()));
                return _cachedIsWritable.Value;
            }

            if (TryLoadVerdictFromFile(out bool cached))
            {
                _cachedIsWritable = cached;
                _logService.Write(
                    "path.probe.cache_hit",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Проверка записи в каталог установки пропущена, использовано сохранённое решение",
                    source: SourceName,
                    properties: LogProps
                        .Create("FileName", LogProps.FileName(_baseDir))
                        .With("Version", GetCurrentAppVersion()));
                return cached;
            }

            bool writable = ProbeBaseDirWritable();
            _cachedIsWritable = writable;
            SaveVerdictToFile(writable);
            _logService.Write(
                "path.probe.completed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                "Пробная запись в каталог установки выполнена, решение сохранено",
                source: SourceName,
                properties: LogProps
                    .Create("FileName", LogProps.FileName(_baseDir))
                    .With("Value", writable)
                    .With("Version", GetCurrentAppVersion()));
            return writable;
        }
    }

    /// <summary>
    /// Выполняет единственную пробную запись в каталог установки.
    /// Имя файла уникально, чтобы параллельные процессы не мешали друг другу.
    /// </summary>
    /// <returns>True, если запись удалась.</returns>
    private bool ProbeBaseDirWritable()
    {
        string testFile = Path.Combine(_baseDir, $".write_test_probe_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(testFile, "test");
            try
            {
                File.Delete(testFile);
            }
            catch (Exception)
            {
                // Удаление пробного файла лучше не доводить до fallback: запись удалась.
            }

            return true;
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(testFile))
                {
                    File.Delete(testFile);
                }
            }
            catch (Exception)
            {
                // Тихое игнорирование очистки: вердикт уже определён как отсутствие прав.
            }

            return false;
        }
    }

    /// <summary>
    /// Читает маркер решения из %LocalAppData%\KTools и проверяет путь, версию и возраст.
    /// Любая ошибка чтения или несоответствие трактуется как отсутствие маркера.
    /// </summary>
    /// <param name="isWritable">Сохранённый вердикт при успехе.</param>
    /// <returns>True, если маркер валиден.</returns>
    private bool TryLoadVerdictFromFile(out bool isWritable)
    {
        isWritable = false;
        string cacheFile;
        try
        {
            cacheFile = GetProbeCacheFilePath(_baseDir);
        }
        catch (Exception)
        {
            return false;
        }

        string json;
        try
        {
            if (!File.Exists(cacheFile))
            {
                return false;
            }

            json = File.ReadAllText(cacheFile);
        }
        catch (Exception)
        {
            return false;
        }

        ProbeCacheEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<ProbeCacheEntry>(json);
        }
        catch (Exception)
        {
            return false;
        }

        if (entry is null || string.IsNullOrWhiteSpace(entry.BaseDir))
        {
            return false;
        }

        if (!string.Equals(NormalizeBaseDir(entry.BaseDir), NormalizeBaseDir(_baseDir), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(entry.AppVersion ?? string.Empty, GetCurrentAppVersion(), StringComparison.Ordinal))
        {
            return false;
        }

        DateTime probed = entry.ProbedAtUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(entry.ProbedAtUtc, DateTimeKind.Utc)
            : entry.ProbedAtUtc.ToUniversalTime();
        if (probed == default)
        {
            return false;
        }

        if (DateTime.UtcNow - probed > ProbeCacheTtl)
        {
            return false;
        }

        isWritable = entry.IsWritable;
        return true;
    }

    /// <summary>
    /// Атомарно сохраняет вердикт в маркер (%LocalAppData%\KTools).
    /// Ошибки тихие: при следующем запуске проба повторится.
    /// </summary>
    /// <param name="isWritable">Вердикт о записи в каталог установки.</param>
    private void SaveVerdictToFile(bool isWritable)
    {
        try
        {
            string cacheFile = GetProbeCacheFilePath(_baseDir);
            string? directory = Path.GetDirectoryName(cacheFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry = new ProbeCacheEntry
            {
                BaseDir = _baseDir,
                IsWritable = isWritable,
                AppVersion = GetCurrentAppVersion(),
                ProbedAtUtc = DateTime.UtcNow,
            };
            string json = JsonSerializer.Serialize(entry);
            string tempFile = cacheFile + ".tmp";
            File.WriteAllText(tempFile, json, Encoding.UTF8);
            File.Move(tempFile, cacheFile, overwrite: true);
        }
        catch (Exception)
        {
            // Тихий кэш: маркер необязателен, проба повторится при следующем запуске.
        }
    }

    /// <summary>
    /// Приводит путь установки к сопоставимому виду для сравнения маркера.
    /// </summary>
    /// <param name="baseDir">Проверяемый путь установки.</param>
    /// <returns>Нормализованный путь.</returns>
    private static string NormalizeBaseDir(string baseDir)
    {
        try
        {
            return Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return baseDir.Trim();
        }
    }

    /// <summary>
    /// Вычисляет стабильный между запусками отпечаток пути установки.
    /// </summary>
    /// <param name="baseDir">Путь установки.</param>
    /// <returns>Первые 16 hex-символов SHA256 в нижнем регистре.</returns>
    private static string ComputeBaseDirFingerprint(string baseDir)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch (Exception)
        {
            normalized = baseDir.ToLowerInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Возвращает версию сборки для инвалидации маркера при обновлении.
    /// </summary>
    /// <returns>Версия приложения или 0.0.0 при недоступности.</returns>
    private static string GetCurrentAppVersion()
    {
        try
        {
            Assembly assembly = typeof(PathManager).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                string version = plus > 0 ? informational[..plus] : informational;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Trim();
                }
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// Запись маркера решения о записи в каталог установки.
    /// </summary>
    private sealed class ProbeCacheEntry
    {
        /// <summary>Путь установки, для которого выполнен вердикт.</summary>
        public string? BaseDir { get; set; }

        /// <summary>Доступен ли каталог установки для записи.</summary>
        public bool IsWritable { get; set; }

        /// <summary>Версия приложения на момент пробы.</summary>
        public string? AppVersion { get; set; }

        /// <summary>Момент пробы в UTC.</summary>
        public DateTime ProbedAtUtc { get; set; }
    }

    /// <summary>
    /// Найти путь к исполняемому файлу утилиты (ffmpeg, mkvmerge, eac3to и др.).
    /// Проверяет как папку bin приложения, так и пользовательскую директорию LocalAppData.
    /// </summary>
    /// <param name="binaryName">Имя бинарного файла утилиты.</param>
    /// <returns>Полный абсолютный путь к файлу утилиты на диске.</returns>
    public string GetBinaryPath(string binaryName)
    {
        if (OperatingSystem.IsWindows() && !binaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            binaryName += ".exe";
        }

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

        // Замена имени в соответствии с соглашением именования K-Tools
        string targetName = binaryName.ToLowerInvariant() switch
        {
            "ffmpeg.exe" => "kt-ffmpeg.exe",
            "ffprobe.exe" => "kt-ffprobe.exe",
            _ => binaryName
        };

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

