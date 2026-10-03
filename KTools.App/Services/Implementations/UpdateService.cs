// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// DTO для десериализации ответа GitHub API о релизах.
/// </summary>
internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto> Assets { get; set; } = new();
}

/// <summary>
/// DTO для десериализации ассетов релиза GitHub.
/// </summary>
internal sealed class GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}

/// <summary>
/// Реализация службы проверки, загрузки и установки обновлений приложения.
/// Все комментарии, логи и исключения реализованы строго на русском языке с исчерпывающей информативностью.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const string SourceName = nameof(UpdateService);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _httpClient;
    private const string GitHubApiUrl = "https://api.github.com/repos/krnzhnr/k-tools/releases";

    /// <summary>
    /// Аргументы командной строки, используемые при запуске процесса фоновой установки обновления.
    /// Ключи /SILENT и /SUPPRESSMSGBOXES обеспечивают автоматическую установку без лишних диалоговых окон.
    /// Флаг /MERGETASKS="!desktopicon" исключает выполнение задачи создания ярлыка на рабочем столе,
    /// предотвращая повторное появление удаленного пользователем ярлыка.
    /// </summary>
    public const string SilentUpdateInstallerArguments = "/SILENT /SUPPRESSMSGBOXES /MERGETASKS=\"!desktopicon\"";
    private readonly ILogService _logService;
    private readonly ISettingsManager _settingsManager;

    /// <summary>
    /// Инициализирует новый экземпляр класса UpdateService с внедрением логгера, настроек и фабрики HTTP-клиентов.
    /// </summary>
    public UpdateService(
        ILogService logService,
        ISettingsManager settingsManager,
        IHttpClientFactory httpClientFactory)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _httpClient = _httpClientFactory.CreateClient("DefaultClient");

        // Настраиваем заголовки по умолчанию для HttpClient.
        // GitHub API требует наличие User-Agent для всех запросов.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("K-Tools-App/2.0.0");
        }
    }

    /// <inheritdoc/>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(
        bool includePreReleases,
        CancellationToken cancellationToken = default)
    {
        _logService.Write("app.update.check_started", LogLevel.Debug, LogStatus.Running, $"Запущена проверка обновлений приложения, предварительные версии: {(includePreReleases ? "включены" : "отключены")}", source: SourceName, properties: LogProps.Create("Group", "Update").With("HostLabel", "api.github.com"));

        try
        {
            string currentVersionStr = GetCurrentVersion();
            _logService.Write("app.update.current_version", LogLevel.Debug, LogStatus.Succeeded, $"Текущая версия приложения: {LogRedactor.CompactSafeToken(currentVersionStr)}", source: SourceName, properties: LogProps.Create("Version", LogRedactor.CompactSafeToken(currentVersionStr)));

            // Отправляем запрос к GitHub API для получения списка релизов
            var response = await _httpClient.GetAsync(GitHubApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(json);

            if (releases == null || releases.Count == 0)
            {
                _logService.Write("app.update.releases_unavailable", LogLevel.Warning, LogStatus.Failed, "Список релизов недоступен или ответ не удалось разобрать", source: SourceName, properties: LogProps.Create("ErrorCode", "RELEASES_UNAVAILABLE").With("HostLabel", "api.github.com"));
                return null;
            }

            // Фильтруем релизы в зависимости от настроек (включать ли пререлизы)
            var targetReleases = includePreReleases
                ? releases
                : releases.Where(r => !r.Prerelease);

            GitHubReleaseDto? bestRelease = null;
            string? bestVersionStr = null;
            GitHubAssetDto? bestAsset = null;

            foreach (var release in targetReleases)
            {
                string rawTagName = release.TagName;
                string remoteVersionStr = rawTagName.TrimStart('v');

                // Если тег релиза является фиксированным (для пререлизов в CI/CD),
                // мы извлекаем реальную SemVer-версию из названия релиза (например, из "K-Tools C# Edition v2.0.0-preview.24")
                if (rawTagName.Equals("csharp-pre-release", StringComparison.OrdinalIgnoreCase) ||
                    rawTagName.Equals("pre-release", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(release.Name, @"v(\d+\.\d+\.\d+[\w\-\.]*)");
                    if (match.Success)
                    {
                        remoteVersionStr = match.Groups[1].Value;
                    }
                }

                // Ищем исполняемый файл установщика среди ассетов релиза (обычно файл .exe)
                var installerAsset = release.Assets.FirstOrDefault(
                    a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                          a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));

                if (installerAsset == null)
                {
                    // Если специальный setup.exe не найден, берем первый попавшийся .exe ассет
                    installerAsset = release.Assets.FirstOrDefault(
                        a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                }

                if (installerAsset == null)
                {
                    _logService.Write("app.update.release_skipped", LogLevel.Debug, LogStatus.Skipped, $"Релиз '{LogRedactor.CompactSafeToken(rawTagName)}' пропущен: отсутствует исполняемый файл установщика", source: SourceName, properties: LogProps.Create("Version", LogRedactor.CompactSafeToken(rawTagName)).With("Extension", "exe").With("Reason", "InstallerMissing"));
                    continue;
                }

                // Сравниваем версию релиза с текущей версией приложения
                int comparisonWithCurrent = CompareVersions(remoteVersionStr, currentVersionStr);
                if (comparisonWithCurrent > 0)
                {
                    // Версия новее текущей. Теперь проверяем, новее ли она нашего лучшего найденного кандидата
                    if (bestVersionStr == null || CompareVersions(remoteVersionStr, bestVersionStr) > 0)
                    {
                        bestRelease = release;
                        bestVersionStr = remoteVersionStr;
                        bestAsset = installerAsset;
                    }
                }
            }

            if (bestRelease != null && bestVersionStr != null && bestAsset != null)
            {
                _logService.Write("app.update.available", LogLevel.Info, LogStatus.Changed, $"Доступно обновление: {LogRedactor.CompactSafeToken(bestVersionStr)}, установлена версия {LogRedactor.CompactSafeToken(currentVersionStr)}, размер файла {bestAsset.Size} байт", source: SourceName, properties: LogProps.Create("Version", LogRedactor.CompactSafeToken(bestVersionStr)).With("TotalBytes", bestAsset.Size));

                return new UpdateInfo(
                    version: bestVersionStr,
                    title: string.IsNullOrEmpty(bestRelease.Name) ? bestRelease.TagName : bestRelease.Name,
                    changelog: bestRelease.Body,
                    downloadUrl: bestAsset.BrowserDownloadUrl,
                    fileName: bestAsset.Name,
                    size: bestAsset.Size,
                    isPrerelease: bestRelease.Prerelease);
            }

            _logService.Write("app.update.up_to_date", LogLevel.Info, LogStatus.Skipped, "Доступных обновлений не обнаружено", source: SourceName, properties: LogProps.Create("Group", "Update"));
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var friendlyEx = new InvalidOperationException(
                "Превышен лимит запросов к GitHub API (Rate Limit Exceeded). Пожалуйста, повторите попытку позже.",
                ex);
            _logService.Write("app.update.rate_limited", LogLevel.Warning, LogStatus.Failed, "Превышен лимит запросов к сервису релизов, проверка обновлений отложена", friendlyEx, SourceName, properties: LogProps.Create("ErrorCode", "UPDATE_RATE_LIMITED").With("StatusCode", "403").With("HostLabel", "api.github.com").With("Retryable", true));
            throw friendlyEx;
        }
        catch (Exception ex)
        {
            _logService.Write("app.update.check_failed", LogLevel.Warning, LogStatus.Failed, "Проверка наличия обновлений не завершена", ex, SourceName, properties: LogProps.Create("ErrorCode", "UPDATE_CHECK_FAILED").With("HostLabel", "api.github.com").With("Retryable", true));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DownloadAndInstallUpdateAsync(
        string downloadUrl,
        string fileName,
        Action<double> progressCallback,
        CancellationToken cancellationToken = default)
    {
        _logService.Write(
            "update.download_started",
            LogLevel.Info,
            LogStatus.Running,
            $"Запущено скачивание обновления (файл: '{fileName}')",
            null,
            "UpdateService",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "download",
                ["HostLabel"] = SafeHostLabel(downloadUrl),
                ["FileName"] = fileName
            });

        string tempFilePath = Path.Combine(Path.GetTempPath(), fileName);

        if (File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);
            }
            catch (Exception delEx)
            {
                _logService.Write(
                    "update.precleanup_failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    $"Не удалось заранее удалить временный файл обновления '{fileName}'",
                    delEx,
                    "UpdateService",
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "precleanup",
                        ["ErrorCode"] = "precleanup-failed",
                        ["FileName"] = fileName
                    });
            }
        }

        try
        {
            // Скачиваем файл с отслеживанием прогресса
            using (var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                _logService.Write("app.update.download_size", LogLevel.Debug, LogStatus.Succeeded, totalBytes.HasValue ? $"Размер файла обновления: {totalBytes.Value} байт" : "Размер файла обновления неизвестен", source: SourceName, properties: LogProps.Create("TotalBytes", totalBytes.HasValue ? (object)totalBytes.Value : null));

                using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(
                    tempFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    true))
                {
                    var buffer = new byte[8192];
                    long totalReadBytes = 0;
                    int readBytes;
                    int lastProgressTick = Environment.TickCount - 100;
                    double lastProgress = -1.0;

                    while ((readBytes = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, readBytes, cancellationToken);
                        totalReadBytes += readBytes;

                        if (totalBytes.HasValue)
                        {
                            double progress = (double)totalReadBytes / totalBytes.Value * 100.0;
                            if (progress >= 100.0 || Environment.TickCount - lastProgressTick >= 100)
                            {
                                lastProgressTick = Environment.TickCount;
                                if (progress != lastProgress)
                                {
                                    lastProgress = progress;
                                    progressCallback(progress);
                                }
                            }
                        }
                    }
                }
            }

            _logService.Write(
                "update.download_completed",
                LogLevel.Info,
                LogStatus.Succeeded,
                $"Файл обновления успешно скачан: '{fileName}'. Запуск процесса бесшумного обновления...",
                null,
                "UpdateService",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "download",
                    ["FileName"] = fileName
                });

            // Запускаем инсталлятор во внешнем процессе с ключами /SILENT /SUPPRESSMSGBOXES и исключением пересоздания ярлыка рабочего стола
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempFilePath,
                Arguments = SilentUpdateInstallerArguments,
                UseShellExecute = true // Обязательный параметр в .NET 10 для запуска exe файлов напрямую
            };

            System.Diagnostics.Process.Start(processStartInfo);

            _logService.Write("app.update.installer_started", LogLevel.Info, LogStatus.Running, "Процесс установщика запущен, приложение завершает работу для перезаписи файлов и автоматического перезапуска", source: SourceName, properties: LogProps.Create("Group", "Update").With("CleanupState", "NotStarted"));

            // Безопасно выходим из приложения
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _logService.Write(
                "update.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Возникла ошибка в процессе скачивания или установки обновления (файл: '{fileName}')",
                ex,
                "UpdateService",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "download",
                    ["ErrorCode"] = "update-failed",
                    ["FileName"] = fileName,
                    ["Retryable"] = true
                });

            // В случае ошибки пытаемся зачистить поврежденный файл, если он был создан
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                    _logService.Write("app.update.temp_removed", LogLevel.Debug, LogStatus.Succeeded, "Временный файл обновления удалён после сбоя", source: SourceName, properties: LogProps.Create("CleanupState", "Removed"));
                }
            }
            catch (Exception deleteEx)
            {
                _logService.Write(
                    "update.cleanup_failed",
                    LogLevel.Warning,
                    LogStatus.PartiallySucceeded,
                    "Не удалось удалить повреждённый временный файл обновления",
                    deleteEx,
                    "UpdateService",
                    properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Stage"] = "cleanup",
                        ["CleanupState"] = "failed",
                        ["ErrorCode"] = "cleanup-failed"
                    });
            }

            throw;
        }
    }

    /// <summary>
    /// Возвращает информационную версию текущей сборки приложения.
    /// </summary>
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

    private string GetCurrentVersion()
    {
        // Если в настройках включен режим симуляции старой версии для отладки,
        // принудительно возвращаем 1.0.0 для срабатывания баннера обновлений.
        if (_settingsManager.DebugSimulateOldVersion)
        {
            _logService.Write("app.update.simulate_old_version", LogLevel.Warning, LogStatus.Changed, "Включена имитация старой версии для проверки сценария обновления", source: SourceName, properties: LogProps.Create("Version", "1.0.0").With("Reason", "DebugSimulation"));
            return "1.0.0";
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion))
            {
                int plusIdx = infoVersion.IndexOf('+');
                return plusIdx > 0 ? infoVersion.Substring(0, plusIdx) : infoVersion;
            }
            return assembly.GetName().Version?.ToString() ?? "2.0.0";
        }
        catch (Exception ex)
        {
            _logService.Write("app.version.resolve_failed", LogLevel.Warning, LogStatus.PartiallySucceeded, "Версию текущей сборки определить не удалось, проверка обновлений может быть неточной", ex, SourceName, properties: LogProps.Create("ErrorCode", "VERSION_RESOLVE_FAILED"));
            return "2.0.0";
        }
    }

    /// <summary>
    /// Компаратор для сравнения двух версий по спецификации SemVer.
    /// Перенаправляет вызов в изолированный класс VersionComparer.
    /// </summary>
    public static int CompareVersions(string versionA, string versionB)
    {
        return KTools_App.Core.VersionComparer.CompareVersions(versionA, versionB);
    }
}
