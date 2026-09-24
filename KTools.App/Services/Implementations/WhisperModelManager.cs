// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Служба управления, проверки наличия и потокобезопасной загрузки нейросетевых моделей Whisper.
/// Хранит файлы моделей в изолированном каталоге bin/whisper-models/.
/// </summary>
public sealed class WhisperModelManager : IWhisperModelManager
{
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly HttpClient _httpClient;
    private readonly List<WhisperModelInfo> _modelsRegistry = new();
    private readonly string _modelsDir;

    private const string HuggingFaceBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private readonly object _downloadsLock = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, int Progress)> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public event Action<string, int>? DownloadProgressChanged;

    /// <inheritdoc/>
    public event Action<string, bool, string?>? DownloadCompleted;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="WhisperModelManager"/> с внедрением зависимостей.
    /// </summary>
    public WhisperModelManager(
        ILogService logService,
        IPathManager pathManager,
        IHttpClientFactory httpClientFactory)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _httpClient = httpClientFactory.CreateClient("DefaultClient");
        _modelsDir = Path.Combine(_pathManager.GetBinDirectory(), "whisper-models");

        InitializeRegistry();
    }

    /// <summary>
    /// Инициализирует статический реестр поддерживаемых моделей GGML.
    /// </summary>
    private void InitializeRegistry()
    {
        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "tiny",
            DisplayName = "Tiny (~75 МБ) — Сверхбыстрая",
            FileName = "ggml-tiny.bin",
            SizeMb = 75.0,
            RequiredMemoryMb = 390.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-tiny.bin",
            IsMultilingual = true,
            Description = "Минимальная модель. Максимальная скорость работы, базовое качество распознавания."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "base",
            DisplayName = "Base (~142 МБ) — Быстрая черновая",
            FileName = "ggml-base.bin",
            SizeMb = 142.0,
            RequiredMemoryMb = 500.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-base.bin",
            IsMultilingual = true,
            Description = "Оптимальна для быстрого распознавания чистой речи без сильных фоновых шумов."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "small",
            DisplayName = "Small (~466 МБ) — Баланс скорости и точности",
            FileName = "ggml-small.bin",
            SizeMb = 466.0,
            RequiredMemoryMb = 1024.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-small.bin",
            IsMultilingual = true,
            Description = "Хороший баланс между скоростью обработки и качеством транскрибации для диалогов."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "small-q5_1",
            DisplayName = "Small Q5_1 (~182 МБ) — Сжатая Small",
            FileName = "ggml-small-q5_1.bin",
            SizeMb = 182.0,
            RequiredMemoryMb = 700.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-small-q5_1.bin",
            IsMultilingual = true,
            Description = "Квантованная 5-битная версия модели Small, экономящая оперативную память."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "medium",
            DisplayName = "Medium (~1.5 ГБ) — Высокая точность",
            FileName = "ggml-medium.bin",
            SizeMb = 1530.0,
            RequiredMemoryMb = 2600.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-medium.bin",
            IsMultilingual = true,
            Description = "Высокое качество распознавания сложных терминов, акцентов и зашумлённой речи."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "medium-q5_0",
            DisplayName = "Medium Q5_0 (~515 МБ) — Сжатая Medium",
            FileName = "ggml-medium-q5_0.bin",
            SizeMb = 515.0,
            RequiredMemoryMb = 1300.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-medium-q5_0.bin",
            IsMultilingual = true,
            Description = "Квантованная 5-битная версия Medium с малым размером и высокой точностью."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "large-v3",
            DisplayName = "Large v3 (~3.1 ГБ) — Максимальная точность",
            FileName = "ggml-large-v3.bin",
            SizeMb = 3100.0,
            RequiredMemoryMb = 4700.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-large-v3.bin",
            IsMultilingual = true,
            Description = "Флагманская модель с наивысшей точностью. Требует мощный процессор или видеокарту."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "large-v3-q5_0",
            DisplayName = "Large v3 Q5_0 (~1.08 ГБ) — Сжатая Large v3",
            FileName = "ggml-large-v3-q5_0.bin",
            SizeMb = 1080.0,
            RequiredMemoryMb = 2200.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-large-v3-q5_0.bin",
            IsMultilingual = true,
            Description = "Квантованная 5-битная версия Large v3. Сохраняет точность при скромном потреблении памяти."
        });

        _modelsRegistry.Add(new WhisperModelInfo
        {
            Key = "large-v3-turbo",
            DisplayName = "Large v3 Turbo (~1.6 ГБ) — Рекомендуемая",
            FileName = "ggml-large-v3-turbo.bin",
            SizeMb = 1620.0,
            RequiredMemoryMb = 2000.0,
            DownloadUrl = $"{HuggingFaceBaseUrl}/ggml-large-v3-turbo.bin",
            IsMultilingual = true,
            Description = "Флагманская быстрая архитектура: до 4x быстрее оригинальной Large v3 при аналогичном качестве."
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<WhisperModelInfo> GetAvailableModels() => _modelsRegistry;

    /// <inheritdoc/>
    public WhisperModelInfo? GetModelInfo(string modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return null;
        return _modelsRegistry.FirstOrDefault(m => m.Key.Equals(modelKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public bool IsModelDownloaded(string modelKey)
    {
        var info = GetModelInfo(modelKey);
        if (info == null) return false;

        string path = GetModelFilePath(modelKey);
        return File.Exists(path) && new FileInfo(path).Length > 1024 * 1024; // Более 1 МБ
    }

    /// <inheritdoc/>
    public string GetModelFilePath(string modelKey)
    {
        var info = GetModelInfo(modelKey);
        string fileName = info != null ? info.FileName : $"ggml-{modelKey}.bin";
        return Path.Combine(_modelsDir, fileName);
    }

    /// <inheritdoc/>
    public string GetModelsDirectory() => _modelsDir;

    /// <inheritdoc/>
    public bool IsModelDownloading(string modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return false;
        lock (_downloadsLock)
        {
            return _activeDownloads.ContainsKey(modelKey);
        }
    }

    /// <inheritdoc/>
    public int GetModelDownloadProgress(string modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return 0;
        lock (_downloadsLock)
        {
            return _activeDownloads.TryGetValue(modelKey, out var state) ? state.Progress : 0;
        }
    }

    /// <inheritdoc/>
    public void CancelDownload(string modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return;
        CancellationTokenSource? cts = null;
        lock (_downloadsLock)
        {
            if (_activeDownloads.TryGetValue(modelKey, out var state))
            {
                cts = state.Cts;
            }
        }

        if (cts != null && !cts.IsCancellationRequested)
        {
            try
            {
                cts.Cancel();
                _logService.Info($"Запрошена отмена загрузки модели Whisper '{modelKey}'", nameof(WhisperModelManager));
            }
            catch (Exception ex)
            {
                _logService.Warn($"Ошибка при отмене загрузки модели Whisper '{modelKey}': {ex.Message}", nameof(WhisperModelManager));
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DownloadModelAsync(
        string modelKey,
        IProgress<int>? progress = null,
        IProgress<string>? speed = null,
        CancellationToken cancellationToken = default)
    {
        var info = GetModelInfo(modelKey);
        if (info == null)
        {
            _logService.Error($"Неизвестный ключ модели Whisper: '{modelKey}'", nameof(WhisperModelManager));
            return false;
        }

        CancellationTokenSource linkedCts;
        lock (_downloadsLock)
        {
            if (_activeDownloads.ContainsKey(modelKey))
            {
                _logService.Warn($"Модель Whisper '{modelKey}' уже находится в процессе скачивания", nameof(WhisperModelManager));
                return false;
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeDownloads[modelKey] = (linkedCts, 0);
        }

        bool isSuccess = false;
        string? failureReason = null;

        try
        {
            if (!Directory.Exists(_modelsDir))
            {
                Directory.CreateDirectory(_modelsDir);
                _logService.Info($"Создана директория для хранения моделей: '{_modelsDir}'", nameof(WhisperModelManager));
            }

            string targetPath = GetModelFilePath(modelKey);
            string tempPath = $"{targetPath}.download";

            _logService.Info($"Начало загрузки модели Whisper '{info.DisplayName}' по адресу: {info.DownloadUrl}", nameof(WhisperModelManager));

            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            long totalRead = 0;
            var buffer = new byte[65536];

            DateTime lastSpeedUpdate = DateTime.UtcNow;
            long lastBytesRead = 0;
            int lastProgressTick = Environment.TickCount - 200;
            int lastReportedPercent = -1;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(linkedCts.Token))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token);
                    totalRead += read;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)((double)totalRead / totalBytes.Value * 100);

                        if (lastReportedPercent != percent)
                        {
                            int nowTick = Environment.TickCount;
                            if (nowTick - lastProgressTick >= 200)
                            {
                                lastReportedPercent = percent;
                                lastProgressTick = nowTick;
                                lock (_downloadsLock)
                                {
                                    if (_activeDownloads.ContainsKey(modelKey))
                                    {
                                        _activeDownloads[modelKey] = (linkedCts, percent);
                                    }
                                }

                                progress?.Report(percent);
                                DownloadProgressChanged?.Invoke(modelKey, percent);
                            }
                        }
                    }

                    DateTime now = DateTime.UtcNow;
                    double elapsedSec = (now - lastSpeedUpdate).TotalSeconds;
                    if (elapsedSec >= 0.5)
                    {
                        double bytesDelta = totalRead - lastBytesRead;
                        double speedBytesPerSec = bytesDelta / elapsedSec;
                        speed?.Report(FormatSpeed(speedBytesPerSec));

                        lastBytesRead = totalRead;
                        lastSpeedUpdate = now;
                    }
                }
            }

            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); } catch { /* Игнорируем */ }
            }

            File.Move(tempPath, targetPath, true);
            progress?.Report(100);
            DownloadProgressChanged?.Invoke(modelKey, 100);

            _logService.Info($"Модель Whisper '{info.DisplayName}' успешно скачана и сохранена в '{targetPath}'", nameof(WhisperModelManager));
            isSuccess = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            failureReason = "Отменено пользователем";
            _logService.Warn($"Загрузка модели Whisper '{modelKey}' была отменена пользователем", nameof(WhisperModelManager));
            return false;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            _logService.Exception(ex, $"Ошибка при загрузке модели Whisper '{modelKey}': {ex.Message}", nameof(WhisperModelManager));
            return false;
        }
        finally
        {
            lock (_downloadsLock)
            {
                _activeDownloads.Remove(modelKey);
            }

            linkedCts.Dispose();

            string tempPath = $"{GetModelFilePath(modelKey)}.download";
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Игнорируем */ }
            }

            DownloadCompleted?.Invoke(modelKey, isSuccess, failureReason);
        }
    }

    /// <inheritdoc/>
    public bool DeleteModel(string modelKey)
    {
        try
        {
            string path = GetModelFilePath(modelKey);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logService.Info($"Файл модели Whisper '{modelKey}' успешно удален с диска: '{path}'", nameof(WhisperModelManager));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Не удалось удалить файл модели Whisper '{modelKey}': {ex.Message}", nameof(WhisperModelManager));
            return false;
        }
    }

    public const string VadModelFileName = "ggml-silero-v6.2.0.bin";
    public const string VadModelDownloadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin";

    /// <inheritdoc/>
    public bool IsVadModelDownloaded()
    {
        string path = GetVadModelFilePath();
        if (!File.Exists(path)) return false;
        try
        {
            var fi = new FileInfo(path);
            return fi.Length > 500_000; // Модель Silero v6.2.0 весит ~885 КБ
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public string GetVadModelFilePath()
    {
        return Path.Combine(_modelsDir, VadModelFileName);
    }

    /// <inheritdoc/>
    public async Task<bool> EnsureVadModelDownloadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsVadModelDownloaded())
        {
            return true;
        }

        Directory.CreateDirectory(_modelsDir);
        string targetPath = GetVadModelFilePath();
        string tempPath = targetPath + ".download";

        try
        {
            _logService.Info($"Начало автоматической загрузки модели детекции речи Silero VAD из '{VadModelDownloadUrl}'...", nameof(WhisperModelManager));
            using var response = await _httpClient.GetAsync(VadModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(tempPath, targetPath);

            _logService.Info($"Модель Silero VAD успешно сохранена в '{targetPath}'", nameof(WhisperModelManager));
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }

            _logService.Exception(ex, $"Ошибка при загрузке модели Silero VAD: {ex.Message}", nameof(WhisperModelManager));
            return false;
        }
    }

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
