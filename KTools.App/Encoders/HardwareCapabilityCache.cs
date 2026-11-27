// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Win32;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Encoders;

/// <summary>
/// Реализация кэша аппаратных возможностей устройства.
/// Опрашивает FFmpeg при запуске приложения и кэширует результаты
/// для синхронного и быстрого доступа из конфигураторов энкодеров.
///
/// Дисковый кэш (PERF-05): файл nvenc-capabilities.json рядом с настройками
/// (каталог IPathManager.GetSettingsDirectory(), НЕ settings.json) вида
/// {nvenc, temporalAq, av1, ffmpegVersion, driverVersion, deviceFingerprint, timestamp}.
/// Запись действительна, только пока совпадают версия FFmpeg, версия
/// драйвера NVIDIA и отпечаток видеооборудования (имена адаптеров и PCI-ID
/// из реестра): смена любой из них, включая замену видеокарты на том же
/// драйвере, делает кэш недействительным и запускает повторный опрос.
/// Кэш старого формата без отпечатка также недействителен. Проверки версий
/// и отпечатка дешёвые и не запускают процессы: версия FFmpeg читается
/// из метаданных бинарника, версия драйвера и состав оборудования — из
/// реестра. Метка времени хранится справочно и на действительность кэша
/// не влияет.
/// </summary>
public class HardwareCapabilityCache : IHardwareCapabilityCache
{
    private const string SourceName = nameof(HardwareCapabilityCache);

    /// <summary>
    /// Имя файла дискового кэша в каталоге настроек (не settings.json).
    /// </summary>
    private const string CacheFileName = "nvenc-capabilities.json";

    /// <summary>
    /// Подстрока описания видеоадаптера в реестре, по которой опознаётся NVIDIA.
    /// </summary>
    private const string NvidiaAdapterMarker = "NVIDIA";

    /// <summary>
    /// Значение отпечатка драйвера, когда адаптеров NVIDIA в реестре нет.
    /// </summary>
    private const string NoNvidiaDriverMarker = "no-nvidia";

    private const string CacheMissNoFile = "no-file";
    private const string CacheMissVersionChanged = "version-changed";
    private const string CacheMissHardwareChanged = "hardware-changed";
    private const string CacheMissCorrupted = "corrupted";

    /// <summary>
    /// Имя значения реестра с описанием видеоадаптера (например, "NVIDIA GeForce RTX 4070").
    /// </summary>
    private const string RegistryDriverDescValue = "DriverDesc";

    /// <summary>
    /// Имя значения реестра с PCI-идентификатором устройства (например, "pci\ven_10de&dev_2684...").
    /// </summary>
    private const string RegistryMatchingIdValue = "MatchingDeviceId";

    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly ILogService _logService;
    private readonly IPathManager? _pathManager;
    private readonly Func<string>? _ffmpegVersionProvider;
    private readonly Func<string>? _driverVersionProvider;
    private readonly Func<string>? _deviceFingerprintProvider;
    private readonly string? _cacheFilePathOverride;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized = false;

    public HardwareCapabilityCache(IFFmpegRunner ffmpegRunner, ILogService logService)
        : this(ffmpegRunner, logService, pathManager: null)
    {
    }

    public HardwareCapabilityCache(IFFmpegRunner ffmpegRunner, ILogService logService, IPathManager? pathManager)
        : this(ffmpegRunner, logService, pathManager, ffmpegVersionProvider: null, driverVersionProvider: null, cacheFilePath: null)
    {
    }

    /// <summary>
    /// Конструктор с подменяемыми источниками версий и пути кэша для тестов:
    /// позволяет проверить попадание в кэш и инвалидацию без обращения к реестру и диску настроек.
    /// </summary>
    public HardwareCapabilityCache(
        IFFmpegRunner ffmpegRunner,
        ILogService logService,
        IPathManager? pathManager,
        Func<string>? ffmpegVersionProvider,
        Func<string>? driverVersionProvider,
        string? cacheFilePath,
        Func<string>? deviceFingerprintProvider = null)
    {
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager;
        _ffmpegVersionProvider = ffmpegVersionProvider;
        _driverVersionProvider = driverVersionProvider;
        _deviceFingerprintProvider = deviceFingerprintProvider;
        _cacheFilePathOverride = cacheFilePath;
    }

