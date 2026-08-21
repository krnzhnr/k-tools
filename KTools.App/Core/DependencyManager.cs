// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Encoders;
using KTools_App.Services.Contracts;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;

namespace KTools_App.Core;

/// <summary>
/// Перечисление, представляющее текущий статус внешней бинарной зависимости.
/// </summary>
public enum DependencyStatus
{
    /// <summary>Зависимость успешно установлена и верифицирована.</summary>
    Installed,
    /// <summary>Зависимость отсутствует на диске.</summary>
    NotInstalled,
    /// <summary>Выполняется асинхронное скачивание архива.</summary>
    Downloading,
    /// <summary>Выполняется распаковка архивных файлов.</summary>
    Extracting,
    /// <summary>Произошла ошибка при скачивании, распаковке или верификации.</summary>
    Error
}

/// <summary>
/// Потокобезопасный синглтон-менеджер для проверки, скачивания, верификации и удаления внешних зависимостей.
/// Инкапсулирует логику сетевого взаимодействия и интеграции с системным декомпрессором.
/// </summary>
public class DependencyManager : IDependencyManager
{
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly ISettingsManager _settingsManager;
    private readonly IHardwareCapabilityCache _hardwareCache;

    private const string DepsReleaseTag = "deps-v1";
    private const string DepsBaseUrl = $"https://github.com/krnzhnr/k-tools/releases/download/{DepsReleaseTag}";

    private readonly string _binDir;
    private readonly Dictionary<string, DependencyStatus> _statuses = new();
    private readonly List<DependencyInfo> _registry = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, CancellationTokenSource> _activeDownloads = new();

    /// <summary>Событие, возникающее при изменении статуса любой из зависимостей.</summary>
    public event Action<string, DependencyStatus>? StatusChanged;
    
    /// <summary>Событие прогресса скачивания зависимости (ключ, процент выполнения от 0 до 100).</summary>
    public event Action<string, int>? ProgressChanged;
    
    /// <summary>Событие обновления скорости скачивания зависимости (ключ, форматированная строка скорости).</summary>
    public event Action<string, string>? SpeedUpdated;
    
    /// <summary>Событие завершения процесса установки зависимости (ключ, признак успеха, сообщение об ошибке).</summary>
    public event Action<string, bool, string>? InstallFinished;

