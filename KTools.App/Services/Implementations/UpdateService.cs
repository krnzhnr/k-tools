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
    /// Шаблон имён собственных файлов установщиков, оставляемых службой обновлений во временной папке.
    /// </summary>
    private const string StaleSetupFilePattern = "KTools_v*_Setup.exe";

    /// <summary>
    /// Возраст файла, моложе которого он считается возможно незавершённой загрузкой и не удаляется.
    /// </summary>
    private static readonly TimeSpan StaleSetupGracePeriod = TimeSpan.FromHours(1);

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
            List<GitHubReleaseDto> releases = await FetchReleasesAsync(cancellationToken);

            if (releases.Count == 0)
            {
                _logService.Write("app.update.releases_unavailable", LogLevel.Warning, LogStatus.Failed, "Список релизов недоступен или ответ не удалось разобрать", source: SourceName, properties: LogProps.Create("ErrorCode", "RELEASES_UNAVAILABLE").With("HostLabel", "api.github.com"));
                return null;
            }

            // Формируем список кандидатов: релизы нужного канала, у которых есть пригодный файл установщика
            List<ReleaseCandidate> candidates = EnumerateCandidates(releases, includePreReleases);

            // Выбираем самый новый кандидат, версия которого новее установленной
            ReleaseCandidate? best = PickNewestAbove(candidates, currentVersionStr);

            if (best != null)
            {
                _logService.Write("app.update.available", LogLevel.Info, LogStatus.Changed, $"Доступно обновление: {LogRedactor.CompactSafeToken(best.Version)}, установлена версия {LogRedactor.CompactSafeToken(currentVersionStr)}, размер файла {best.Asset.Size} байт", source: SourceName, properties: LogProps.Create("Version", LogRedactor.CompactSafeToken(best.Version)).With("TotalBytes", best.Asset.Size));

                return new UpdateInfo(
                    version: best.Version,
                    title: string.IsNullOrEmpty(best.Release.Name) ? best.Release.TagName : best.Release.Name,
                    changelog: best.Release.Body,
                    downloadUrl: best.Asset.BrowserDownloadUrl,
                    fileName: best.Asset.Name,
                    size: best.Asset.Size,
                    isPrerelease: best.Release.Prerelease);
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

        // Цель скачивания: адрес ассета и имя файла могут быть уточнены, если ссылка устарела
        DownloadTarget target = new DownloadTarget(downloadUrl, fileName);
        string tempFilePath = Path.Combine(Path.GetTempPath(), target.FileName);

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
            // Первая попытка выполняется по ссылке, полученной при проверке обновлений.
            // Пререлизы публикуются в релиз с перемещаемым тегом, поэтому ассет может быть
            // заменён новой сборкой сразу после проверки. В этом случае GitHub отвечает 404/410,
            // и тогда цель восстанавливается по свежему списку релизов, а скачивание повторяется один раз.
            try
            {
                await DownloadAndInstallCoreAsync(target, tempFilePath, progressCallback, cancellationToken);
                return;
            }
            catch (HttpRequestException ex) when (IsAssetMissing(ex))
            {
                target = await ResolveAlternateTargetAsync(target, ex, cancellationToken);
                tempFilePath = Path.Combine(Path.GetTempPath(), target.FileName);
            }

            await DownloadAndInstallCoreAsync(target, tempFilePath, progressCallback, cancellationToken);
        }
        catch (Exception ex)
        {
            _logService.Write(
                "update.failed",
                LogLevel.Error,
                LogStatus.Failed,
                $"Возникла ошибка в процессе скачивания или установки обновления (файл: '{target.FileName}')",
                ex,
                "UpdateService",
                properties: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Stage"] = "download",
                    ["ErrorCode"] = "update-failed",
                    ["FileName"] = target.FileName,
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
    /// Скачивает установщик по указанной цели и запускает его в бесшумном режиме.
    /// </summary>
    /// <param name="target">Адрес ассета и имя сохраняемого файла.</param>
    /// <param name="tempFilePath">Полный путь временного файла установщика.</param>
    /// <param name="progressCallback">Обратный вызов для уведомления о прогрессе (значение от 0.0 до 100.0).</param>
    /// <param name="cancellationToken">Токен отмены асинхронной операции.</param>
    private async Task DownloadAndInstallCoreAsync(
        DownloadTarget target,
        string tempFilePath,
        Action<double> progressCallback,
        CancellationToken cancellationToken)
    {
        // Скачиваем файл с отслеживанием прогресса
        using (var response = await _httpClient.GetAsync(
            target.Url,
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
            $"Файл обновления успешно скачан: '{target.FileName}'. Запуск процесса бесшумного обновления...",
            null,
            "UpdateService",
            properties: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Stage"] = "download",
                ["FileName"] = target.FileName
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

    /// <inheritdoc/>
    public int CleanupStaleSetupFiles(string? directory = null)
    {
        string targetDir;
        try
        {
            targetDir = string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory;
        }
        catch (Exception ex)
        {
            _logService.Write("update.stale_setup_cleanup_failed", LogLevel.Warning, LogStatus.Failed, "Не удалось определить папку для чистки старых установщиков", ex, SourceName, properties: LogProps.Create("Stage", "cleanup").With("ErrorCode", "TEMP_DIR_RESOLVE_FAILED"));
            return 0;
        }

        List<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(targetDir)
                .EnumerateFiles(StaleSetupFilePattern, SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (Exception ex)
        {
            _logService.Write("update.stale_setup_cleanup_failed", LogLevel.Warning, LogStatus.Failed, "Не удалось перечислить старые установщики во временной папке", ex, SourceName, properties: LogProps.Create("Stage", "cleanup").With("ErrorCode", "STALE_SETUP_ENUM_FAILED"));
            return 0;
        }

        int removed = 0;
        long freedBytes = 0;
        DateTime thresholdUtc = DateTime.UtcNow - StaleSetupGracePeriod;
        foreach (FileInfo file in candidates)
        {
            try
            {
                file.Refresh();
                if (!file.Exists || file.LastWriteTimeUtc >= thresholdUtc)
                {
                    continue;
                }

                long size = file.Length;
                file.Delete();
                removed++;
                freedBytes += size;
            }
            catch (Exception ex)
            {
                _logService.Write("update.stale_setup_delete_failed", LogLevel.Warning, LogStatus.PartiallySucceeded, $"Не удалось удалить старый установщик '{LogProps.FileName(file.Name)}'", ex, SourceName, properties: LogProps.Create("Stage", "cleanup").With("ErrorCode", "STALE_SETUP_DELETE_FAILED"));
            }
        }

        _logService.Write(
            "update.stale_setup_cleaned",
            LogLevel.Info,
            removed > 0 ? LogStatus.Succeeded : LogStatus.Skipped,
            removed > 0
                ? $"Удалены устаревшие установщики обновлений: {removed} шт."
                : "Устаревших установщиков обновлений во временной папке нет",
            null,
            SourceName,
            properties: LogProps.Create("Stage", "cleanup").With("Count", removed).With("Bytes", freedBytes));
        return removed;
    }

    /// <summary>
    /// Описание цели скачивания установщика: адрес ассета и имя сохраняемого файла.
    /// </summary>
    private sealed class DownloadTarget
    {
        public DownloadTarget(string url, string fileName)
        {
            Url = url;
            FileName = fileName;
        }

        /// <summary>Адрес скачивания ассета.</summary>
        public string Url { get; }

        /// <summary>Имя сохраняемого файла установщика.</summary>
        public string FileName { get; }
    }

    /// <summary>
    /// Кандидат на обновление: релиз GitHub, его версия и ассет установщика.
    /// </summary>
    private sealed class ReleaseCandidate
    {
        public ReleaseCandidate(GitHubReleaseDto release, string version, GitHubAssetDto asset)
        {
            Release = release;
            Version = version;
            Asset = asset;
        }

        public GitHubReleaseDto Release { get; }
        public string Version { get; }
        public GitHubAssetDto Asset { get; }
    }

    /// <summary>
    /// Признак того, что запрошенный файл ассета на сервере отсутствует.
    /// Именно так проявляется случай, когда сборка пререлиза уже заменена новой.
    /// </summary>
    private static bool IsAssetMissing(HttpRequestException ex)
    {
        return ex.StatusCode == System.Net.HttpStatusCode.NotFound
            || ex.StatusCode == System.Net.HttpStatusCode.Gone;
    }

    /// <summary>
    /// Запрашивает и разбирает список релизов репозитория.
    /// </summary>
    private async Task<List<GitHubReleaseDto>> FetchReleasesAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(GitHubApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<GitHubReleaseDto>>(json) ?? new List<GitHubReleaseDto>();
    }

    /// <summary>
    /// Формирует список кандидатов на обновление для указанного канала релизов.
    /// Релизы без подходящего файла установщика пропускаются с записью в журнал.
    /// </summary>
    private List<ReleaseCandidate> EnumerateCandidates(IEnumerable<GitHubReleaseDto> releases, bool includePreReleases)
    {
        var candidates = new List<ReleaseCandidate>();
        foreach (GitHubReleaseDto release in releases)
        {
            if (!includePreReleases && release.Prerelease)
            {
                continue;
            }

            string version = ResolveReleaseVersion(release);
            GitHubAssetDto? installerAsset = FindInstallerAsset(release);
            if (installerAsset == null || string.IsNullOrEmpty(installerAsset.BrowserDownloadUrl))
            {
                _logService.Write("app.update.release_skipped", LogLevel.Debug, LogStatus.Skipped, $"Релиз '{LogRedactor.CompactSafeToken(release.TagName)}' пропущен: отсутствует исполняемый файл установщика", source: SourceName, properties: LogProps.Create("Version", LogRedactor.CompactSafeToken(release.TagName)).With("Extension", "exe").With("Reason", "InstallerMissing"));
                continue;
            }

            candidates.Add(new ReleaseCandidate(release, version, installerAsset));
        }

        return candidates;
    }

    /// <summary>
    /// Возвращает кандидата с самой новой версией, которая строго новее базовой.
    /// </summary>
    private static ReleaseCandidate? PickNewestAbove(List<ReleaseCandidate> candidates, string baselineVersion)
    {
        ReleaseCandidate? best = null;
        foreach (ReleaseCandidate candidate in candidates)
        {
            if (CompareVersions(candidate.Version, baselineVersion) <= 0)
            {
                continue;
            }

            if (best == null || CompareVersions(candidate.Version, best.Version) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Извлекает номер версии из имени файла установщика (например, из "KTools_v2.3.1-preview.213_Setup.exe").
    /// </summary>
    private static string? TryExtractVersionFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"v(\d+\.\d+\.\d+[\w\-\.]*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Определяет SemVer-версию релиза. Для релизов с фиксированным тегом (пререлизы CI/CD)
    /// версия извлекается из названия релиза.
    /// </summary>
    private static string ResolveReleaseVersion(GitHubReleaseDto release)
    {
        string rawTagName = release.TagName ?? string.Empty;
        string remoteVersionStr = rawTagName.TrimStart('v');

        if (rawTagName.Equals("csharp-pre-release", StringComparison.OrdinalIgnoreCase) ||
            rawTagName.Equals("pre-release", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(release.Name ?? string.Empty, @"v(\d+\.\d+\.\d+[\w\-\.]*)");
            if (match.Success)
            {
                remoteVersionStr = match.Groups[1].Value;
            }
        }

        return remoteVersionStr;
    }

    /// <summary>
    /// Находит ассет установщика в релизе: сначала setup.exe, затем любой .exe.
    /// </summary>
    private static GitHubAssetDto? FindInstallerAsset(GitHubReleaseDto release)
    {
        var assets = release.Assets ?? new List<GitHubAssetDto>();

        GitHubAssetDto? installerAsset = assets.FirstOrDefault(
            a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                 a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));

        return installerAsset ?? assets.FirstOrDefault(
            a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Восстанавливает цель скачивания по свежему списку релизов, если исходная ссылка устарела.
    /// Выбирается самый новый ассет того же канала (стабильный или пререлиз), а при его отсутствии —
    /// тот же номер версии с другим именем файла.
    /// </summary>
    /// <exception cref="InvalidOperationException">В списке релизов нет ни одного подходящего установщика.</exception>
    private async Task<DownloadTarget> ResolveAlternateTargetAsync(
        DownloadTarget staleTarget,
        HttpRequestException failure,
        CancellationToken cancellationToken)
    {
        _logService.Write(
            "app.update.download_asset_missing",
            LogLevel.Warning,
            LogStatus.Failed,
            $"Файл обновления '{staleTarget.FileName}' больше недоступен на сервере, запрашивается актуальный ассет",
            failure,
            SourceName,
            properties: LogProps.Create("Stage", "download")
                .With("ErrorCode", "asset-missing")
                .With("StatusCode", failure.StatusCode.HasValue ? (int)failure.StatusCode.Value : 0)
                .With("FileName", staleTarget.FileName)
                .With("HostLabel", SafeHostLabel(staleTarget.Url)));

        string? requestedVersion = TryExtractVersionFromFileName(staleTarget.FileName);
        bool includePreReleases = requestedVersion != null && requestedVersion.Contains('-');

        List<GitHubReleaseDto> releases;
        try
        {
            releases = await FetchReleasesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logService.Write("app.update.reresolve_failed", LogLevel.Warning, LogStatus.PartiallySucceeded, "Не удалось получить актуальный список релизов для восстановления ссылки на установщик", ex, SourceName, properties: LogProps.Create("ErrorCode", "RERESOLVE_FAILED").With("HostLabel", "api.github.com").With("Retryable", true));
            throw new InvalidOperationException(
                "Файл обновления больше недоступен на сервере, а получить список актуальных версий не удалось. Проверьте подключение к интернету и повторите проверку обновлений.",
                ex);
        }

        List<ReleaseCandidate> candidates = EnumerateCandidates(releases, includePreReleases);

        ReleaseCandidate? chosen = requestedVersion == null
            ? PickNewestAbove(candidates, "0.0.0")
            : PickNewestAbove(candidates, requestedVersion)
              ?? candidates.FirstOrDefault(c => CompareVersions(c.Version, requestedVersion) == 0)
              ?? PickNewestAbove(candidates, "0.0.0");

        if (chosen == null)
        {
            _logService.Write("app.update.download_target_unavailable", LogLevel.Warning, LogStatus.Failed, $"В актуальном списке релизов нет подходящего установщика для замены файла '{staleTarget.FileName}'", source: SourceName, properties: LogProps.Create("Stage", "download").With("ErrorCode", "NO_REPLACEMENT_ASSET").With("Reason", "NoCandidate"));
            throw new InvalidOperationException(
                "Файл обновления больше недоступен на сервере, и подходящей замены среди опубликованных версий нет. Повторите проверку обновлений позже.",
                failure);
        }

        _logService.Write(
            "app.update.download_target_reresolved",
            LogLevel.Info,
            LogStatus.Changed,
            $"Ссылка на установщик восстановлена по актуальному списку релизов: '{staleTarget.FileName}' → '{chosen.Asset.Name}' (версия {chosen.Version})",
            null,
            SourceName,
            properties: LogProps.Create("Stage", "download")
                .With("Version", LogRedactor.CompactSafeToken(chosen.Version))
                .With("FileName", chosen.Asset.Name)
                .With("Reason", "AssetMissing"));

        return new DownloadTarget(chosen.Asset.BrowserDownloadUrl, chosen.Asset.Name);
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
