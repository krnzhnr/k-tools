// -*- coding: utf-8 -*-
using System.Threading;
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
    /// Отмена через токен прерывает ожидание и возвращает null без запуска новых процессов.
    /// </summary>
    Task<MediaStructure?> ProbeAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Дополнительно обогащает названия дорожек из ffprobe, если они отсутствуют.
    /// Вызывается точечно по запросу для предотвращения замедления первичного сканирования.
    /// </summary>
    Task EnrichTrackNamesAsync(MediaStructure structure);
}
