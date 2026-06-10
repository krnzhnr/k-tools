// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
public class DependencyManager
{
    private static readonly Lazy<DependencyManager> LazyInstance = 
        new(() => new DependencyManager());

    /// <summary>
    /// Глобальная точка доступа к единственному экземпляру класса DependencyManager.
    /// </summary>
    public static DependencyManager Instance => LazyInstance.Value;

    private const string DepsReleaseTag = "deps-v1";
    private const string DepsBaseUrl = $"https://github.com/krnzhnr/k-tools/releases/download/{DepsReleaseTag}";

    private readonly string _binDir;
    private readonly Dictionary<string, DependencyStatus> _statuses = new();
    private readonly List<DependencyInfo> _registry = new();
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

    private DependencyManager()
    {
        // Используем метод интеллектуального поиска директории bin
        _binDir = PathManager.GetBinDirectory();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("K-Tools-DependencyManager-WinUI3");

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
            SizeMb = 122.0,
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
            SizeMb = 5.68,
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
            SizeMb = 3.89,
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
            SizeMb = 48.1,
            ArchiveName = "dee.tar.xz",
            VerifyBinary = "dee.exe",
            IsRequired = false
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
        // 1. Проверяем локальный путь релиза
        string localPath = Path.Combine(_binDir, dep.Subfolder, dep.VerifyBinary);
        if (File.Exists(localPath))
        {
            return true;
        }

        // 2. Проверяем режим разработки (через PathManager)
        string resolvedPath = PathManager.GetBinaryPath(dep.VerifyBinary);
        if (File.Exists(resolvedPath) && !resolvedPath.Equals(dep.VerifyBinary, StringComparison.OrdinalIgnoreCase))
        {
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
            LogService.Instance.Error($"Запрос на установку неизвестной зависимости '{key}'", "DependencyManager");
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

        LogService.Instance.Info($"Запущена процедура установки зависимости '{dep.DisplayName}' ({dep.Key})", "DependencyManager");
        SetStatus(key, DependencyStatus.Downloading);
        string tempArchivePath = Path.Combine(Path.GetTempPath(), dep.ArchiveName);

        try
        {
            // 1. Асинхронное скачивание архива
            string downloadUrl = $"{DepsBaseUrl}/{dep.ArchiveName}";
            LogService.Instance.Info($"Начало скачивания архива: {downloadUrl} в {tempArchivePath}", "DependencyManager");
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

            LogService.Instance.Info($"Архив '{dep.ArchiveName}' успешно скачан на диск", "DependencyManager");

            // 2. Распаковка архива через системную утилиту tar.exe
            SetStatus(key, DependencyStatus.Extracting);
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
                LogService.Instance.Error($"Ошибка доступа при распаковке '{dep.DisplayName}': {errMsg}", "DependencyManager");
                InstallFinished?.Invoke(key, false, errMsg);
                return;
            }

            LogService.Instance.Info($"Начало распаковки архива в папку '{destinationFolder}'", "DependencyManager");
            var cancellationToken = _activeDownloads[key].Token;
            await ExtractArchiveAsync(tempArchivePath, destinationFolder, cancellationToken);
            LogService.Instance.Info($"Распаковка архива '{dep.ArchiveName}' успешно завершена", "DependencyManager");

            // 3. Верификация установки
            RefreshAllStatuses();
            if (IsInstalled(key))
            {
                SetStatus(key, DependencyStatus.Installed);
                LogService.Instance.Info($"Зависимость '{dep.DisplayName}' успешно установлена и верифицирована", "DependencyManager");
                InstallFinished?.Invoke(key, true, string.Empty);
            }
            else
            {
                SetStatus(key, DependencyStatus.Error);
                string err = $"Файл-маркер '{dep.VerifyBinary}' отсутствует на диске после распаковки.";
                LogService.Instance.Error($"Ошибка верификации '{dep.DisplayName}': {err}", "DependencyManager");
                InstallFinished?.Invoke(key, false, err);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(key, DependencyStatus.NotInstalled);
            LogService.Instance.Warn($"Установка зависимости '{dep.DisplayName}' отменена пользователем", "DependencyManager");
            InstallFinished?.Invoke(key, false, "Установка отменена пользователем.");
        }
        catch (Exception ex)
        {
            SetStatus(key, DependencyStatus.Error);
            string detailedErrorMessage = $"Критический сбой в процессе загрузки, верификации или распаковки зависимости '{dep.DisplayName}' (ключ: {dep.Key}). Подробности возникшего исключения: {ex.Message}. Стек вызовов: {ex.StackTrace}";
            LogService.Instance.Error(detailedErrorMessage, "DependencyManager");
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
    /// Асинхронно распаковывает архив .tar.xz в целевую папку, используя стороннюю библиотеку SharpCompress.
    /// Выполняется в фоновом режиме на пуле потоков без блокировки основного UI-потока.
    /// </summary>
    /// <param name="archivePath">Абсолютный путь к исходному tar.xz-архиву на диске.</param>
    /// <param name="destinationDir">Абсолютный путь к целевой директории распаковки.</param>
    /// <param name="cancellationToken">Токен отмены для прерывания процесса распаковки по требованию пользователя.</param>
    /// <exception cref="ArgumentNullException">Инициируется, если один из входных путей равен null.</exception>
    /// <exception cref="OperationCanceledException">Инициируется, если процесс был отменен через token.</exception>
    private static async Task ExtractArchiveAsync(string archivePath, string destinationDir, CancellationToken cancellationToken)
    {
        if (archivePath == null)
        {
            throw new ArgumentNullException(nameof(archivePath), "Путь к архиву не может быть пустым (null).");
        }

        if (destinationDir == null)
        {
            throw new ArgumentNullException(nameof(destinationDir), "Путь к целевой папке не может быть пустым (null).");
        }

        LogService.Instance.Info($"Запуск асинхронной распаковки tar.xz архива '{archivePath}' в папку '{destinationDir}'...", "DependencyManager");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var fileStream = File.OpenRead(archivePath);
            using var xzStream = new XZStream(fileStream);
            using var reader = ReaderFactory.OpenReader(xzStream);

            var options = new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            };

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!reader.Entry.IsDirectory)
                {
                    LogService.Instance.Info($"Распаковка файла из архива: {reader.Entry.Key}", "DependencyManager");
                    reader.WriteEntryToDirectory(destinationDir, options);
                }
            }

            LogService.Instance.Info($"Распаковка архива '{archivePath}' успешно завершена через SharpCompress", "DependencyManager");
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
            LogService.Instance.Error($"Попытка удаления неизвестной зависимости '{key}'", "DependencyManager");
            return false;
        }

        LogService.Instance.Info($"Запрос на удаление зависимости '{dep.DisplayName}'", "DependencyManager");
        string folderPath = Path.Combine(_binDir, dep.Subfolder);
        if (!Directory.Exists(folderPath))
        {
            LogService.Instance.Warn($"Папка зависимости '{dep.DisplayName}' не обнаружена на диске. Сброс статуса в NotInstalled", "DependencyManager");
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }

        try
        {
            Directory.Delete(folderPath, true);
            LogService.Instance.Info($"Папка зависимости '{dep.DisplayName}' успешно удалена с диска", "DependencyManager");
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Не удалось удалить папку зависимости '{dep.DisplayName}': {ex.Message}", "DependencyManager");
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
}
