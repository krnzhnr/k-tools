// -*- coding: utf-8 -*-
namespace KTools_App.Services.Contracts;

/// <summary>
/// Сведения о запуске из неосновной копии приложения:
/// текущая папка запуска отличается от папки зарегистрированной установки.
/// Все описания выполнены строго на русском языке.
/// </summary>
/// <param name="CurrentDirectory">Папка, из которой запущен текущий процесс.</param>
/// <param name="CurrentVersion">Версия запущенной копии.</param>
/// <param name="RegisteredDirectory">Папка зарегистрированной установки.</param>
/// <param name="RegisteredExecutablePath">Полный путь к исполняемому файлу зарегистрированной установки.</param>
/// <param name="RegisteredVersion">Версия зарегистрированной установки.</param>
/// <param name="ShouldWarn">Признак того, что запущенной копии стоит показать предупреждение
/// (зарегистрированная установка не старее запущенной).</param>
public sealed record StaleCopyInfo(
    string CurrentDirectory,
    string CurrentVersion,
    string RegisteredDirectory,
    string RegisteredExecutablePath,
    string RegisteredVersion,
    bool ShouldWarn);

/// <summary>
/// Служба определения папки зарегистрированной установки приложения.
/// Позволяет обнаружить запуск из устаревшей копии в другой папке:
/// тихие обновления ставятся в зарегистрированную папку, а старая копия
/// при повторном запуске снова предлагает то же обновление.
/// Все описания выполнены строго на русском языке.
/// </summary>
public interface IInstallLocationService
{
    /// <summary>
    /// Проверяет, запущен ли текущий процесс из папки зарегистрированной установки.
    /// Возвращает сведения о неосновной копии либо null, если текущий запуск
    /// является основным, установка не зарегистрирована или её файлы отсутствуют.
    /// </summary>
    StaleCopyInfo? DetectStaleCopy();

    /// <summary>
    /// Запускает исполняемый файл зарегистрированной установки с флагом захвата
    /// места первичного экземпляра: новая копия ждёт завершения текущей
    /// и продолжает работу в основной установке. Завершение текущего процесса
    /// выполняет вызывающая сторона на UI-потоке.
    /// </summary>
    /// <param name="info">Сведения о неосновной копии.</param>
    /// <returns>True, если основная копия успешно запущена.</returns>
    bool TryLaunchRegisteredCopy(StaleCopyInfo info);
}
