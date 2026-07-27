// -*- coding: utf-8 -*-
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс сервиса фонового анализа структуры и метаданных медиафайлов.
/// </summary>
public interface IMediaProbeService
{
    /// <summary>
    /// Асинхронно анализирует медиафайл и возвращает его структуру (дорожки, вложения, длительность).
    /// </summary>
    Task<MediaStructure?> ProbeAsync(string filePath);

    /// <summary>
    /// Дополнительно обогащает названия дорожек из ffprobe, если они отсутствуют.
    /// Вызывается точечно по запросу для предотвращения замедления первичного сканирования.
    /// </summary>
    Task EnrichTrackNamesAsync(MediaStructure structure);
}
