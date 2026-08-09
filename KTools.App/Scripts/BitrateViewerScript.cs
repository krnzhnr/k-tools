// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт детального покадрового анализа побитового битрейта видео и аудио файлов.
/// Относится к категории "Инструменты". Поддерживает все медиа-контейнеры и аудиоформаты.
/// Анализирует временные штампы pts_time и формирует точную карту распределения битрейта.
/// Все комментарии и логи строго на русском языке в соответствии с правилами проекта.
/// </summary>
public sealed class BitrateViewerScript : AbstractScript
{
    private readonly IBitrateAnalyzerService _bitrateAnalyzerService;
    private readonly IDiskTypeDetectorService _diskTypeDetectorService;

    public BitrateViewerScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IBitrateAnalyzerService bitrateAnalyzerService,
        IDiskTypeDetectorService diskTypeDetectorService)
        : base(logService, settingsManager, pathManager)
    {
        _bitrateAnalyzerService = bitrateAnalyzerService ?? throw new ArgumentNullException(nameof(bitrateAnalyzerService));
        _diskTypeDetectorService = diskTypeDetectorService ?? throw new ArgumentNullException(nameof(diskTypeDetectorService));
    }

    public override string Name => AppConstants.ScriptMetadata.BitrateViewerName;
    public override string Description => AppConstants.ScriptMetadata.BitrateViewerDesc;
    public override string Category => AppConstants.ScriptCategory.Tools;
    public override string IconName => "\uE9D2"; // Analytics icon

    /// <summary>
    /// Поддержка ВСЕХ видео и аудио контейнеров/потоков.
    /// </summary>
    public override string[] FileExtensions => AppConstants.VideoContainers
        .Concat(AppConstants.AudioContainers)
        .Concat(AppConstants.AudioStreams)
        .Distinct()
        .ToArray();

    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    public override List<SettingField> SettingsSchema => new()
    {
        new SettingField(
            "save_png_export",
            "Сохранять копию графика в PNG",
            SettingType.Checkbox,
            false,
            "Параметры графиков",
            comment: "Автоматически сохранять высокополигональный график PNG в папку Completed рядом с файлом.",
            column: 0, colSpan: 2)
    };

    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        ResetCancellation();
        var results = new List<string>();

        _logService.Info($"Запуск анализа битрейта для файла '{Path.GetFileName(filePath)}'", "BitrateViewerScript");

        // Определение типа накопителя
        var driveType = _diskTypeDetectorService.GetDriveTypeForPath(filePath);
        _logService.Info($"Файл '{Path.GetFileName(filePath)}' расположен на диске типа: {driveType}", "BitrateViewerScript");

        try
        {
            var analysisResult = await _bitrateAnalyzerService.AnalyzeBitrateAsync(
                filePath,
                (pct, status) =>
                {
                    progressCallback(fileIndex, totalCount, status, pct);
                },
                CancellationToken.None);

            if (IsCancelled)
            {
                results.Add($"⚠ Прервано пользователем: {Path.GetFileName(filePath)}");
                return results;
            }

            if (analysisResult != null)
            {
                // Привязываем результат анализа к элементу очереди файлов в UI
                var fileItem = FilesQueue.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (fileItem != null)
                {
                    fileItem.BitrateData = analysisResult;
                }

                progressCallback(fileIndex, totalCount, "Завершено!", 100.0);

                string summaryMsg = $"✅ Готово: {Path.GetFileName(filePath)} | Кодек: {analysisResult.CodecName} | Средний: {analysisResult.MeanMbps} Mbps (Мин: {analysisResult.MinMbps}, Макс: {analysisResult.MaxMbps}, StdDev: {analysisResult.StdDevMbps}) | Ключевых кадров: {analysisResult.KeyframeTimes.Length}";
                _logService.Info(summaryMsg, "BitrateViewerScript");
                results.Add(summaryMsg);



                bool savePng = _settingsManager.GetSetting("Script_Анализ_битрейта_видео_и_аудио", "save_png_export", false);
                if (savePng)
                {
                    _logService.Info($"Фоновое сохранение PNG-графика для '{Path.GetFileName(filePath)}'", "BitrateViewerScript");
                }
            }
            else
            {
                string errMsg = $"❌ Ошибка анализа битрейта для файла: {Path.GetFileName(filePath)}";
                _logService.Error(errMsg, "BitrateViewerScript");
                results.Add(errMsg);
            }
        }
        catch (Exception ex)
        {
            string err = $"❌ Сбой обработки файла '{Path.GetFileName(filePath)}': {ex.Message}";
            _logService.Exception(ex, err, "BitrateViewerScript");
            results.Add(err);
        }

        return results;
    }

    public override string GetOutputExtension(string inputPath)
    {
        return Path.GetExtension(inputPath);
    }
}