    /// <summary>
    /// Инициализирует новый экземпляр класса DependencyManager с внедрением зависимостей.
    /// </summary>
    public DependencyManager(
        ILogService logService,
        IPathManager pathManager,
        IHttpClientFactory httpClientFactory,
        ISettingsManager settingsManager,
        IHardwareCapabilityCache hardwareCache)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _hardwareCache = hardwareCache ?? throw new ArgumentNullException(nameof(hardwareCache));
        // Используем метод интеллектуального поиска директории bin
        _binDir = _pathManager.GetBinDirectory();
        _httpClient = _httpClientFactory.CreateClient("DefaultClient");
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");
        }

        InitializeRegistry();
        RefreshAllStatuses();
    }

    /// <summary>
    /// Инициализирует внутренний реестр зависимостей K-Tools.
    /// </summary>
    private void InitializeRegistry()
    {
        _registry.Add(new DependencyInfo
        {
            Key = "ffmpeg",
            DisplayName = "FFmpeg + QAAC",
            Description = "Кодирование аудио и видео потоков",
            IconName = "video",
            Subfolder = "ffmpeg",
            SizeMb = 471.4,
            ArchiveSizeMb = 122.0,
            ArchiveName = "ffmpeg.tar.xz",
            VerifyBinary = "kt-ffmpeg.exe",
            IsRequired = true
        });

        _registry.Add(new DependencyInfo
        {
            Key = "mkvtoolnix",
            DisplayName = "MKVToolNix",
            Description = "Слияние, сборка и парсинг контейнеров MKV",
            IconName = "share",
            Subfolder = "mkvtoolnix",
            SizeMb = 20.6,
            ArchiveSizeMb = 5.68,
            ArchiveName = "mkvtoolnix.tar.xz",
            VerifyBinary = "mkvmerge.exe",
            IsRequired = true
        });

        _registry.Add(new DependencyInfo
        {
            Key = "eac3to",
            DisplayName = "eac3to",
            Description = "Изменение скорости аудио (PAL NTSC)",
            IconName = "music",
            Subfolder = "eac3to",
            SizeMb = 11.4,
            ArchiveSizeMb = 3.89,
            ArchiveName = "eac3to.tar.xz",
            VerifyBinary = "eac3to.exe",
            IsRequired = false
        });

        _registry.Add(new DependencyInfo
        {
            Key = "dee",
            DisplayName = "Dolby Encoding Engine",
            Description = "Профессиональный даунмикс аудио в Stereo 2.0",
            IconName = "headphone",
            Subfolder = "DEE",
            SizeMb = 185.8,
            ArchiveSizeMb = 48.1,
            ArchiveName = "dee.tar.xz",
            VerifyBinary = "dee.exe",
            IsRequired = false
        });

        _registry.Add(new DependencyInfo
        {
            Key = "eac3to_decoders",
            DisplayName = "Декодеры eac3to",
            Description = "Декодеры Nero для поддержки AAC/DTS",
            IconName = "music",
            Subfolder = "eac3to_decoders",
            SizeMb = 5.0,
            ArchiveSizeMb = 4.8,
            ArchiveName = "eac3to_decoders.tar.xz",
            VerifyBinary = "eac3to Decoder Pack 1.4.exe",
            IsRequired = false
        });

        _registry.Add(new DependencyInfo
        {
            Key = "yt-dlp",
            DisplayName = "yt-dlp (Nightly)",
            Description = "Загрузка медиа-контента из сети (nightly-сборка)",
            IconName = "globe",
            Subfolder = "yt-dlp",
            SizeMb = 50.0,
            ArchiveSizeMb = 50.0,
            ArchiveName = "yt-dlp.exe",
            VerifyBinary = "yt-dlp.exe",
            IsRequired = false,
            CustomDownloadUrl = "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe",
            IsRawExecutable = true
        });

        _registry.Add(new DependencyInfo
        {
            Key = "node",
            DisplayName = "Node.js (Portable)",
            Description = "Локальное окружение выполнения JavaScript",
            IconName = "globe",
            Subfolder = "node",
            SizeMb = 70.0,
            ArchiveSizeMb = 35.0,
            ArchiveName = "node-v22.11.0-win-x64.zip",
            VerifyBinary = "node.exe",
            IsRequired = false,
            CustomDownloadUrl = "https://nodejs.org/dist/v22.11.0/node-v22.11.0-win-x64.zip",
            StripTopLevelFolder = true
        });
    }

    /// <summary>
    /// Возвращает список всех зарегистрированных зависимостей.
    /// </summary>
    public IReadOnlyList<DependencyInfo> GetRegistry() => _registry;

    /// <summary>
    /// Сканирует диск и обновляет статусы всех зависимостей.
    /// </summary>
    public void RefreshAllStatuses()
    {
        lock (_statuses)
        {
            foreach (var dep in _registry)
            {
                bool present = IsBinaryPresent(dep);
                _statuses[dep.Key] = present ? DependencyStatus.Installed : DependencyStatus.NotInstalled;
            }
        }
    }

    /// <summary>
    /// Проверяет физическое наличие исполняемого файла-маркера для указанной зависимости.
    /// </summary>
    private bool IsBinaryPresent(DependencyInfo dep)
    {
        // Для декодеров eac3to проверяем наличие системных DirectShow-фильтров Nero в Windows
        if (dep.Key.Equals("eac3to_decoders", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Проверяем как SysWOW64 (для 32-битного фильтра на 64-битной ОС), так и System32
                string sysWow64Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "NeAudio2.ax");
                string system32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "NeAudio2.ax");
                return File.Exists(sysWow64Path) || File.Exists(system32Path);
            }
            catch (Exception ex)
            {
                _logService.Error($"Ошибка при проверке установленных декодеров Nero: {ex.Message}", "DependencyManager");
                return false;
            }
        }

        // 1. Проверяем локальный путь релиза
        string localPath = Path.Combine(_binDir, dep.Subfolder, dep.VerifyBinary);
        if (File.Exists(localPath))
        {
            if (dep.Key == "node")
            {
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(localPath);
                    if (versionInfo.FileMajorPart < 22)
                    {
                        _logService.Info($"Обнаружена устаревшая версия Node.js ({versionInfo.FileVersion}). Требуется обновление до v22.", "DependencyManager");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Warn($"Не удалось проверить версию Node.js: {ex.Message}", "DependencyManager");
                }
            }
            return true;
        }

        // 2. Проверяем режим разработки (через PathManager)
        string resolvedPath = _pathManager.GetBinaryPath(dep.VerifyBinary);
        if (File.Exists(resolvedPath) && !resolvedPath.Equals(dep.VerifyBinary, StringComparison.OrdinalIgnoreCase))
        {
            if (dep.Key == "node")
            {
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(resolvedPath);
                    if (versionInfo.FileMajorPart < 22)
                    {
                        _logService.Info($"Обнаружена устаревшая версия Node.js в режиме разработки ({versionInfo.FileVersion}). Требуется обновление до v22.", "DependencyManager");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Warn($"Не удалось проверить версию Node.js в режиме разработки: {ex.Message}", "DependencyManager");
                }
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Возвращает текущий статус указанной зависимости.
    /// </summary>
    public DependencyStatus GetStatus(string key)
    {
        lock (_statuses)
        {
            return _statuses.TryGetValue(key, out var status) ? status : DependencyStatus.NotInstalled;
        }
    }

    /// <summary>
    /// Устанавливает и сообщает статус зависимости.
    /// </summary>
    private void SetStatus(string key, DependencyStatus status)
    {
        lock (_statuses)
        {
            _statuses[key] = status;
        }
        StatusChanged?.Invoke(key, status);
    }

    /// <summary>
    /// Возвращает признак того, установлена ли зависимость.
    /// </summary>
    public bool IsInstalled(string key)
    {
        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return dep != null && IsBinaryPresent(dep);
    }

    private readonly Dictionary<string, bool> _updatesAvailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _installedVersionsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Проверяет, доступно ли обновление для указанной зависимости.
    /// </summary>
    public bool IsUpdateAvailable(string key)
    {
        lock (_updatesAvailable)
        {
            return _updatesAvailable.TryGetValue(key, out bool available) && available;
        }
    }

    /// <summary>
    /// Устанавливает имитацию доступности обновления для отладки и тестирования UI.
    /// </summary>
    public void SetSimulatedUpdateAvailable(string key, bool available)
    {
        lock (_updatesAvailable)
        {
            _updatesAvailable[key] = available;
        }
        StatusChanged?.Invoke(key, GetStatus(key));
    }

    private readonly Dictionary<string, string> _cachedVersions = new();

    /// <summary>
    /// Определяет и возвращает строку версии установленной зависимости.
    /// </summary>
    public string GetInstalledVersion(string key)
    {
        if (!IsInstalled(key)) return string.Empty;

        lock (_cachedVersions)
        {
            if (_cachedVersions.TryGetValue(key, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }
        }

        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null) return string.Empty;

        string binaryPath = _pathManager.GetBinaryPath(dep.VerifyBinary);
        if (!File.Exists(binaryPath)) return string.Empty;

        try
        {
            if (key.Equals("yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                string ver = _settingsManager.GetSetting("Updates", "YtDlpInstalledVersion", "Nightly");
                lock (_cachedVersions) { _cachedVersions[key] = ver; }
                return ver;
            }

            if (key.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                var vi = FileVersionInfo.GetVersionInfo(binaryPath);
                string ver = string.IsNullOrEmpty(vi.FileVersion) ? "v22.11.0" : $"v{vi.FileVersion}";
                lock (_cachedVersions) { _cachedVersions[key] = ver; }
                return ver;
            }

            // Для FFmpeg, MKVToolNix, eac3to вызываем исполняемый файл и извлекаем версию из первой строки вывода
            // Для eac3to обязательно передаем -log=nul для подавления создания eac3to.log
            string args = key switch
            {
                "ffmpeg" => "-version",
                "mkvtoolnix" => "-V",
                "eac3to" => "-log=nul",
                _ => string.Empty
            };

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string line = process.StandardOutput.ReadLine() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(line))
            {
                line = process.StandardError.ReadLine() ?? string.Empty;
            }
            process.WaitForExit(1000);

            var match = System.Text.RegularExpressions.Regex.Match(line, @"v?(\d+\.\d+(\.\d+)?)");
            string result = match.Success ? match.Value : (line.Length > 20 ? line.Substring(0, 20) : line);
            if (!string.IsNullOrEmpty(result))
            {
                lock (_cachedVersions) { _cachedVersions[key] = result; }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logService.Warn($"Не удалось извлечь версию для {key}: {ex.Message}", "DependencyManager");
            return "Установлено";
        }
    }

    /// <summary>
    /// Проверяет, установлены ли все обязательные зависимости приложения.
    /// </summary>
    public bool AreRequiredDependenciesInstalled()
    {
        return _registry.Where(d => d.IsRequired).All(IsBinaryPresent);
    }

    /// <summary>
    /// Запускает асинхронный процесс скачивания и установки зависимости.
    /// </summary>
    /// <param name="key">Уникальный ключ зависимости.</param>
    public async Task InstallDependencyAsync(string key)
    {
        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null)
        {
            _logService.Error($"Запрос на установку неизвестной зависимости '{key}'", "DependencyManager");
            InstallFinished?.Invoke(key, false, "Зависимость не найдена в реестре манифеста.");
            return;
        }

        lock (_activeDownloads)
        {
            if (_activeDownloads.ContainsKey(key))
            {
                return; // Процесс уже запущен
            }
            var cts = new CancellationTokenSource();
            _activeDownloads[key] = cts;
        }

        _logService.Info($"Запущена процедура установки зависимости '{dep.DisplayName}' ({dep.Key})", "DependencyManager");

        // Предварительная проверка доступности бинарных файлов на запись (не заняты ли они другими процессами)
        string verifyPath = Path.Combine(_binDir, dep.Subfolder, dep.VerifyBinary);
        if (File.Exists(verifyPath))
        {
            try
            {
                using (var testStream = new FileStream(verifyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Файл свободно доступен для перезаписи
                }
            }
            catch (IOException ex)
            {
                _logService.Warn($"Исполняемый файл '{verifyPath}' заблокирован другим процессом: {ex.Message}", "DependencyManager");
                InstallFinished?.Invoke(key, false, $"Файл '{dep.VerifyBinary}' заблокирован. Остановите активные задачи кодирования/загрузки в KTools или сторонний процесс, использующий этот файл, и повторите попытку.");
                lock (_activeDownloads)
                {
                    if (_activeDownloads.TryGetValue(key, out var cts))
                    {
                        cts.Dispose();
                        _activeDownloads.Remove(key);
                    }
                }
                return;
            }
        }

        SetStatus(key, DependencyStatus.Downloading);
        string tempArchivePath = Path.Combine(Path.GetTempPath(), dep.ArchiveName);

        try
        {
            // 1. Асинхронное скачивание архива
            string downloadUrl = !string.IsNullOrEmpty(dep.CustomDownloadUrl)
                ? dep.CustomDownloadUrl
                : $"{DepsBaseUrl}/{dep.ArchiveName}";
            _logService.Info($"Начало скачивания архива: {downloadUrl} в {tempArchivePath}", "DependencyManager");
            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempArchivePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;
                    var stopwatch = Stopwatch.StartNew();
                    long lastBytesRead = 0;
                    var lastSpeedUpdate = DateTime.UtcNow;

                    var token = _activeDownloads[key].Token;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, token)) != 0)
                    {
                        token.ThrowIfCancellationRequested();

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                        totalRead += bytesRead;

                        // Расчет и отправка прогресса скачивания
                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int pct = (int)((totalRead * 100) / totalBytes.Value);
                            ProgressChanged?.Invoke(key, pct);
                        }

                        // Расчет и отправка скорости скачивания раз в секунду
                        var now = DateTime.UtcNow;
                        double elapsedSeconds = (now - lastSpeedUpdate).TotalSeconds;
                        if (elapsedSeconds >= 1.0)
                        {
                            long bytesDelta = totalRead - lastBytesRead;
                            double speedBytesPerSec = bytesDelta / elapsedSeconds;
                            string formattedSpeed = FormatSpeed(speedBytesPerSec);
                            SpeedUpdated?.Invoke(key, formattedSpeed);

                            lastBytesRead = totalRead;
                            lastSpeedUpdate = now;
                        }
                    }
                }
            }

            _logService.Info($"Файл/архив '{dep.ArchiveName}' успешно скачан на диск", "DependencyManager");

            string destinationFolder = Path.Combine(_binDir, dep.Subfolder);

            // Гарантируем наличие целевых папок
            try
            {
                Directory.CreateDirectory(destinationFolder);
            }
            catch (UnauthorizedAccessException ex)
            {
                SetStatus(key, DependencyStatus.Error);
                string errMsg = $"Нет прав доступа для создания папки '{destinationFolder}'. " +
                    $"Это может быть связано с ограничениями MSIX или прав пользователя. " +
                    $"Убедитесь что приложение запущено от правильного пользователя. Подробности: {ex.Message}";
                _logService.Error($"Ошибка доступа при распаковке/установке '{dep.DisplayName}': {errMsg}", "DependencyManager");
                InstallFinished?.Invoke(key, false, errMsg);
                return;
            }

            var cancellationToken = _activeDownloads[key].Token;

            if (dep.IsRawExecutable)
            {
                _logService.Info($"Копирование исполняемого файла в папку '{destinationFolder}'...", "DependencyManager");
                string targetPath = Path.Combine(destinationFolder, dep.VerifyBinary);
                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { /* Игнорируем ошибки удаления старого файла */ }
                }
                File.Move(tempArchivePath, targetPath, true);
                _logService.Info($"Файл '{dep.VerifyBinary}' успешно скопирован в целевую папку", "DependencyManager");
            }
            else
            {
                // 2. Распаковка архива
                SetStatus(key, DependencyStatus.Extracting);
                _logService.Info($"Начало распаковки архива в папку '{destinationFolder}'", "DependencyManager");
                await ExtractArchiveAsync(dep, tempArchivePath, destinationFolder, cancellationToken);
                _logService.Info($"Распаковка архива '{dep.ArchiveName}' успешно завершена", "DependencyManager");
            }

            // Если устанавливаем декодеры eac3to, нужно запустить тихую установку с повышением прав
            if (key.Equals("eac3to_decoders", StringComparison.OrdinalIgnoreCase))
            {
                string setupPath = Path.Combine(destinationFolder, dep.VerifyBinary);
                if (File.Exists(setupPath))
                {
                    _logService.Info($"Запуск тихой установки декодеров из файла: '{setupPath}' с повышением прав UAC", "DependencyManager");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = setupPath,
                        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                        UseShellExecute = true,
                        Verb = "runas"
                    };

                    try
                    {
                        using var process = Process.Start(startInfo);
                        if (process != null)
                        {
                            _logService.Info("Ожидание завершения установщика декодеров eac3to...", "DependencyManager");
                            await process.WaitForExitAsync(cancellationToken);
                            _logService.Info("Установщик декодеров eac3to успешно завершил работу", "DependencyManager");
                        }
                        else
                        {
                            throw new InvalidOperationException("Не удалось инициализировать процесс установщика декодеров.");
                        }
                    }
                    catch (Exception ex)
                    {
                        string detailedErr = $"Ошибка при выполнении тихого установщика декодеров eac3to. Описание ошибки: {ex.Message}";
                        _logService.Error(detailedErr, "DependencyManager");
                        throw new InvalidOperationException(detailedErr, ex);
                    }
                }
                else
                {
                    string missingSetupErr = $"Файл установщика декодеров '{setupPath}' не найден после распаковки архива.";
                    _logService.Error(missingSetupErr, "DependencyManager");
                    throw new FileNotFoundException(missingSetupErr);
                }
            }

            // 3. Верификация установки
            RefreshAllStatuses();
            if (IsInstalled(key))
            {
                SetStatus(key, DependencyStatus.Installed);
                _logService.Info($"Зависимость '{dep.DisplayName}' успешно установлена и верифицирована", "DependencyManager");
                InstallFinished?.Invoke(key, true, string.Empty);

                // После установки FFmpeg повторно определяем аппаратные возможности (NVENC):
                // кэш мог быть инициализирован, когда бинарник ещё отсутствовал.
                if (key.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    _hardwareCache.Invalidate();
                    await _hardwareCache.InitializeAsync();
                }
            }
            else
            {
                SetStatus(key, DependencyStatus.Error);
                string err = $"Файл-маркер '{dep.VerifyBinary}' отсутствует на диске после распаковки.";
                _logService.Error($"Ошибка верификации '{dep.DisplayName}': {err}", "DependencyManager");
                InstallFinished?.Invoke(key, false, err);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(key, DependencyStatus.NotInstalled);
            _logService.Warn($"Установка зависимости '{dep.DisplayName}' отменена пользователем", "DependencyManager");
            InstallFinished?.Invoke(key, false, "Установка отменена пользователем.");
        }
        catch (Exception ex)
        {
            SetStatus(key, DependencyStatus.Error);
            string detailedErrorMessage = $"Критический сбой в процессе загрузки, верификации или распаковки зависимости '{dep.DisplayName}' (ключ: {dep.Key}). Подробности возникшего исключения: {ex.Message}. Стек вызовов: {ex.StackTrace}";
            _logService.Error(detailedErrorMessage, "DependencyManager");
            InstallFinished?.Invoke(key, false, ex.Message);
        }
        finally
        {
            // Безопасное удаление временного архива
            if (File.Exists(tempArchivePath))
            {
                try { File.Delete(tempArchivePath); } catch { /* Игнорируем ошибки удаления временных файлов */ }
            }

            lock (_activeDownloads)
            {
                if (_activeDownloads.TryGetValue(key, out var cts))
                {
                    cts.Dispose();
                    _activeDownloads.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Отменяет активную задачу загрузки/установки указанной зависимости.
    /// </summary>
    public void CancelInstallation(string key)
    {
        lock (_activeDownloads)
        {
            if (_activeDownloads.TryGetValue(key, out var cts))
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Асинхронно распаковывает архив (.tar.xz или .zip) в целевую папку, используя стороннюю библиотеку SharpCompress.
    /// Выполняется в фоновом режиме на пуле потоков без блокировки основного UI-потока.
    /// </summary>
    /// <param name="dep">Информация о зависимости.</param>
    /// <param name="archivePath">Абсолютный путь к исходному архиву на диске.</param>
    /// <param name="destinationDir">Абсолютный путь к целевой директории распаковки.</param>
    /// <param name="cancellationToken">Токен отмены для прерывания процесса распаковки по требованию пользователя.</param>
    /// <exception cref="ArgumentNullException">Инициируется, если один из входных путей равен null.</exception>
    /// <exception cref="OperationCanceledException">Инициируется, если процесс был отменен через token.</exception>
    private async Task ExtractArchiveAsync(DependencyInfo dep, string archivePath, string destinationDir, CancellationToken cancellationToken)
    {
        if (archivePath == null)
        {
            throw new ArgumentNullException(nameof(archivePath), "Путь к архиву не может быть пустым (null).");
        }

        if (destinationDir == null)
        {
            throw new ArgumentNullException(nameof(destinationDir), "Путь к целевой папке не может быть пустым (null).");
        }

        _logService.Info($"Запуск асинхронной распаковки архива '{archivePath}' в папку '{destinationDir}'...", "DependencyManager");

        await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isZip = archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            using var fileStream = File.OpenRead(archivePath);
            using var xzStream = isZip ? null : new XZStream(fileStream);
            using var reader = ReaderFactory.OpenReader(xzStream ?? (Stream)fileStream);

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!reader.Entry.IsDirectory)
                {
                    string? entryKey = reader.Entry.Key;
                    if (string.IsNullOrEmpty(entryKey)) continue;

                    if (dep.StripTopLevelFolder)
                    {
                        int slashIndex = entryKey.IndexOf('/');
                        if (slashIndex == -1) slashIndex = entryKey.IndexOf('\\');
                        if (slashIndex != -1 && slashIndex < entryKey.Length - 1)
                        {
                            entryKey = entryKey.Substring(slashIndex + 1);
                        }
                    }

                    _logService.Info($"Распаковка файла из архива: {reader.Entry.Key} -> {entryKey}", "DependencyManager");
                    string targetPath = Path.Combine(destinationDir, entryKey);
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (dir != null && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using var entryStream = reader.OpenEntryStream();
                    using var targetFileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                    await entryStream.CopyToAsync(targetFileStream, cancellationToken);
                }
            }

            _logService.Info($"Распаковка архива '{archivePath}' успешно завершена через SharpCompress", "DependencyManager");
        }, cancellationToken);
    }

    /// <summary>
    /// Физически удаляет папку зависимости с диска и сбрасывает статус.
    /// </summary>
    public bool RemoveDependency(string key)
    {
        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null)
        {
            _logService.Error($"Попытка удаления неизвестной зависимости '{key}'", "DependencyManager");
            return false;
        }

        _logService.Info($"Запрос на удаление зависимости '{dep.DisplayName}'", "DependencyManager");

        if (key.Equals("eac3to_decoders", StringComparison.OrdinalIgnoreCase))
        {
            _logService.Info("Запрос на удаление декодеров eac3to: использование оригинального деинсталлятора из реестра", "DependencyManager");
            bool uninstalledViaSetup = false;
            try
            {
                string? uninstallStr = GetEac3toDecodersUninstallString();
                if (!string.IsNullOrEmpty(uninstallStr))
                {
                    string exePath = uninstallStr.Trim().Trim('"');
                    if (File.Exists(exePath))
                    {
                        _logService.Info($"Запуск оригинального деинсталлятора: '{exePath}' в тихом режиме", "DependencyManager");
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        using var process = Process.Start(startInfo);
                        if (process != null)
                        {
                            process.WaitForExit();
                            _logService.Info("Деинсталлятор eac3to Decoder Pack успешно завершил работу", "DependencyManager");
                            uninstalledViaSetup = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Error($"Ошибка при вызове официального деинсталлятора eac3to: {ex.Message}", "DependencyManager");
            }

            if (!uninstalledViaSetup)
            {
                _logService.Warn("Не удалось использовать официальный деинсталлятор. Запуск резервного метода безопасной ручной деинсталляции", "DependencyManager");
                string tempBatPath = Path.Combine(Path.GetTempPath(), $"uninstall_eac3to_decoders_{Guid.NewGuid():N}.bat");
                try
                {
                    var commands = new List<string>
                    {
                        "@echo off",
                        "chcp 65001 > nul",
                        "",
                        ":: Разрегистрация DirectShow-фильтров из SysWOW64",
                        "if exist \"%SystemRoot%\\SysWOW64\\NeAudio2.ax\" \"%SystemRoot%\\SysWOW64\\regsvr32.exe\" /u /s \"%SystemRoot%\\SysWOW64\\NeAudio2.ax\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\ASAudioHD.ax\" \"%SystemRoot%\\SysWOW64\\regsvr32.exe\" /u /s \"%SystemRoot%\\SysWOW64\\ASAudioHD.ax\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\CinemasterAudio.dll\" \"%SystemRoot%\\SysWOW64\\regsvr32.exe\" /u /s \"%SystemRoot%\\SysWOW64\\CinemasterAudio.dll\"",
                        "",
                        ":: Разрегистрация DirectShow-фильтров из System32",
                        "if exist \"%SystemRoot%\\System32\\NeAudio2.ax\" \"%SystemRoot%\\System32\\regsvr32.exe\" /u /s \"%SystemRoot%\\System32\\NeAudio2.ax\"",
                        "if exist \"%SystemRoot%\\System32\\ASAudioHD.ax\" \"%SystemRoot%\\System32\\regsvr32.exe\" /u /s \"%SystemRoot%\\System32\\ASAudioHD.ax\"",
                        "if exist \"%SystemRoot%\\System32\\CinemasterAudio.dll\" \"%SystemRoot%\\System32\\regsvr32.exe\" /u /s \"%SystemRoot%\\System32\\CinemasterAudio.dll\"",
                        "",
                        ":: Удаление специфичных файлов декодеров из SysWOW64",
                        "if exist \"%SystemRoot%\\SysWOW64\\NeAudio2.ax\" del /f /q \"%SystemRoot%\\SysWOW64\\NeAudio2.ax\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\NeDtsDec.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\NeDtsDec.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\NeEacDec.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\NeEacDec.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\AdvrCntr2.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\AdvrCntr2.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\ASAudioHD.ax\" del /f /q \"%SystemRoot%\\SysWOW64\\ASAudioHD.ax\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\checkactivate.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\checkactivate.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\MagCore.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\MagCore.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\MagPCMac.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\MagPCMac.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\MagUIEngine.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\MagUIEngine.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\MagUIInter.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\MagUIInter.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\dtsdecoderdll.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\dtsdecoderdll.dll\"",
                        "if exist \"%SystemRoot%\\SysWOW64\\CinemasterAudio.dll\" del /f /q \"%SystemRoot%\\SysWOW64\\CinemasterAudio.dll\"",
                        "",
                        ":: Удаление специфичных файлов декодеров из System32",
                        "if exist \"%SystemRoot%\\System32\\NeAudio2.ax\" del /f /q \"%SystemRoot%\\System32\\NeAudio2.ax\"",
                        "if exist \"%SystemRoot%\\System32\\NeDtsDec.dll\" del /f /q \"%SystemRoot%\\System32\\NeDtsDec.dll\"",
                        "if exist \"%SystemRoot%\\System32\\NeEacDec.dll\" del /f /q \"%SystemRoot%\\System32\\NeEacDec.dll\"",
                        "if exist \"%SystemRoot%\\System32\\AdvrCntr2.dll\" del /f /q \"%SystemRoot%\\System32\\AdvrCntr2.dll\"",
                        "if exist \"%SystemRoot%\\System32\\ASAudioHD.ax\" del /f /q \"%SystemRoot%\\System32\\ASAudioHD.ax\"",
                        "if exist \"%SystemRoot%\\System32\\checkactivate.dll\" del /f /q \"%SystemRoot%\\System32\\checkactivate.dll\"",
                        "if exist \"%SystemRoot%\\System32\\MagCore.dll\" del /f /q \"%SystemRoot%\\System32\\MagCore.dll\"",
                        "if exist \"%SystemRoot%\\System32\\MagPCMac.dll\" del /f /q \"%SystemRoot%\\System32\\MagPCMac.dll\"",
                        "if exist \"%SystemRoot%\\System32\\MagUIEngine.dll\" del /f /q \"%SystemRoot%\\System32\\MagUIEngine.dll\"",
                        "if exist \"%SystemRoot%\\System32\\MagUIInter.dll\" del /f /q \"%SystemRoot%\\System32\\MagUIInter.dll\"",
                        "if exist \"%SystemRoot%\\System32\\dtsdecoderdll.dll\" del /f /q \"%SystemRoot%\\System32\\dtsdecoderdll.dll\"",
                        "if exist \"%SystemRoot%\\System32\\CinemasterAudio.dll\" del /f /q \"%SystemRoot%\\System32\\CinemasterAudio.dll\"",
                        "",
                        ":: Удаление файлов из директории Windows",
                        "if exist \"%SystemRoot%\\neroAacEnc.exe\" del /f /q \"%SystemRoot%\\neroAacEnc.exe\"",
                        "if exist \"%SystemRoot%\\surcode\" rd /s /q \"%SystemRoot%\\surcode\"",
                        "",
                        ":: Очистка разделов реестра",
                        "reg delete \"HKLM\\SOFTWARE\\Ahead\\Installation\\Families\\Nero 7\" /f >nul 2>&1",
                        "reg delete \"HKLM\\SOFTWARE\\Ahead\\Installation\\Families\\Plugins\" /f >nul 2>&1",
                        "reg delete \"HKLM\\SOFTWARE\\Sonic\\CommonMPEGDecoders\\4.2\\AudioDecoder\" /f >nul 2>&1",
                        "reg delete \"HKLM\\SOFTWARE\\Minnetonka Audio Software\\SurCode DVD-DTS\" /f >nul 2>&1",
                        "reg delete \"HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{167887DA-6C4F-4265-8139-8750A543FD52}_is1\" /f >nul 2>&1",
                        "reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{167887DA-6C4F-4265-8139-8750A543FD52}_is1\" /f >nul 2>&1"
                    };

                    File.WriteAllLines(tempBatPath, commands, System.Text.Encoding.ASCII);

                    _logService.Info($"Запуск временного батника удаления '{tempBatPath}' с правами администратора", "DependencyManager");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{tempBatPath}\"",
                        UseShellExecute = true,
                        Verb = "runas",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        _logService.Info("Резервное удаление декодеров eac3to завершено успешно", "DependencyManager");
                    }
                    else
                    {
                        throw new InvalidOperationException("Не удалось запустить процесс удаления.");
                    }
                }
                catch (Exception ex)
                {
                    _logService.Error($"Ошибка при резервном удалении декодеров Nero: {ex.Message}", "DependencyManager");
                }
                finally
                {
                    if (File.Exists(tempBatPath))
                    {
                        try { File.Delete(tempBatPath); } catch { /* Игнорируем ошибки удаления временного файла */ }
                    }
                }
            }
        }

        string folderPath = Path.Combine(_binDir, dep.Subfolder);
        if (!Directory.Exists(folderPath))
        {
            _logService.Warn($"Папка зависимости '{dep.DisplayName}' не обнаружена на диске. Сброс статуса в NotInstalled", "DependencyManager");
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }

        try
        {
            Directory.Delete(folderPath, true);
            _logService.Info($"Папка зависимости '{dep.DisplayName}' успешно удалена с диска", "DependencyManager");
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }
        catch (Exception ex)
        {
            _logService.Error($"Не удалось удалить папку зависимости '{dep.DisplayName}': {ex.Message}", "DependencyManager");
            SetStatus(key, DependencyStatus.Error);
            return false;
        }
    }

    /// <summary>
    /// Вспомогательный метод для форматирования скорости скачивания в человекочитаемый вид.
    /// </summary>
    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1048576)
        {
            return $"{bytesPerSec / 1048576:F1} МБ/с";
        }
        if (bytesPerSec >= 1024)
        {
            return $"{bytesPerSec / 1024:F1} КБ/с";
        }
        return $"{bytesPerSec:F0} Б/с";
    }

    /// <summary>
    /// Ищет строку деинсталляции eac3to Decoder Pack в реестре Windows.
    /// </summary>
    private static string? GetEac3toDecodersUninstallString()
    {
        string[] registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var path in registryPaths)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;

                // Сначала пробуем прямой поиск по известному GUID инсталлятора
                using var subKeyGuid = key.OpenSubKey("{167887DA-6C4F-4265-8139-8750A543FD52}_is1");
                if (subKeyGuid != null)
                {
                    var val = subKeyGuid.GetValue("UninstallString")?.ToString();
                    if (!string.IsNullOrEmpty(val)) return val;
                }

                // Резервный поиск по DisplayName в цикле
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName")?.ToString();
                        if (displayName != null && displayName.Contains("eac3to Decoder Pack", StringComparison.OrdinalIgnoreCase))
                        {
                            var val = subKey.GetValue("UninstallString")?.ToString();
                            if (!string.IsNullOrEmpty(val)) return val;
                        }
                    }
                    catch { /* Игнорируем ошибки доступа к отдельным разделам */ }
                }
            }
            catch { /* Игнорируем ошибки доступа к ветке реестра */ }
        }
        return null;
    }

    /// <summary>
    /// Выполняет фоновую проверку обновлений всех зависимостей (yt-dlp, FFmpeg, MKVToolNix, eac3to) раз в сутки.
    /// </summary>
    public async Task CheckAllDependencyUpdatesAsync(bool force = false)
    {
        try
        {
            string lastCheckStr = _settingsManager.GetSetting("Updates", "LastDepsCheckTime", string.Empty);
            if (!force && DateTime.TryParse(lastCheckStr, out DateTime lastCheckTime))
            {
                if (DateTime.UtcNow - lastCheckTime < TimeSpan.FromDays(1))
                {
                    _logService.Info("Проверка обновлений всех зависимостей выполнялась менее 24 часов назад. Пропуск.", "DependencyManager");
                    return;
                }
            }

            _logService.Info("Запуск фоновой проверки обновлений всех зависимостей KTools...", "DependencyManager");

            // 1. Проверяем обновления yt-dlp
            await CheckAndUpdateYtDlpAsync(force: true);

            // 2. Проверяем остальной набор зависимостей из релиза deps-v1 на GitHub
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/krnzhnr/k-tools/releases/tags/deps-v1");
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");

            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("body", out var bodyProp))
                {
                    string body = bodyProp.GetString() ?? string.Empty;
                    var match = System.Text.RegularExpressions.Regex.Match(body, @"```json:versions\s*(\{[\s\S]*?\})\s*```");
                    if (match.Success)
                    {
                        string jsonVersions = match.Groups[1].Value;
                        using var verDoc = JsonDocument.Parse(jsonVersions);
                        foreach (var prop in verDoc.RootElement.EnumerateObject())
                        {
                            string key = prop.Name;
                            string remoteVersion = prop.Value.GetString() ?? string.Empty;
                            if (string.IsNullOrEmpty(remoteVersion)) continue;

                            string localVer = GetInstalledVersion(key);
                            if (IsInstalled(key) && !string.IsNullOrEmpty(localVer) && !remoteVersion.Equals(localVer, StringComparison.OrdinalIgnoreCase))
                            {
                                lock (_updatesAvailable)
                                {
                                    _updatesAvailable[key] = true;
                                }
                                _logService.Info($"Обнаружена новая версия для зависимости '{key}': remote={remoteVersion}, local={localVer}", "DependencyManager");
                                StatusChanged?.Invoke(key, GetStatus(key));
                            }
                        }
                    }
                }
            }

            _settingsManager.SetSetting("Updates", "LastDepsCheckTime", DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            _logService.Error($"Ошибка при фоновой проверке обновлений зависимостей: {ex.Message}", "DependencyManager");
        }
    }

    /// <summary>
    /// Выполняет проверку обновлений для утилиты yt-dlp раз в сутки и обновляет её при необходимости.
    /// </summary>
    public async Task CheckAndUpdateYtDlpAsync(bool force = false)
    {
        // Если утилита yt-dlp не установлена, автообновление не требуется
        if (!IsInstalled("yt-dlp"))
        {
            _logService.Info("Проверка обновлений yt-dlp пропущена, так как утилита не установлена.", "DependencyManager");
            return;
        }

        try
        {
            // Проверяем время последней успешной проверки
            string lastCheckStr = _settingsManager.GetSetting("Updates", "LastYtDlpCheckTime", string.Empty);
            if (!force && DateTime.TryParse(lastCheckStr, out DateTime lastCheckTime))
            {
                if (DateTime.UtcNow - lastCheckTime < TimeSpan.FromDays(1))
                {
                    _logService.Info("Проверка обновлений yt-dlp выполнялась менее 24 часов назад. Пропуск.", "DependencyManager");
                    return;
                }
            }

            _logService.Info("Запуск фоновой проверки обновлений yt-dlp nightly...", "DependencyManager");

            // Выполняем GET-запрос к GitHub API для получения последнего релиза
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest");
            // GitHub API требует User-Agent
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logService.Warn($"Не удалось получить данные о релизе yt-dlp. Код ответа: {response.StatusCode}", "DependencyManager");
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                _logService.Warn("Отсутствует свойство tag_name в ответе GitHub API для yt-dlp.", "DependencyManager");
                return;
            }

            string latestTag = tagProp.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(latestTag))
            {
                _logService.Warn("Тег последней версии yt-dlp пуст.", "DependencyManager");
                return;
            }

            // Запоминаем время текущей проверки
            _settingsManager.SetSetting("Updates", "LastYtDlpCheckTime", DateTime.UtcNow.ToString("o"));

            string localVersion = _settingsManager.GetSetting("Updates", "YtDlpInstalledVersion", string.Empty);
            _logService.Info($"Последняя доступная версия yt-dlp: {latestTag}. Локальная версия: {localVersion}", "DependencyManager");

            if (latestTag.Equals(localVersion, StringComparison.OrdinalIgnoreCase))
            {
                lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = false; }
                _logService.Info("Установлена актуальная версия yt-dlp. Обновление не требуется.", "DependencyManager");
                return;
            }

            // Если версии не совпадают, фиксируем наличие обновления и запускаем установку/обновление
            lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = true; }
            _logService.Info($"Обнаружена новая версия yt-dlp: {latestTag}. Запуск автоматического обновления...", "DependencyManager");
            
            // Запускаем асинхронную установку
            await InstallDependencyAsync("yt-dlp");

            // Если установка завершилась успехом, сохраняем новую версию в настройки
            if (IsInstalled("yt-dlp"))
            {
                lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = false; }
                _settingsManager.SetSetting("Updates", "YtDlpInstalledVersion", latestTag);
                _logService.Info($"yt-dlp успешно обновлен до версии {latestTag}", "DependencyManager");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Исключение при проверке обновлений yt-dlp: {ex.Message}", "DependencyManager");
        }
    }
}
