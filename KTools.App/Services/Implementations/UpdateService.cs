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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _httpClient;
    private const string GitHubApiUrl = "https://api.github.com/repos/krnzhnr/k-tools/releases";
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
        _logService.Info(
            $"Запущена проверка обновлений (Включать предварительные версии: {(includePreReleases ? "Да" : "Нет")})",
            "UpdateService");

        try
        {
            string currentVersionStr = GetCurrentVersion();
            _logService.DebugLog($"Текущая информационная версия приложения: {currentVersionStr}", "UpdateService");

            // Отправляем запрос к GitHub API для получения списка релизов
            var response = await _httpClient.GetAsync(GitHubApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(json);

            if (releases == null || releases.Count == 0)
            {
                _logService.Warn("Список релизов на GitHub пуст или не удалось десериализовать ответ.", "UpdateService");
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
                    _logService.DebugLog(
                        $"Пропуск релиза '{release.Name}' ({rawTagName}), так как в нем отсутствует исполняемый файл установщика (.exe)",
                        "UpdateService");
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
                _logService.Info(
                    $"Найдено наиболее подходящее обновление: {bestVersionStr} (Текущая: {currentVersionStr}). " +
                    $"Название релиза: '{bestRelease.Name}'. Размер файла: {bestAsset.Size} байт.",
                    "UpdateService");

                return new UpdateInfo(
                    version: bestVersionStr,
                    title: string.IsNullOrEmpty(bestRelease.Name) ? bestRelease.TagName : bestRelease.Name,
                    changelog: bestRelease.Body,
                    downloadUrl: bestAsset.BrowserDownloadUrl,
                    fileName: bestAsset.Name,
                    size: bestAsset.Size,
                    isPrerelease: bestRelease.Prerelease);
            }

            _logService.Info("Доступных обновлений не обнаружено.", "UpdateService");
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var friendlyEx = new InvalidOperationException(
                "Превышен лимит запросов к GitHub API (Rate Limit Exceeded). Пожалуйста, повторите попытку позже.",
                ex);
            _logService.Exception(
                friendlyEx,
                "Превышен лимит запросов к GitHub API (403 Forbidden).",
                "UpdateService");
            throw friendlyEx;
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                "Произошла непредвиденная ошибка при проверке наличия обновлений на GitHub.",
                "UpdateService");
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
        _logService.Info($"Запущено скачивание обновления из источника: {downloadUrl}", "UpdateService");

        string tempFilePath = Path.Combine(Path.GetTempPath(), fileName);
        _logService.DebugLog($"Файл будет временно сохранен по пути: {tempFilePath}", "UpdateService");

        if (File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);
            }
            catch (Exception delEx)
            {
                _logService.Warn($"Не удалось заранее удалить временный файл обновления '{tempFilePath}': {delEx.Message}", "UpdateService");
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
                _logService.DebugLog($"Размер файла для скачивания: {(totalBytes.HasValue ? $"{totalBytes.Value} байт" : "Неизвестен")}", "UpdateService");

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

                    while ((readBytes = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, readBytes, cancellationToken);
                        totalReadBytes += readBytes;

                        if (totalBytes.HasValue)
                        {
                            double progress = (double)totalReadBytes / totalBytes.Value * 100.0;
                            progressCallback(progress);
                        }
                    }
                }
            }

            _logService.Info($"Файл обновления успешно скачан: {tempFilePath}. Запуск процесса бесшумного обновления...", "UpdateService");

            // Запускаем инсталлятор во внешнем процессе с ключами /SILENT /SUPPRESSMSGBOXES
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempFilePath,
                Arguments = "/SILENT /SUPPRESSMSGBOXES",
                UseShellExecute = true // Обязательный параметр в .NET 10 для запуска exe файлов напрямую
            };

            System.Diagnostics.Process.Start(processStartInfo);

            _logService.Info("Процесс установщика успешно запущен. Завершение работы текущего приложения для перезаписи файлов и автоматического перезапуска.", "UpdateService");

            // Безопасно выходим из приложения
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _logService.Exception(
                ex,
                $"Возникла ошибка в процессе скачивания или установки обновления. Локальный путь: {tempFilePath}",
                "UpdateService");

            // В случае ошибки пытаемся зачистить поврежденный файл, если он был создан
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                    _logService.DebugLog("Временный файл обновления успешно удален после сбоя.", "UpdateService");
                }
            }
            catch (Exception deleteEx)
            {
                _logService.Exception(
                    deleteEx,
                    "Не удалось удалить поврежденный временный файл обновления во временной директории.",
                    "UpdateService");
            }

            throw;
        }
    }

    /// <summary>
    /// Возвращает информационную версию текущей сборки приложения.
    /// </summary>
    private string GetCurrentVersion()
    {
        // Если в настройках включен режим симуляции старой версии для отладки,
        // принудительно возвращаем 1.0.0 для срабатывания баннера обновлений.
        if (_settingsManager.DebugSimulateOldVersion)
        {
            _logService.Warn("[Отладка] Активирована имитация старой версии. Возвращаем версию 1.0.0", "UpdateService");
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
            _logService.Exception(
                ex,
                "Не удалось определить версию текущей сборки приложения из метаданных Assembly.",
                "UpdateService");
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