    public bool IsNvencSupported { get; private set; }
    public bool IsNvencTemporalAqSupported { get; private set; }
    public bool IsNvencAv1Supported { get; private set; }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                _logService.Write(
                    "encoder.capabilities.init_started",
                    LogLevel.Debug,
                    LogStatus.Running,
                    "Начинается опрос аппаратных возможностей кодирования",
                    source: SourceName);
                if (TryLoadValidCache(out bool cachedNvenc, out bool cachedTemporalAq, out bool cachedAv1))
                {
                    IsNvencSupported = cachedNvenc;
                    IsNvencTemporalAqSupported = cachedTemporalAq;
                    IsNvencAv1Supported = cachedAv1;
                    _isInitialized = true;
                    _logService.Write(
                        "encoder.capabilities.cache_hit",
                        LogLevel.Info,
                        LogStatus.Succeeded,
                        $"Аппаратные возможности кодирования взяты из дискового кэша без запуска процессов: NVENC {(IsNvencSupported ? "доступен" : "недоступен")}",
                        source: SourceName,
                        properties: LogProps
                            .Create("Verified", IsNvencSupported)
                            .With("Tool", "ffmpeg")
                            .With("Persisted", true));
                    return;
                }

                IsNvencSupported = await _ffmpegRunner.CheckNvencSupportAsync().ConfigureAwait(false);
                _logService.Write(
                    "encoder.capabilities.detected",
                    LogLevel.Info,
                    LogStatus.Succeeded,
                    $"Аппаратные возможности кодирования определены: NVENC {(IsNvencSupported ? "доступен" : "недоступен")}",
                    source: SourceName,
                    properties: LogProps
                        .Create("Verified", IsNvencSupported)
                        .With("Tool", "ffmpeg")
                        .With("Codec", "h264_nvenc"));

                if (IsNvencSupported)
                {
                    IsNvencTemporalAqSupported = await CheckNvencTemporalAqAsync().ConfigureAwait(false);
                    _logService.Write(
                        "encoder.capabilities.temporal_aq_detected",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        $"NVENC Temporal AQ {(IsNvencTemporalAqSupported ? "доступен" : "недоступен")}",
                        source: SourceName,
                        properties: LogProps
                            .Create("Verified", IsNvencTemporalAqSupported)
                            .With("Codec", "h264_nvenc"));
                    IsNvencAv1Supported = await CheckNvencAv1Async().ConfigureAwait(false);
                    _logService.Write(
                        "encoder.capabilities.av1_detected",
                        LogLevel.Debug,
                        LogStatus.Succeeded,
                        $"NVENC AV1 {(IsNvencAv1Supported ? "доступен" : "недоступен")}",
                        source: SourceName,
                        properties: LogProps
                            .Create("Verified", IsNvencAv1Supported)
                            .With("Codec", "av1_nvenc"));
                }
                else
                {
                    IsNvencTemporalAqSupported = false;
                    IsNvencAv1Supported = false;
                }

