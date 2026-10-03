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

using KTools_App.Diagnostics;
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
    private const string SourceName = nameof(DependencyManager);

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
    private readonly Dictionary<string, int> _downloadProgress = new();
    private readonly Dictionary<string, string> _downloadSpeed = new();

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
            Description = "Кодирование аудио и видеодорожек",
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

        _registry.Add(new DependencyInfo
        {
            Key = "whisper_cpu",
            DisplayName = "Whisper (CPU)",
            Description = "Распознавание речи на процессоре (AVX2)",
            IconName = "audio",
            Subfolder = "whisper-cpu",
            SizeMb = 18.0,
            ArchiveSizeMb = 8.5,
            ArchiveName = "whisper-bin-x64.zip",
            VerifyBinary = "whisper-cli.exe",
            IsRequired = false,
            CustomDownloadUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip",
            StripTopLevelFolder = true
        });

        _registry.Add(new DependencyInfo
        {
            Key = "whisper_cuda",
            DisplayName = "Whisper (NVIDIA CUDA)",
            Description = "Распознавание речи на GPU NVIDIA (CUDA)",
            IconName = "audio",
            Subfolder = "whisper-cuda",
            SizeMb = 675.0,
            ArchiveSizeMb = 675.0,
            ArchiveName = "whisper-cublas-12.4.0-bin-x64.zip",
            VerifyBinary = "whisper-cli.exe",
            IsRequired = false,
            CustomDownloadUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-cublas-12.4.0-bin-x64.zip",
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
                // Не сбрасываем статус, если в данный момент для зависимости выполняется скачивание или распаковка
                if (_statuses.TryGetValue(dep.Key, out var currentStatus) &&
                    (currentStatus == DependencyStatus.Downloading || currentStatus == DependencyStatus.Extracting))
                {
                    continue;
                }

                lock (_activeDownloads)
                {
                    if (_activeDownloads.ContainsKey(dep.Key))
                    {
                        continue;
                    }
                }

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
                _logService.Write(
                    "dependency.nero_probe_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Ошибка при проверке установленных декодеров Nero",
                    ex,
                    "DependencyManager",
                    properties: LogProps
                        .Create("Tool", "eac3to_decoders")
                        .With("ErrorCode", "NERO_DECODER_PROBE_FAILED"));
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
                        _logService.Write(
                            "dependency.node.outdated",
                            LogLevel.Warning,
                            LogStatus.Skipped,
                            $"Обнаружена устаревшая версия Node.js ({LogRedactor.CompactSafeToken(versionInfo.FileVersion)}), требуется обновление до v22",
                            source: SourceName,
                            properties: LogProps
                                .Create("Tool", "node")
                                .With("Version", LogRedactor.CompactSafeToken(versionInfo.FileVersion))
                                .With("ErrorCode", "DEPENDENCY_OUTDATED"));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Write("dependency.node_version_failed", LogLevel.Warning, LogStatus.Skipped, "Не удалось проверить версию Node.js", ex, "DependencyManager");
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
                        _logService.Write(
                            "dependency.node.outdated_dev",
                            LogLevel.Warning,
                            LogStatus.Skipped,
                            $"Обнаружена устаревшая версия Node.js в режиме разработки ({LogRedactor.CompactSafeToken(versionInfo.FileVersion)}), требуется обновление до v22",
                            source: SourceName,
                            properties: LogProps
                                .Create("Tool", "node")
                                .With("Version", LogRedactor.CompactSafeToken(versionInfo.FileVersion))
                                .With("ErrorCode", "DEPENDENCY_OUTDATED"));
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Write("dependency.node_dev_version_failed", LogLevel.Warning, LogStatus.Skipped, "Не удалось проверить версию Node.js в режиме разработки", ex, "DependencyManager");
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

    /// <summary>
    /// Проверяет, выполняются ли в данный момент какие-либо активные операции скачивания или распаковки зависимостей.
    /// </summary>
    public bool HasActiveOperations
    {
        get
        {
            lock (_activeDownloads)
            {
                if (_activeDownloads.Count > 0) return true;
            }

            lock (_statuses)
            {
                return _statuses.Values.Any(s => s == DependencyStatus.Downloading || s == DependencyStatus.Extracting);
            }
        }
    }

    /// <summary>
    /// Получить сохранённый процент скачивания для указанной зависимости (от 0 до 100).
    /// </summary>
    public int GetDownloadProgress(string key)
    {
        lock (_downloadProgress)
        {
            return _downloadProgress.TryGetValue(key, out int prog) ? prog : 0;
        }
    }

    /// <summary>
    /// Получить сохранённую форматированную скорость скачивания для указанной зависимости.
    /// </summary>
    public string GetDownloadSpeed(string key)
    {
        lock (_downloadSpeed)
        {
            return _downloadSpeed.TryGetValue(key, out string? speed) ? (speed ?? string.Empty) : string.Empty;
        }
    }

    /// <summary>
    /// Проверить, находится ли указанная зависимость в процессе активного скачивания.
    /// </summary>
    public bool IsDownloading(string key)
    {
        lock (_activeDownloads)
        {
            return _activeDownloads.ContainsKey(key);
        }
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
        return GetInstalledVersionAsync(key).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Синхронная обёртка удаления файлов зависимости.
    /// </summary>
    public bool RemoveDependency(string key)
    {
        return RemoveDependencyAsync(key).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Определяет и возвращает строку версии установленной зависимости.
    /// </summary>
    public async Task<string> GetInstalledVersionAsync(string key)
    {
        bool isQaac = key.Equals("qaac", StringComparison.OrdinalIgnoreCase);
        if (!isQaac && !IsInstalled(key)) return string.Empty;

        lock (_cachedVersions)
        {
            if (_cachedVersions.TryGetValue(key, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }
        }

        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null && !isQaac) return string.Empty;

        string binaryPath = dep != null ? _pathManager.GetBinaryPath(dep.VerifyBinary) : string.Empty;
        if (!isQaac && !File.Exists(binaryPath)) return string.Empty;

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

            // Для qaac вызываем qaac64.exe --check
            if (key.Equals("qaac", StringComparison.OrdinalIgnoreCase))
            {
                string qaacPath = _pathManager.GetBinaryPath("qaac64.exe");
                if (!File.Exists(qaacPath)) return string.Empty;

                string qLine = await ReadFirstOutputLineAsync(qaacPath, "--check");

                var qMatch = System.Text.RegularExpressions.Regex.Match(qLine, @"qaac\s+(\d+\.\d+(\.\d+)?)");
                string qResult = qMatch.Success ? qMatch.Groups[1].Value : (qLine.Length > 20 ? qLine.Substring(0, 20) : qLine);
                if (!string.IsNullOrEmpty(qResult))
                {
                    lock (_cachedVersions) { _cachedVersions[key] = qResult; }
                }
                return qResult;
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

            string line = await ReadFirstOutputLineAsync(binaryPath, args);

            var match = System.Text.RegularExpressions.Regex.Match(line, @"v?(\d+(\.\d+)+)");
            string result = match.Success ? match.Groups[1].Value : (line.Length > 20 ? line.Substring(0, 20) : line);
            result = result.TrimStart('v', 'V').Trim();
            if (!string.IsNullOrEmpty(result))
            {
                lock (_cachedVersions) { _cachedVersions[key] = result; }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logService.Write("dependency.version_extract_failed", LogLevel.Warning, LogStatus.Skipped, $"Не удалось извлечь версию зависимости '{key}'", ex, "DependencyManager");
            return "Установлено";
        }
    }

    /// <summary>
    /// Запускает процесс, параллельно читает stdout и stderr и возвращает первую строку вывода
    /// (stdout либо stderr, если stdout пуст). При зависании процесс снимается по таймауту.
    /// </summary>
    private static async Task<string> ReadFirstOutputLineAsync(string fileName, string arguments)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (!process.HasExited)
        {
            try { process.Kill(true); }
            catch { /* Игнорируем ошибки Kill */ }
        }

        // Чтение потоков также ограничиваем по времени: после Kill процесс-потомок
        // может удерживать дескриптор, и ожидание ReadToEndAsync длилось бы вечно.
        string stdout = await ReadToEndWithTimeoutAsync(stdoutTask);
        string stderr = await ReadToEndWithTimeoutAsync(stderrTask);

        string line = stdout.Length > 0 ? stdout.Split('\n')[0].TrimEnd('\r') : string.Empty;
        if (string.IsNullOrWhiteSpace(line) && stderr.Length > 0)
        {
            line = stderr.Split('\n')[0].TrimEnd('\r');
        }
        return line;
    }

    /// <summary>
    /// Ожидает завершения асинхронного чтения потока с жестким таймаутом,
    /// не блокируя вызывающий поток.
    /// </summary>
    private static async Task<string> ReadToEndWithTimeoutAsync(Task<string> readTask)
    {
        if (readTask.IsCompleted)
        {
            try { return await readTask; }
            catch { return string.Empty; }
        }

        try
        {
            Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
            if (completed == readTask)
            {
                return await readTask;
            }
        }
        catch
        {
            // Игнорируем: возвращаем пустой вывод
        }

        return string.Empty;
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
            _logService.Write(
                "dependency.install.unknown_key",
                LogLevel.Error,
                LogStatus.Failed,
                $"Запрошена установка неизвестной зависимости '{LogRedactor.CompactSafeToken(key)}'",
                source: SourceName,
                properties: LogProps
                    .Create("Key", LogRedactor.CompactSafeToken(key))
                    .With("ErrorCode", "DEPENDENCY_NOT_IN_MANIFEST"));
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

        _logService.Write(
            "dependency.install.started",
            LogLevel.Info,
            LogStatus.Running,
            $"Начата установка зависимости '{dep.DisplayName}'",
            source: SourceName,
            properties: LogProps
                .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                .With("Group", dep.DisplayName));

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
                _logService.Write("dependency.binary_locked", LogLevel.Warning, LogStatus.Skipped, $"Исполняемый файл зависимости '{key}' заблокирован другим процессом", ex, "DependencyManager");
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

            // Для компонентов Whisper динамически получаем актуальный URL релиза с бинарными сборками, если доступен
            if (key.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase))
            {
                string resolvedUrl = await ResolveWhisperDownloadUrlAsync(key, dep.ArchiveName, downloadUrl);
                if (!string.IsNullOrEmpty(resolvedUrl))
                {
                    downloadUrl = resolvedUrl;
                }
            }

            _logService.Write(
                "dependency.download_started",
                LogLevel.Info,
                LogStatus.Running,
                $"Начало скачивания архива зависимости '{key}'",
                null,
                "DependencyManager",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = key,
                    ["Stage"] = "download",
                    ["HostLabel"] = SafeHostLabel(downloadUrl),
                    ["FileName"] = dep.ArchiveName
                });
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
                            lock (_downloadProgress)
                            {
                                _downloadProgress[key] = pct;
                            }
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
                            lock (_downloadSpeed)
                            {
                                _downloadSpeed[key] = formattedSpeed;
                            }
                            SpeedUpdated?.Invoke(key, formattedSpeed);

                            lastBytesRead = totalRead;
                            lastSpeedUpdate = now;
                        }
                    }
                }
            }

            _logService.Write(
                "dependency.download_completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Файл/архив '{dep.ArchiveName}' успешно скачан на диск",
                null,
                "DependencyManager",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = key,
                    ["Stage"] = "download",
                    ["FileName"] = dep.ArchiveName
                });

            string destinationFolder = Path.Combine(_binDir, dep.Subfolder);

            // Гарантируем наличие целевых папок и очистку от старых файлов перед новой распаковкой
            try
            {
                if (Directory.Exists(destinationFolder))
                {
                    // Если папка уже существовала, очищаем её содержимое перед установкой/обновлением,
                    // чтобы исключить дублирование и накопление устаревших файлов
                    try
                    {
                        var di = new DirectoryInfo(destinationFolder);
                        foreach (var file in di.GetFiles())
                        {
                            try { file.Delete(); } catch { /* Игнорируем заблокированные файлы */ }
                        }
                        foreach (var dir in di.GetDirectories())
                        {
                            try { dir.Delete(true); } catch { /* Игнорируем вложенные каталоги с блокировками */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Write(
                            "dependency.precleanup_failed",
                            LogLevel.Warning,
                            LogStatus.PartiallySucceeded,
                            "Предупреждение при предварительной очистке папки установки зависимости",
                            ex,
                            "DependencyManager",
                            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["Key"] = key,
                                ["Stage"] = "precleanup",
                                ["ErrorCode"] = "precleanup-failed"
                            });
                    }
                }
                else
                {
                    Directory.CreateDirectory(destinationFolder);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                SetStatus(key, DependencyStatus.Error);
                string errMsg = $"Нет прав доступа для создания папки установки зависимости '{dep.DisplayName}'. " +
                    "Это может быть связано с ограничениями MSIX или прав пользователя. " +
                    "Убедитесь, что приложение запущено от правильного пользователя.";
                _logService.Write(
                    "dependency.access_denied",
                    LogLevel.Error,
                    LogStatus.Failed,
                    $"Ошибка доступа при распаковке/установке '{dep.DisplayName}'",
                    ex,
                    "DependencyManager",
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(key))
                        .With("Group", dep.DisplayName)
                        .With("ErrorCode", "DEPENDENCY_INSTALL_ACCESS_DENIED"));
                InstallFinished?.Invoke(key, false, errMsg);
                return;
            }

            var cancellationToken = _activeDownloads[key].Token;

            if (dep.IsRawExecutable)
            {
                _logService.Write(
                    "dependency.install.binary_copy_started",
                    LogLevel.Debug,
                    LogStatus.Running,
                    "Исполняемый файл зависимости копируется в целевую папку",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("FileName", LogProps.FileName(destinationFolder)));
                string targetPath = Path.Combine(destinationFolder, dep.VerifyBinary);
                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { /* Игнорируем ошибки удаления старого файла */ }
                }
                File.Move(tempArchivePath, targetPath, true);
                _logService.Write(
                    "dependency.install.binary_copied",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Исполняемый файл зависимости скопирован в целевую папку",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("FileName", dep.VerifyBinary));
            }
            else
            {
                // 2. Распаковка архива
                SetStatus(key, DependencyStatus.Extracting);
                _logService.Write(
                    "dependency.install.archive_extract_started",
                    LogLevel.Debug,
                    LogStatus.Running,
                    "Начата распаковка архива зависимости",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("FileName", LogProps.FileName(dep.ArchiveName)));
                await ExtractArchiveAsync(dep, tempArchivePath, destinationFolder, cancellationToken);
                _logService.Write(
                    "dependency.install.archive_extract_completed",
                    LogLevel.Debug,
                    LogStatus.Succeeded,
                    "Архив зависимости распакован",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("FileName", LogProps.FileName(dep.ArchiveName)));
            }

            // Если устанавливаем декодеры eac3to, нужно запустить тихую установку с повышением прав
            if (key.Equals("eac3to_decoders", StringComparison.OrdinalIgnoreCase))
            {
                string setupPath = Path.Combine(destinationFolder, dep.VerifyBinary);
                if (File.Exists(setupPath))
                {
                    _logService.Write(
                        "dependency.eac3to_decoders.setup_started",
                        LogLevel.Debug,
                        LogStatus.Running,
                        "Запущен тихий установщик декодеров eac3to с повышением прав",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                            .With("FileName", LogProps.FileName(setupPath))
                            .With("IsAdmin", true));
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
                            _logService.Write(
                                "dependency.eac3to_decoders.setup_waiting",
                                LogLevel.Debug,
                                LogStatus.Running,
                                "Ожидание завершения установщика декодеров eac3to",
                                source: SourceName,
                                properties: LogProps.Create("Key", LogRedactor.CompactSafeToken(dep.Key)));
                            await process.WaitForExitAsync(cancellationToken);
                            _logService.Write(
                                "dependency.eac3to_decoders.setup_completed",
                                LogLevel.Debug,
                                LogStatus.Succeeded,
                                "Установщик декодеров eac3to завершил работу",
                                source: SourceName,
                                properties: LogProps
                                    .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                                    .With("ExitCode", process.ExitCode));
                        }
                        else
                        {
                            throw new InvalidOperationException("Не удалось инициализировать процесс установщика декодеров.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.Write(
                            "dependency.eac3to_installer_failed",
                            LogLevel.Error,
                            LogStatus.Failed,
                            "Ошибка при выполнении тихого установщика декодеров eac3to",
                            ex,
                            "DependencyManager",
                            properties: LogProps
                                .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                                .With("Tool", "eac3to_decoders")
                                .With("ErrorCode", "EAC3TO_SETUP_FAILED"));
                        throw new InvalidOperationException("Не удалось выполнить тихий установщик декодеров eac3to.", ex);
                    }
                }
                else
                {
                    string missingSetupErr = $"Файл установщика декодеров '{setupPath}' не найден после распаковки архива.";
                    _logService.Write(
                        "dependency.eac3to_decoders.setup_missing",
                        LogLevel.Error,
                        LogStatus.Failed,
                        "Файл установщика декодеров eac3to не найден",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                            .With("FileName", LogProps.FileName(setupPath))
                            .With("ErrorCode", "EAC3TO_SETUP_MISSING"));
                    throw new FileNotFoundException(missingSetupErr);
                }
            }

            // 3. Верификация установки
            RefreshAllStatuses();
            if (IsInstalled(key))
            {
                lock (_cachedVersions)
                {
                    _cachedVersions.Remove(key);
                    if (key.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedVersions.Remove("qaac");
                    }
                }
                lock (_updatesAvailable)
                {
                    _updatesAvailable[key] = false;
                }
                SetStatus(key, DependencyStatus.Installed);
                _logService.Write(
                    "dependency.install.verified",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Зависимость '{dep.DisplayName}' установлена и проверена",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("Group", dep.DisplayName)
                        .With("Verified", true));
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
                _logService.Write(
                    "dependency.install.verify_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    $"Зависимость '{dep.DisplayName}' не прошла проверку после установки",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("Group", dep.DisplayName)
                        .With("Verified", false)
                        .With("ErrorCode", "DEPENDENCY_VERIFY_FAILED"));
                InstallFinished?.Invoke(key, false, err);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(key, DependencyStatus.NotInstalled);
            _logService.Write(
                "dependency.install_cancelled",
                LogLevel.Info,
                LogStatus.Cancelled,
                $"Установка зависимости '{dep.DisplayName}' отменена по запросу пользователя",
                null,
                "DependencyManager",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = key,
                    ["ErrorCode"] = "cancelled"
                });
            InstallFinished?.Invoke(key, false, "Установка отменена пользователем.");
        }
        catch (Exception ex)
        {
            SetStatus(key, DependencyStatus.Error);
            _logService.Write(
                "dependency.install_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Критический сбой в процессе загрузки, верификации или распаковки зависимости '{dep.DisplayName}' (ключ: {dep.Key})",
                ex,
                "DependencyManager",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = key,
                    ["Stage"] = "install",
                    ["ErrorCode"] = "install-failed",
                    ["Retryable"] = true
                });
            InstallFinished?.Invoke(key, false, "Не удалось установить зависимость. Подробности в журнале приложения.");
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

            lock (_downloadProgress)
            {
                _downloadProgress.Remove(key);
            }

            lock (_downloadSpeed)
            {
                _downloadSpeed.Remove(key);
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

        _logService.Write(
            "dependency.archive.extract_started",
            LogLevel.Debug,
            LogStatus.Running,
            "Начата асинхронная распаковка архива зависимости",
            source: SourceName,
            properties: LogProps
                .Create("FileName", LogProps.FileName(archivePath))
                .With("WorkingDirLabel", LogProps.FileName(destinationDir)));

        await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isZip = archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            int extractedEntries = 0;

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

                    extractedEntries++;
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

            _logService.Write(
                "dependency.archive.extract_completed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Архив зависимости распакован, извлечено элементов: {extractedEntries}",
                source: SourceName,
                properties: LogProps
                    .Create("FileName", LogProps.FileName(archivePath))
                    .With("Count", extractedEntries));
        }, cancellationToken);
    }

    /// <summary>
    /// Физически удаляет папку зависимости с диска и сбрасывает статус.
    /// </summary>
    public async Task<bool> RemoveDependencyAsync(string key)
    {
        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null)
        {
            _logService.Write(
                "dependency.remove.unknown_key",
                LogLevel.Error,
                LogStatus.Failed,
                $"Запрошено удаление неизвестной зависимости '{LogRedactor.CompactSafeToken(key)}'",
                source: SourceName,
                properties: LogProps
                    .Create("Key", LogRedactor.CompactSafeToken(key))
                    .With("ErrorCode", "DEPENDENCY_NOT_IN_MANIFEST"));
            return false;
        }

        _logService.Write(
            "dependency.remove.started",
            LogLevel.Info,
            LogStatus.Running,
            $"Начато удаление зависимости '{dep.DisplayName}'",
            source: SourceName,
            properties: LogProps
                .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                .With("Group", dep.DisplayName));

        if (key.Equals("eac3to_decoders", StringComparison.OrdinalIgnoreCase))
        {
            _logService.Write(
                "dependency.eac3to_decoders.uninstall_started",
                LogLevel.Info,
                LogStatus.Running,
                "Удаление декодеров eac3to: используется оригинальный деинсталлятор из реестра",
                source: SourceName,
                properties: LogProps.Create("Key", LogRedactor.CompactSafeToken(dep.Key)));
            bool uninstalledViaSetup = false;
            try
            {
                string? uninstallStr = GetEac3toDecodersUninstallString();
                if (!string.IsNullOrEmpty(uninstallStr))
                {
                    string exePath = uninstallStr.Trim().Trim('"');
                    if (File.Exists(exePath))
                    {
                        _logService.Write(
                            "dependency.eac3to_decoders.uninstaller_started",
                            LogLevel.Debug,
                            LogStatus.Running,
                            "Запущен оригинальный деинсталлятор декодеров eac3to в тихом режиме",
                            source: SourceName,
                            properties: LogProps
                                .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                                .With("FileName", LogProps.FileName(exePath))
                                .With("IsAdmin", true));
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
                            await process.WaitForExitAsync();
                            _logService.Write(
                                "dependency.eac3to_decoders.uninstaller_completed",
                                LogLevel.Debug,
                                LogStatus.Succeeded,
                                "Деинсталлятор декодеров eac3to завершил работу",
                                source: SourceName,
                                properties: LogProps
                                    .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                                    .With("ExitCode", process.ExitCode));
                            uninstalledViaSetup = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "dependency.eac3to_uninstall_failed",
                    LogLevel.Error,
                    LogStatus.Failed,
                    "Ошибка при вызове официального деинсталлятора eac3to",
                    ex,
                    "DependencyManager",
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("Tool", "eac3to_decoders")
                        .With("ErrorCode", "EAC3TO_UNINSTALLER_FAILED"));
            }

            if (!uninstalledViaSetup)
            {
                _logService.Write(
                    "dependency.eac3to_decoders.uninstaller_fallback",
                    LogLevel.Warning,
                    LogStatus.RetryScheduled,
                    "Официальный деинсталлятор декодеров eac3to недоступен, применяется резервная ручная деинсталляция",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                        .With("ErrorCode", "EAC3TO_UNINSTALLER_UNAVAILABLE")
                        .With("Retryable", true));
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

                    _logService.Write(
                        "dependency.eac3to_decoders.manual_uninstall_started",
                        LogLevel.Debug,
                        LogStatus.Running,
                        "Запущен резервный сценарий удаления декодеров eac3to с повышением прав",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                            .With("FileName", LogProps.FileName(tempBatPath))
                            .With("IsAdmin", true));
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
                        await process.WaitForExitAsync();
                        _logService.Write(
                            "dependency.eac3to_decoders.manual_uninstall_completed",
                            LogLevel.Debug,
                            LogStatus.Succeeded,
                            "Резервное удаление декодеров eac3to завершено",
                            source: SourceName,
                            properties: LogProps
                                .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                                .With("ExitCode", process.ExitCode));
                    }
                    else
                    {
                        throw new InvalidOperationException("Не удалось запустить процесс удаления.");
                    }
                }
                catch (Exception ex)
                {
                    _logService.Write(
                        "dependency.nero_fallback_removal_failed",
                        LogLevel.Error,
                        LogStatus.Failed,
                        "Ошибка при резервном удалении декодеров Nero",
                        ex,
                        "DependencyManager",
                        properties: LogProps
                            .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                            .With("Tool", "eac3to_decoders")
                            .With("ErrorCode", "NERO_FALLBACK_REMOVAL_FAILED"));
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
        lock (_cachedVersions)
        {
            _cachedVersions.Remove(key);
            if (key.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                _cachedVersions.Remove("qaac");
            }
        }
        lock (_updatesAvailable)
        {
            _updatesAvailable[key] = false;
        }

        if (!Directory.Exists(folderPath))
        {
            _logService.Write(
                "dependency.folder_missing",
                LogLevel.Warning,
                LogStatus.Skipped,
                $"Папка зависимости '{dep.DisplayName}' не обнаружена на диске, статус сброшен в NotInstalled",
                null,
                "DependencyManager",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = key,
                    ["ErrorCode"] = "folder-missing"
                });
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }

        try
        {
            Directory.Delete(folderPath, true);
            _logService.Write(
                "dependency.remove.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Папка зависимости '{dep.DisplayName}' удалена с диска",
                source: SourceName,
                properties: LogProps
                    .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                    .With("Group", dep.DisplayName)
                    .With("Verified", true));
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.folder_removal_failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Не удалось удалить папку зависимости '{dep.DisplayName}'",
                ex,
                "DependencyManager",
                properties: LogProps
                    .Create("Key", LogRedactor.CompactSafeToken(dep.Key))
                    .With("Group", dep.DisplayName)
                    .With("ErrorCode", "DEPENDENCY_FOLDER_REMOVAL_FAILED"));
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
                    _logService.Write(
                        "dependency.update_check.throttled",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Проверка обновлений всех зависимостей выполнялась менее 24 часов назад, проверка пропущена",
                        source: SourceName,
                        properties: LogProps
                            .Create("ElapsedMs", 24.0 * 60.0 * 60.0 * 1000.0)
                            .With("Group", "AllDependencies"));
                    return;
                }
            }

            _logService.Write(
                "dependency.update_check.started",
                LogLevel.Debug,
                LogStatus.Running,
                "Запущена фоновая проверка обновлений всех зависимостей",
                source: SourceName,
                properties: LogProps.Create("Group", "AllDependencies"));

            // 1. Проверяем обновления yt-dlp
            await CheckAndUpdateYtDlpAsync(force: true);

            // 1.1. Проверяем обновления Whisper
            await CheckAndUpdateWhisperAsync(force: true);

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
                        var remoteVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in verDoc.RootElement.EnumerateObject())
                        {
                            string key = prop.Name;
                            string remoteVersion = string.Empty;

                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                remoteVersion = prop.Value.GetString() ?? string.Empty;
                            }
                            else if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in prop.Value.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.String)
                                    {
                                        remoteVersion = item.GetString() ?? string.Empty;
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(remoteVersion))
                            {
                                remoteVersions[key] = remoteVersion;
                            }
                        }

                        // Проверяем FFmpeg (+ QAAC): обновление доступно, если отличается версия FFmpeg или версия QAAC
                        if (IsInstalled("ffmpeg"))
                        {
                            bool ffmpegUpdate = false;
                            string localFfmpegVer = (await GetInstalledVersionAsync("ffmpeg")).TrimStart('v', 'V').Trim();
                            if (remoteVersions.TryGetValue("ffmpeg", out var remoteFfmpeg) &&
                                !string.IsNullOrEmpty(localFfmpegVer))
                            {
                                string cleanRemoteFfmpeg = remoteFfmpeg.TrimStart('v', 'V').Trim();
                                if (!cleanRemoteFfmpeg.Equals(localFfmpegVer, StringComparison.OrdinalIgnoreCase))
                                {
                                    ffmpegUpdate = true;
                                    _logService.Write(
                                    "dependency.update.available",
                                    LogLevel.Info,
                                    LogStatus.Changed,
                                    $"Обнаружена новая версия FFmpeg: удалённая {LogRedactor.CompactSafeToken(remoteFfmpeg)}, локальная {LogRedactor.CompactSafeToken(localFfmpegVer)}",
                                    source: SourceName,
                                    properties: LogProps
                                        .Create("Key", "ffmpeg")
                                        .With("Version", LogRedactor.CompactSafeToken(remoteFfmpeg)));
                                }
                            }

                            string localQaacVer = (await GetInstalledVersionAsync("qaac")).TrimStart('v', 'V').Trim();
                            if (remoteVersions.TryGetValue("qaac", out var remoteQaac) &&
                                !string.IsNullOrEmpty(localQaacVer))
                            {
                                string cleanRemoteQaac = remoteQaac.TrimStart('v', 'V').Trim();
                                if (!cleanRemoteQaac.Equals(localQaacVer, StringComparison.OrdinalIgnoreCase))
                                {
                                    ffmpegUpdate = true;
                                    _logService.Write(
                                        "dependency.update.available",
                                        LogLevel.Info,
                                        LogStatus.Changed,
                                        $"Обнаружена новая версия QAAC: удалённая {LogRedactor.CompactSafeToken(remoteQaac)}, локальная {LogRedactor.CompactSafeToken(localQaacVer)}",
                                        source: SourceName,
                                        properties: LogProps
                                            .Create("Key", "qaac")
                                            .With("Version", LogRedactor.CompactSafeToken(remoteQaac)));
                                }
                            }

                            lock (_updatesAvailable)
                            {
                                _updatesAvailable["ffmpeg"] = ffmpegUpdate;
                            }
                            StatusChanged?.Invoke("ffmpeg", GetStatus("ffmpeg"));
                        }

                        // Проверяем остальные зависимости из манифеста
                        foreach (var kvp in remoteVersions)
                        {
                            string depKey = kvp.Key;
                            if (depKey.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                                depKey.Equals("qaac", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (!IsInstalled(depKey))
                            {
                                continue;
                            }

                            string localVer = (await GetInstalledVersionAsync(depKey)).TrimStart('v', 'V').Trim();
                            string remoteVer = kvp.Value.TrimStart('v', 'V').Trim();
                            bool hasUpdate = !string.IsNullOrEmpty(localVer) &&
                                !remoteVer.Equals(localVer, StringComparison.OrdinalIgnoreCase);

                            lock (_updatesAvailable)
                            {
                                _updatesAvailable[depKey] = hasUpdate;
                            }

                            if (hasUpdate)
                            {
                                _logService.Write(
                                "dependency.update.available",
                                LogLevel.Info,
                                LogStatus.Changed,
                                $"Для зависимости '{depKey}' доступна новая версия: удалённая {LogRedactor.CompactSafeToken(kvp.Value)}, локальная {LogRedactor.CompactSafeToken(localVer)}",
                                source: SourceName,
                                properties: LogProps
                                    .Create("Key", LogRedactor.CompactSafeToken(depKey))
                                    .With("Version", LogRedactor.CompactSafeToken(kvp.Value)));
                            }
                            StatusChanged?.Invoke(depKey, GetStatus(depKey));
                        }
                    }
                }
            }

            _settingsManager.SetSetting("Updates", "LastDepsCheckTime", DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.update_check_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Ошибка при фоновой проверке обновлений зависимостей",
                ex,
                "DependencyManager",
                properties: LogProps
                    .Create("Stage", "dependency_update_check")
                    .With("ErrorCode", "DEPENDENCY_UPDATE_CHECK_FAILED")
                    .With("Retryable", true));
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
            _logService.Write(
                "dependency.update_check.skipped",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Проверка обновлений yt-dlp пропущена: утилита не установлена",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "yt-dlp")
                    .With("Reason", "NotInstalled"));
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
                    _logService.Write(
                        "dependency.update_check.throttled",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Проверка обновлений yt-dlp выполнялась менее 24 часов назад, проверка пропущена",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", "yt-dlp")
                            .With("ElapsedMs", 24.0 * 60.0 * 60.0 * 1000.0));
                    return;
                }
            }

            _logService.Write(
                "dependency.update_check.started",
                LogLevel.Debug,
                LogStatus.Running,
                "Запущена фоновая проверка обновлений yt-dlp",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "yt-dlp")
                    .With("HostLabel", "api.github.com"));

            // Выполняем GET-запрос к GitHub API для получения последнего релиза
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest");
            // GitHub API требует User-Agent
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logService.Write(
                    "dependency.update_check.http_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Данные о релизе yt-dlp не получены",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "yt-dlp")
                        .With("StatusCode", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .With("HostLabel", "api.github.com")
                        .With("ErrorCode", "UPDATE_CHECK_HTTP_FAILED"));
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                _logService.Write(
                    "dependency.update_check.malformed_response",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "В ответе сервиса релизов yt-dlp отсутствует идентификатор версии",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "yt-dlp")
                        .With("ErrorCode", "UPDATE_CHECK_MALFORMED"));
                return;
            }

            string latestTag = tagProp.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(latestTag))
            {
                _logService.Write(
                    "dependency.update_check.empty_tag",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Идентификатор последней версии yt-dlp пуст",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "yt-dlp")
                        .With("ErrorCode", "UPDATE_CHECK_EMPTY_TAG"));
                return;
            }

            // Запоминаем время текущей проверки
            _settingsManager.SetSetting("Updates", "LastYtDlpCheckTime", DateTime.UtcNow.ToString("o"));

            string localVersion = _settingsManager.GetSetting("Updates", "YtDlpInstalledVersion", string.Empty);
            _logService.Write(
                "dependency.update_check.completed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Проверка обновлений yt-dlp завершена: доступна {LogRedactor.CompactSafeToken(latestTag)}, установлена {LogRedactor.CompactSafeToken(localVersion)}",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "yt-dlp")
                    .With("Version", LogRedactor.CompactSafeToken(latestTag)));

            if (latestTag.Equals(localVersion, StringComparison.OrdinalIgnoreCase))
            {
                lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = false; }
                _logService.Write(
                    "dependency.update.up_to_date",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Установлена актуальная версия yt-dlp, обновление не требуется",
                    source: SourceName,
                    properties: LogProps.Create("Key", "yt-dlp"));
                return;
            }

            // Если версии не совпадают, фиксируем наличие обновления и запускаем установку/обновление
            lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = true; }
            _logService.Write(
                "dependency.update.install_started",
                LogLevel.Info,
                LogStatus.Running,
                $"Запущено автоматическое обновление yt-dlp до версии {LogRedactor.CompactSafeToken(latestTag)}",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "yt-dlp")
                    .With("Version", LogRedactor.CompactSafeToken(latestTag)));

            // Запускаем асинхронную установку
            await InstallDependencyAsync("yt-dlp");

            // Если установка завершилась успехом, сохраняем новую версию в настройки
            if (IsInstalled("yt-dlp"))
            {
                lock (_updatesAvailable) { _updatesAvailable["yt-dlp"] = false; }
                _settingsManager.SetSetting("Updates", "YtDlpInstalledVersion", latestTag);
                _logService.Write(
                    "dependency.update.completed",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"yt-dlp обновлён до версии {LogRedactor.CompactSafeToken(latestTag)}",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "yt-dlp")
                        .With("Version", LogRedactor.CompactSafeToken(latestTag))
                        .With("Verified", true));
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.ytdlp_update_check_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Исключение при проверке обновлений yt-dlp",
                ex,
                "DependencyManager",
                properties: LogProps
                    .Create("Key", "yt-dlp")
                    .With("HostLabel", "api.github.com")
                    .With("ErrorCode", "YT_DLP_UPDATE_CHECK_FAILED"));
        }
    }

    /// <summary>
    /// Выполняет фоновую проверку обновлений whisper.cpp и обновляет установленные рантаймы при обнаружении новой версии на GitHub.
    /// </summary>
    /// <param name="force">Принудительно запустить проверку без учёта 24-часового интервала.</param>
    public async Task CheckAndUpdateWhisperAsync(bool force = false)
    {
        // Проверяем, установлен ли хотя бы один рантайм Whisper
        bool anyWhisperInstalled = IsInstalled("whisper_cpu") || IsInstalled("whisper_cuda");
        if (!anyWhisperInstalled)
        {
            _logService.Write(
                "dependency.update_check.skipped",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Проверка обновлений Whisper пропущена: ни один рантайм не установлен",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("Reason", "NotInstalled"));
            return;
        }

        try
        {
            string lastCheckStr = _settingsManager.GetSetting("Updates", "LastWhisperCheckTime", string.Empty);
            if (!force && DateTime.TryParse(lastCheckStr, out DateTime lastCheckTime))
            {
                if (DateTime.UtcNow - lastCheckTime < TimeSpan.FromDays(1))
                {
                    _logService.Write(
                        "dependency.update_check.throttled",
                        LogLevel.Debug,
                        LogStatus.Skipped,
                        "Проверка обновлений Whisper выполнялась менее 24 часов назад, проверка пропущена",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", "whisper")
                            .With("ElapsedMs", 24.0 * 60.0 * 60.0 * 1000.0));
                    return;
                }
            }

            _logService.Write(
                "dependency.update_check.started",
                LogLevel.Debug,
                LogStatus.Running,
                "Запущена проверка обновлений Whisper",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("HostLabel", "api.github.com"));

            // Ищем последний релиз в ggml-org/whisper.cpp, содержащий бинарные сборки
            var (latestTag, _) = await FindLatestWhisperReleaseWithAssetsAsync();
            if (string.IsNullOrEmpty(latestTag))
            {
                _logService.Write(
                    "dependency.update_check.no_assets",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Релиз Whisper с доступными бинарными сборками не определён",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "whisper")
                        .With("ErrorCode", "UPDATE_CHECK_NO_ASSETS"));
                return;
            }

            _settingsManager.SetSetting("Updates", "LastWhisperCheckTime", DateTime.UtcNow.ToString("o"));
            string localVersion = _settingsManager.GetSetting("Updates", "WhisperInstalledVersion", string.Empty);
            _logService.Write(
                "dependency.update_check.completed",
                LogLevel.Debug,
                LogStatus.Succeeded,
                $"Проверка обновлений Whisper завершена: доступна {LogRedactor.CompactSafeToken(latestTag)}, установлена {LogRedactor.CompactSafeToken(localVersion)}",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("Version", LogRedactor.CompactSafeToken(latestTag)));

            if (latestTag.Equals(localVersion, StringComparison.OrdinalIgnoreCase))
            {
                lock (_updatesAvailable)
                {
                    _updatesAvailable["whisper_cpu"] = false;
                    _updatesAvailable["whisper_cuda"] = false;
                }
                _logService.Write(
                    "dependency.update.up_to_date",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Установлена актуальная версия Whisper, обновление не требуется",
                    source: SourceName,
                    properties: LogProps.Create("Key", "whisper"));
                return;
            }

            // Обновляем те рантаймы, которые установлены
            string[] whisperKeys = new[] { "whisper_cpu", "whisper_cuda" };
            foreach (var wKey in whisperKeys)
            {
                if (IsInstalled(wKey))
                {
                    lock (_updatesAvailable) { _updatesAvailable[wKey] = true; }
                    _logService.Write(
                        "dependency.update.install_started",
                        LogLevel.Info,
                        LogStatus.Running,
                        $"Запущено обновление рантайма Whisper '{wKey}' до версии {LogRedactor.CompactSafeToken(latestTag)}",
                        source: SourceName,
                        properties: LogProps
                            .Create("Key", LogRedactor.CompactSafeToken(wKey))
                            .With("Version", LogRedactor.CompactSafeToken(latestTag)));
                    await InstallDependencyAsync(wKey);
                    lock (_updatesAvailable) { _updatesAvailable[wKey] = false; }
                }
            }

            _settingsManager.SetSetting("Updates", "WhisperInstalledVersion", latestTag);
            _logService.Write(
                "dependency.update.completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Рантаймы Whisper обновлены до версии {LogRedactor.CompactSafeToken(latestTag)}",
                source: SourceName,
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("Version", LogRedactor.CompactSafeToken(latestTag))
                    .With("Verified", true));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.whisper_update_check_failed",
                LogLevel.Error,
                LogStatus.Failed,
                "Исключение при проверке обновлений Whisper",
                ex,
                "DependencyManager",
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("HostLabel", "api.github.com")
                    .With("ErrorCode", "WHISPER_UPDATE_CHECK_FAILED"));
        }
    }

    /// <summary>
    /// Динамически находит релиз Whisper на GitHub, в котором прикреплены скомпилированные бинарные архивы.
    /// </summary>
    private async Task<(string Tag, JsonElement? AssetsArray)> FindLatestWhisperReleaseWithAssetsAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10");
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logService.Write(
                    "dependency.update_check.http_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Список релизов Whisper не получен",
                    source: SourceName,
                    properties: LogProps
                        .Create("Key", "whisper")
                        .With("StatusCode", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .With("HostLabel", "api.github.com")
                        .With("ErrorCode", "UPDATE_CHECK_HTTP_FAILED"));
                return (string.Empty, null);
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (string.Empty, null);
            }

            foreach (var releaseEl in doc.RootElement.EnumerateArray())
            {
                if (releaseEl.TryGetProperty("assets", out var assetsProp) &&
                    assetsProp.ValueKind == JsonValueKind.Array &&
                    assetsProp.GetArrayLength() > 0)
                {
                    string tag = releaseEl.TryGetProperty("tag_name", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrEmpty(tag))
                    {
                        // Клонируем элемент ассетов для безопасного возврата из using-блока JsonDocument
                        return (tag, assetsProp.Clone());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "dependency.update_check.releases_failed",
                LogLevel.Warning,
                LogStatus.Failed,
                "Список релизов Whisper с бинарными сборками получить не удалось",
                ex,
                SourceName,
                properties: LogProps
                    .Create("Key", "whisper")
                    .With("HostLabel", "api.github.com")
                    .With("ErrorCode", "WHISPER_RELEASE_LOOKUP_FAILED"));
        }

        return (string.Empty, null);
    }

    /// <summary>
    /// Разрешает прямую ссылку на скачивание архива Whisper из самого свежего подходящего релиза.
    /// Если запрос завершается ошибкой или ассет не найден, возвращает исходный fallbackUrl.
    /// </summary>
    private async Task<string> ResolveWhisperDownloadUrlAsync(string key, string archiveName, string fallbackUrl)
    {
        try
        {
            var (tag, assets) = await FindLatestWhisperReleaseWithAssetsAsync();
            if (assets.HasValue && assets.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.Value.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? string.Empty) : string.Empty;
                    if (string.Equals(name, archiveName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            string resolved = urlProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                _logService.Write(
                                    "dependency.archive_url.resolved",
                                    LogLevel.Debug,
                                    LogStatus.Succeeded,
                                    $"Адрес загрузки архива зависимости '{LogRedactor.CompactSafeToken(key)}' определён для релиза {LogRedactor.CompactSafeToken(tag)}",
                                    source: SourceName,
                                    properties: LogProps
                                        .Create("Key", LogRedactor.CompactSafeToken(key))
                                        .With("FileName", LogProps.FileName(archiveName))
                                        .With("Version", LogRedactor.CompactSafeToken(tag))
                                        .With("HostLabel", SafeHostLabel(resolved)));
                                return resolved;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Write("dependency.url_resolve_failed", LogLevel.Warning, LogStatus.Skipped, $"Не удалось динамически разрешить адрес загрузки для '{key}', используется значение по умолчанию", ex, "DependencyManager");
        }

        return fallbackUrl;
    }

    private static string SafeHostLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "unknown";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            return "unknown";
        }

        return LogRedactor.CompactSafeToken(uri.Host);
    }
}
