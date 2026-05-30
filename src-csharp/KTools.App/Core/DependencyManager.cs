// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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

        SetStatus(key, DependencyStatus.Downloading);
        string tempArchivePath = Path.Combine(Path.GetTempPath(), dep.ArchiveName);

        try
        {
            // 1. Асинхронное скачивание архива
            string downloadUrl = $"{DepsBaseUrl}/{dep.ArchiveName}";
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

            // 2. Распаковка архива через системную утилиту tar.exe
            SetStatus(key, DependencyStatus.Extracting);
            string destinationFolder = Path.Combine(_binDir, dep.Subfolder);
            
            // Гарантируем наличие целевых папок
            Directory.CreateDirectory(destinationFolder);

            var cancellationToken = _activeDownloads[key].Token;
            await ExtractArchiveAsync(tempArchivePath, destinationFolder, cancellationToken);

            // 3. Верификация установки
            RefreshAllStatuses();
            if (IsInstalled(key))
            {
                SetStatus(key, DependencyStatus.Installed);
                InstallFinished?.Invoke(key, true, string.Empty);
            }
            else
            {
                SetStatus(key, DependencyStatus.Error);
                InstallFinished?.Invoke(key, false, $"Файл-маркер '{dep.VerifyBinary}' отсутствует на диске после распаковки.");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(key, DependencyStatus.NotInstalled);
            InstallFinished?.Invoke(key, false, "Установка отменена пользователем.");
        }
        catch (Exception ex)
        {
            SetStatus(key, DependencyStatus.Error);
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
    /// Асинхронно распаковывает архив .tar.xz в целевую папку, вызывая встроенный системный tar.exe операционной системы Windows.
    /// </summary>
    private static async Task ExtractArchiveAsync(string archivePath, string destinationDir, CancellationToken cancellationToken)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "tar",
            // Флаг -x распаковывает, -f указывает архив, -C указывает целевую директорию
            Arguments = $"-xf \"{archivePath}\" -C \"{destinationDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(processInfo);
        if (process == null)
        {
            throw new Exception("Не удалось запустить встроенный системный декомпрессор 'tar.exe'.");
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            string errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new Exception($"tar.exe завершился с кодом ошибки {process.ExitCode}. Подробности: {errorOutput}");
        }
    }

    /// <summary>
    /// Физически удаляет папку зависимости с диска и сбрасывает статус.
    /// </summary>
    public bool RemoveDependency(string key)
    {
        var dep = _registry.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (dep == null)
        {
            return false;
        }

        string folderPath = Path.Combine(_binDir, dep.Subfolder);
        if (!Directory.Exists(folderPath))
        {
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }

        try
        {
            Directory.Delete(folderPath, true);
            SetStatus(key, DependencyStatus.NotInstalled);
            return true;
        }
        catch (Exception)
        {
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