                _isInitialized = true;
                SaveCache();
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "encoder.capabilities.init_failed",
                    LogLevel.Warning,
                    LogStatus.Failed,
                    "Аппаратные возможности кодирования не определены, аппаратное ускорение будет недоступно",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("ErrorCode", "ENCODER_CAPABILITIES_INIT_FAILED")
                        .With("Tool", "ffmpeg"));
                IsNvencSupported = false;
                IsNvencTemporalAqSupported = false;
                IsNvencAv1Supported = false;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Сбрасывает кэш, чтобы при следующем вызове InitializeAsync проверки выполнились заново.
    /// Сносит и дисковый кэш.
    /// </summary>
    public void Invalidate()
    {
        _isInitialized = false;
        IsNvencSupported = false;
        IsNvencTemporalAqSupported = false;
        IsNvencAv1Supported = false;
        try
        {
            string cachePath = ResolveCacheFilePath();
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex)
        {
            _logService.Write(
                "encoder.capabilities.cache_invalidate_failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Дисковый кэш возможностей кодирования не удалён, при следующем опросе он будет перезаписан",
                ex,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", "ENCODER_CACHE_INVALIDATE_FAILED")
                    .With("Tool", "ffmpeg"));
            return;
        }

        _logService.Write(
            "encoder.capabilities.cache_invalidated",
            LogLevel.Debug,
            LogStatus.Succeeded,
            "Кэш возможностей кодирования сброшен, следующий опрос оборудования выполнится заново",
            source: SourceName,
            properties: LogProps
                .Create("Tool", "ffmpeg")
                .With("Persisted", false));
    }

    private async Task<bool> CheckNvencTemporalAqAsync()
    {
        try
        {
            return await _ffmpegRunner.CheckNvencTemporalAqSupportAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckNvencAv1Async()
    {
        try
        {
            return await _ffmpegRunner.CheckNvencAv1SupportAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Пытается взять результат из дискового кэша без запуска процессов.
    /// Возвращает true, только если версии FFmpeg и драйвера, а также
    /// отпечаток видеооборудования совпали с записанными. Кэш старого
    /// формата без отпечатка считается недействительным.
    /// Никогда не бросает исключений: любая неполадка означает промах кэша.
    /// </summary>
    private bool TryLoadValidCache(out bool nvenc, out bool temporalAq, out bool av1)
    {
        nvenc = false;
        temporalAq = false;
        av1 = false;
        try
        {
            string cachePath = ResolveCacheFilePath();
            if (!File.Exists(cachePath))
            {
                WriteCacheMissed(CacheMissNoFile);
                return false;
            }

            NvencCapabilitySnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<NvencCapabilitySnapshot>(File.ReadAllText(cachePath));
            }
            catch (Exception ex)
            {
                _logService.Write(
                    "encoder.capabilities.cache_missed",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Дисковый кэш возможностей кодирования повреждён, выполняется повторный опрос оборудования",
                    ex,
                    SourceName,
                    properties: LogProps
                        .Create("Reason", CacheMissCorrupted)
                        .With("Tool", "ffmpeg"));
                return false;
            }

            if (snapshot is null
                || string.IsNullOrWhiteSpace(snapshot.FfmpegVersion)
                || string.IsNullOrWhiteSpace(snapshot.DriverVersion))
            {
                WriteCacheMissed(CacheMissCorrupted);
                return false;
            }

            string currentFfmpeg = ReadFfmpegVersion();
            string currentDriver = ReadDriverVersion();
            string currentDevices = ReadDeviceFingerprint();
            if (!string.Equals(snapshot.FfmpegVersion, currentFfmpeg, StringComparison.Ordinal)
                || !string.Equals(snapshot.DriverVersion, currentDriver, StringComparison.Ordinal))
            {
                _logService.Write(
                    "encoder.capabilities.cache_missed",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Дисковый кэш возможностей кодирования устарел после обновления, выполняется повторный опрос оборудования",
                    source: SourceName,
                    properties: LogProps
                        .Create("Reason", CacheMissVersionChanged)
                        .With("Tool", "ffmpeg")
                        .With("ToolVersion", snapshot.FfmpegVersion));
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.DeviceFingerprint)
                || !string.Equals(snapshot.DeviceFingerprint, currentDevices, StringComparison.Ordinal))
            {
                _logService.Write(
                    "encoder.capabilities.cache_missed",
                    LogLevel.Debug,
                    LogStatus.Skipped,
                    "Состав видеооборудования изменился или кэш записан в старом формате без отпечатка, выполняется повторный опрос оборудования",
                    source: SourceName,
                    properties: LogProps
                        .Create("Reason", CacheMissHardwareChanged)
                        .With("Tool", "ffmpeg"));
                return false;
            }

            nvenc = snapshot.Nvenc;
            temporalAq = snapshot.TemporalAq;
            av1 = snapshot.Av1;
            return true;
        }
        catch (Exception ex)
        {
            _logService.Write(
                "encoder.capabilities.cache_missed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Дисковый кэш возможностей кодирования недоступен, выполняется повторный опрос оборудования",
                ex,
                SourceName,
                properties: LogProps
                    .Create("Reason", CacheMissCorrupted)
                    .With("Tool", "ffmpeg"));
            return false;
        }
    }

    private void WriteCacheMissed(string reason)
    {
        _logService.Write(
            "encoder.capabilities.cache_missed",
            LogLevel.Debug,
            LogStatus.Skipped,
            "Дисковый кэш возможностей кодирования не найден, выполняется первичный опрос оборудования",
            source: SourceName,
            properties: LogProps
                .Create("Reason", reason)
                .With("Tool", "ffmpeg"));
    }

    /// <summary>
    /// Записывает текущий результат на диск. Ошибка записи не влияет на работу:
    /// опрос просто повторится при следующем запуске.
    /// </summary>
    private void SaveCache()
    {
        try
        {
            var snapshot = new NvencCapabilitySnapshot
            {
                Nvenc = IsNvencSupported,
                TemporalAq = IsNvencTemporalAqSupported,
                Av1 = IsNvencAv1Supported,
                FfmpegVersion = ReadFfmpegVersion(),
                DriverVersion = ReadDriverVersion(),
                DeviceFingerprint = ReadDeviceFingerprint(),
                Timestamp = DateTimeOffset.UtcNow,
            };
            string cachePath = ResolveCacheFilePath();
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _logService.Write(
                "encoder.capabilities.cache_write_failed",
                LogLevel.Debug,
                LogStatus.Skipped,
                "Дисковый кэш возможностей кодирования не записан, опрос повторится при следующем запуске",
                ex,
                SourceName,
                properties: LogProps
                    .Create("ErrorCode", "ENCODER_CACHE_WRITE_FAILED")
                    .With("Tool", "ffmpeg"));
        }
    }

    private string ResolveCacheFilePath()
    {
        if (!string.IsNullOrWhiteSpace(_cacheFilePathOverride))
        {
            return _cacheFilePathOverride;
        }

        try
        {
            string? settingsDirectory = _pathManager?.GetSettingsDirectory();
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                return Path.Combine(settingsDirectory, CacheFileName);
            }
        }
        catch
        {
            // Ниже — резервный путь без зависимостей.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTools",
            CacheFileName);
    }

    /// <summary>
    /// Дешёвая версия FFmpeg без запуска процессов: читается из метаданных
    /// бинарника. Пустая строка означает, что версия неизвестна и кэш недействителен.
    /// </summary>
    private string ReadFfmpegVersion()
    {
        if (_ffmpegVersionProvider is not null)
        {
            try
            {
                return _ffmpegVersionProvider() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            string binaryPath = _pathManager?.GetBinaryPath("ffmpeg.exe") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
            {
                return string.Empty;
            }

            string? fileVersion = FileVersionInfo.GetVersionInfo(binaryPath).FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion.Trim();
            }

            return new FileInfo(binaryPath).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Дешёвый отпечаток драйвера NVIDIA без запуска nvidia-smi: версии
    /// драйверов читаются из реестра видеоадаптеров. Пустая строка означает,
    /// что версия неизвестна и кэш недействителен.
    /// </summary>
    private string ReadDriverVersion()
    {
        if (_driverVersionProvider is not null)
        {
            try
            {
                return _driverVersionProvider() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return NoNvidiaDriverMarker;
            }

            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null)
            {
                return string.Empty;
            }

            var versions = baseKey.GetSubKeyNames()
                .Where(name => name.Length == 4 && char.IsDigit(name[0]))
                .Select(name =>
                {
                    try
                    {
                        using RegistryKey? subKey = baseKey.OpenSubKey(name);
                        string description = subKey?.GetValue(RegistryDriverDescValue)?.ToString() ?? string.Empty;
                        if (!description.Contains(NvidiaAdapterMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        return subKey?.GetValue("DriverVersion")?.ToString();
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version!.Trim())
                .OrderBy(version => version, StringComparer.Ordinal)
                .ToArray();
            return versions.Length == 0 ? NoNvidiaDriverMarker : string.Join(";", versions);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Дешёвый отпечаток состава видеооборудования без запуска процессов:
    /// имена адаптеров NVIDIA и их PCI-идентификаторы из реестра.
    /// Замена видеокарты на том же драйвере меняет отпечаток и делает
    /// кэш недействительным. Пустая строка означает, что состав неизвестен
    /// и кэш недействителен.
    /// </summary>
    private string ReadDeviceFingerprint()
    {
        if (_deviceFingerprintProvider is not null)
        {
            try
            {
                return _deviceFingerprintProvider() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return NoNvidiaDriverMarker;
            }

            using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is null)
            {
                return string.Empty;
            }

            var adapters = baseKey.GetSubKeyNames()
                .Where(name => name.Length == 4 && char.IsDigit(name[0]))
                .Select(name =>
                {
                    try
                    {
                        using RegistryKey? subKey = baseKey.OpenSubKey(name);
                        string description = subKey?.GetValue(RegistryDriverDescValue)?.ToString() ?? string.Empty;
                        if (!description.Contains(NvidiaAdapterMarker, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }

                        string deviceId = ReadAdapterDeviceId(subKey);
                        return string.IsNullOrWhiteSpace(deviceId)
                            ? description.Trim()
                            : $"{description.Trim()}|{deviceId}";
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(adapter => !string.IsNullOrWhiteSpace(adapter))
                .Select(adapter => adapter!.Trim())
                .OrderBy(adapter => adapter, StringComparer.Ordinal)
                .ToArray();
            return adapters.Length == 0 ? NoNvidiaDriverMarker : string.Join(";", adapters);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Читает PCI-идентификатор видеоадаптера из ключа реестра.
    /// Пустая строка означает отсутствие идентификатора.
    /// </summary>
    private static string ReadAdapterDeviceId(RegistryKey? subKey)
    {
        if (subKey is null)
        {
            return string.Empty;
        }

        try
        {
            string? matchingId = subKey.GetValue(RegistryMatchingIdValue)?.ToString();
            if (!string.IsNullOrWhiteSpace(matchingId))
            {
                return matchingId.Trim();
            }

            if (subKey.GetValue("HardwareID") is string[] hardwareIds)
            {
                string? firstId = hardwareIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                if (!string.IsNullOrWhiteSpace(firstId))
                {
                    return firstId.Trim();
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Снимок результата опроса для дискового кэша рядом с настройками.
    /// </summary>
    private sealed class NvencCapabilitySnapshot
    {
        public bool Nvenc { get; set; }

        public bool TemporalAq { get; set; }

        public bool Av1 { get; set; }

        public string FfmpegVersion { get; set; } = string.Empty;

        public string DriverVersion { get; set; } = string.Empty;

        public string DeviceFingerprint { get; set; } = string.Empty;

        public DateTimeOffset Timestamp { get; set; }
    }
}
