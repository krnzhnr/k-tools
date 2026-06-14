// -*- coding: utf-8 -*-
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Информация о доступном обновлении приложения.
/// Описание полей выполнено строго на русском языке.
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>Номер версии доступного обновления.</summary>
    public string Version { get; }

    /// <summary>Заголовок релиза на GitHub.</summary>
    public string Title { get; }

    /// <summary>Список изменений в релизе (Changelog/Примечания к выпуску).</summary>
    public string Changelog { get; }

    /// <summary>Прямая ссылка для скачивания файла установщика.</summary>
    public string DownloadUrl { get; }

    /// <summary>Рекомендуемое имя файла для скачивания.</summary>
    public string FileName { get; }

    /// <summary>Размер файла установщика в байтах.</summary>
    public long Size { get; }

    /// <summary>Указывает, является ли этот релиз предварительной версией (Pre-release).</summary>
    public bool IsPrerelease { get; }

    /// <summary>
    /// Инициализирует новый экземпляр класса UpdateInfo с подробными метаданными о релизе.
    /// </summary>
    public UpdateInfo(
        string version,
        string title,
        string changelog,
        string downloadUrl,
        string fileName,
        long size,
        bool isPrerelease)
    {
        Version = version;
        Title = title;
        Changelog = changelog;
        DownloadUrl = downloadUrl;
        FileName = fileName;
        Size = size;
        IsPrerelease = isPrerelease;
    }
}

/// <summary>
/// Интерфейс службы проверки, загрузки и установки обновлений приложения.
/// Все описания и XML-комментарии написаны строго на русском языке.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Выполняет проверку наличия новых версий приложения на GitHub.
    /// </summary>
    /// <param name="includePreReleases">Флаг, указывающий на необходимость включения предварительных версий в поиск.</param>
    /// <param name="cancellationToken">Токен отмены асинхронной операции.</param>
    /// <returns>Объект UpdateInfo при наличии обновления, иначе null.</returns>
    Task<UpdateInfo?> CheckForUpdatesAsync(
        bool includePreReleases,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Скачивает файл инсталлятора, запускает процесс установки и закрывает текущее приложение.
    /// </summary>
    /// <param name="downloadUrl">Прямая ссылка на скачивание исполняемого файла инсталлятора.</param>
    /// <param name="fileName">Имя сохраняемого файла на диске.</param>
    /// <param name="progressCallback">Обратный вызов для уведомления о прогрессе скачивания (значение от 0.0 до 100.0).</param>
    /// <param name="cancellationToken">Токен отмены асинхронной операции.</param>
    /// <returns>Асинхронная задача выполнения установки.</returns>
    Task DownloadAndInstallUpdateAsync(
        string downloadUrl,
        string fileName,
        Action<double> progressCallback,
        CancellationToken cancellationToken = default);
}
