// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс менеджера внешних зависимостей приложения K-Tools.
/// </summary>
public interface IDependencyManager
{
    /// <summary>
    /// Событие изменения статуса любой из зависимостей.
    /// </summary>
    event Action<string, DependencyStatus>? StatusChanged;

    /// <summary>
    /// Событие прогресса скачивания зависимости (ключ, процент).
    /// </summary>
    event Action<string, int>? ProgressChanged;

    /// <summary>
    /// Событие обновления скорости скачивания зависимости (ключ, форматированная скорость).
    /// </summary>
    event Action<string, string>? SpeedUpdated;

    /// <summary>
    /// Событие завершения установки (ключ, успех, сообщение об ошибке).
    /// </summary>
    event Action<string, bool, string>? InstallFinished;

    /// <summary>
    /// Получить полный список зарегистрированных в реестре зависимостей.
    /// </summary>
    IReadOnlyList<DependencyInfo> GetRegistry();

    /// <summary>
    /// Проверить и обновить статусы всех зависимостей на диске.
    /// </summary>
    void RefreshAllStatuses();

    /// <summary>
    /// Получить текущий статус указанной зависимости.
    /// </summary>
    DependencyStatus GetStatus(string key);

    /// <summary>
    /// Проверить, установлена ли и верифицирована зависимость.
    /// </summary>
    bool IsInstalled(string key);

    /// <summary>
    /// Проверить, доступно ли обновление для указанной зависимости.
    /// </summary>
    bool IsUpdateAvailable(string key);

    /// <summary>
    /// Проверить, установлены ли все обязательные зависимости (ffmpeg, mkvtoolnix).
    /// </summary>
    bool AreRequiredDependenciesInstalled();

    /// <summary>
    /// Запустить процесс скачивания, распаковки и верификации зависимости.
    /// </summary>
    Task InstallDependencyAsync(string key);

    /// <summary>
    /// Отменить активный процесс установки указанной зависимости.
    /// </summary>
    void CancelInstallation(string key);

    /// <summary>
    /// Удалить файлы зависимости с диска и сбросить её статус.
    /// </summary>
    bool RemoveDependency(string key);

    /// <summary>
    /// Получить определенную версию установленной зависимости (например "7.1.0" или "v93.0").
    /// </summary>
    string GetInstalledVersion(string key);

    /// <summary>
    /// Выполняет проверку обновлений всех зависимостей (включая FFmpeg, MKVToolNix, eac3to и yt-dlp) раз в сутки.
    /// </summary>
    Task CheckAllDependencyUpdatesAsync(bool force = false);

    /// <summary>
    /// Устанавливает имитацию флага обновления для отладки и проверки UI.
    /// </summary>
    void SetSimulatedUpdateAvailable(string key, bool available);

    /// <summary>
    /// Выполняет проверку обновлений для утилиты yt-dlp раз в сутки и обновляет её при необходимости.
    /// </summary>
    /// <param name="force">Принудительно запустить проверку без учёта 24-часового интервала.</param>
    /// <returns>Асинхронная задача проверки/обновления.</returns>
    Task CheckAndUpdateYtDlpAsync(bool force = false);
}
