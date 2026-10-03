// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс менеджера настроек приложения K-Tools.
/// Все операции записи возвращают типизированный <see cref="PersistenceResult"/>,
/// поэтому вызывающий код может отличить «изменено в памяти» от «записано на диск».
/// </summary>
public interface ISettingsManager : IDisposable
{
    /// <summary>
    /// Перезаписывать ли существующие выходные файлы.
    /// </summary>
    bool OverwriteExisting { get; set; }

    /// <summary>
    /// Имя подпапки для результатов по умолчанию.
    /// </summary>
    string DefaultOutputSubfolder { get; set; }

    /// <summary>
    /// Использовать ли автоматическое создание подпапки результатов.
    /// </summary>
    bool UseAutoSubfolder { get; set; }

    /// <summary>
    /// Тема оформления интерфейса.
    /// </summary>
    string Theme { get; set; }

    /// <summary>
    /// Тип фона окон приложения (Mica или Acrylic).
    /// </summary>
    string BackdropType { get; set; }

    /// <summary>
    /// Максимальное количество параллельных задач обработки.
    /// </summary>
    int MaxParallelTasks { get; set; }

    /// <summary>
    /// Разрешить ли параллельное выполнение задач обработки.
    /// </summary>
    bool EnableParallel { get; set; }

    /// <summary>
    /// Очищать ли очередь перед добавлением новых файлов.
    /// </summary>
    bool ClearListOnAdd { get; set; }

    /// <summary>
    /// Отображать ли монитор логов (вкладку).
    /// </summary>
    bool ShowLogsTab { get; set; }

    /// <summary>
    /// Пользовательский путь к директории хранения логов.
    /// Установка значения только меняет настройку и не переинициализирует
    /// уже запущенную сессию журналирования.
    /// </summary>
    string LogDir { get; set; }

    /// <summary>
    /// Изменить пользовательский путь к директории хранения логов
    /// и получить типизированный результат сохранения.
    /// </summary>
    /// <param name="logDirectory">Новый путь или пустая строка для пути по умолчанию.</param>
    /// <returns>Результат операции сохранения настройки.</returns>
    PersistenceResult SetLogDirectory(string? logDirectory);

    /// <summary>
    /// Автоматически проверять обновления при старте.
    /// </summary>
    bool AutoCheckUpdates { get; set; }

    /// <summary>
    /// Включать ли бета-версии при поиске обновлений.
    /// </summary>
    bool IncludePreReleases { get; set; }

    /// <summary>
    /// Имитировать ли старую версию при проверке обновлений.
    /// </summary>
    bool DebugSimulateOldVersion { get; set; }

    /// <summary>
    /// Отключать ли действие кнопок обновления и скачивания (имитация пустышек).
    /// </summary>
    bool DebugDisableUpdateAction { get; set; }

    /// <summary>
    /// Флаг включения переименования выходных файлов по регулярным выражениям.
    /// </summary>
    bool RenameEnableRegex { get; set; }

    /// <summary>
    /// Использовать ли регулярные выражения при глобальном переименовании.
    /// </summary>
    bool RenameUseRegex { get; set; }

    /// <summary>
    /// Учитывать ли регистр при глобальном переименовании.
    /// </summary>
    bool RenameCaseSensitive { get; set; }

    /// <summary>
    /// Шаблон поиска (регулярное выражение) для переименования выходных файлов.
    /// </summary>
    string RenameRegexSearch { get; set; }

    /// <summary>
    /// Строка замены для переименования выходных файлов.
    /// </summary>
    string RenameRegexReplace { get; set; }

    /// <summary>
    /// Пользовательские шаблоны для поиска.
    /// </summary>
    List<TemplateItem> SearchTemplates { get; set; }

    /// <summary>
    /// Пользовательские шаблоны для замены.
    /// </summary>
    List<TemplateItem> ReplaceTemplates { get; set; }

    /// <summary>
    /// Получить значение настройки.
    /// Возвращается глубокая копия значения: изменение полученной коллекции или объекта
    /// не влияет на кэш и не обходит систему обнаружения изменений.
    /// </summary>
    T GetSetting<T>(string group, string key, T defaultValue);

    /// <summary>
    /// Записать значение настройки в кэш и запустить (или запланировать) сохранение на диск.
    /// В кэш попадает глубокая копия значения, поэтому последующие изменения исходного
    /// объекта вызывающей стороны не обходят систему обнаружения изменений.
    /// Сырое значение никогда не попадает в журнал: используется только безопасный хэш.
    /// </summary>
    /// <typeparam name="T">Тип значения настройки.</typeparam>
    /// <param name="group">Группа настройки.</param>
    /// <param name="key">Ключ настройки.</param>
    /// <param name="value">Новое значение.</param>
    /// <returns>Результат операции: Succeeded, Pending или Failed.</returns>
    PersistenceResult SetSetting<T>(string group, string key, T value);

    /// <summary>
    /// Получить все сохраненные настройки определенной группы.
    /// Значения возвращаются глубокими копиями и не позволяют изменять кэш извне.
    /// </summary>
    Dictionary<string, object> GetAllSettingsInGroup(string group);

    /// <summary>
    /// Инициализировать настройки по умолчанию на основе схемы скриптов.
    /// Успех возвращается только после подтверждённой записи на диск.
    /// Если предыдущая загрузка была деградированной (повреждённый settings.json),
    /// значения по умолчанию применяются только в памяти и не записываются поверх
    /// повреждённого файла: для этого существует <see cref="ResetToDefaults"/>.
    /// </summary>
    /// <param name="scripts">Скрипты, для которых создаются значения по умолчанию.</param>
    /// <returns>Результат операции сохранения.</returns>
    PersistenceResult InitializeDefaults(List<AbstractScript> scripts);

    /// <summary>
    /// Явный сброс настроек к значениям по умолчанию, инициированный пользователем.
    /// Единственный сценарий, который разрешает запись поверх деградированной загрузки.
    /// </summary>
    /// <param name="scripts">Скрипты, для которых восстанавливаются значения по умолчанию.</param>
    /// <param name="applicationDefaults">
    /// Явные значения в формате «группа/ключ» — «значение». Записи без разделителя игнорируются.
    /// </param>
    /// <returns>Результат операции сохранения.</returns>
    PersistenceResult ResetToDefaults(
        List<AbstractScript> scripts,
        IReadOnlyList<KeyValuePair<string, object?>>? applicationDefaults);

    /// <summary>
    /// Нормализовать имя скрипта для использования в качестве имени секции (группы) JSON.
    /// </summary>
    string GetSafeGroupName(string scriptName);

    /// <summary>
    /// Сохранить текущее состояние настроек на диск атомарно (только атомарные стратегии
    /// фиксации: временный файл плюс move/replace, без неатомарного копирования).
    /// Возвращает типизированный результат; отмена фиксируется только когда
    /// фактическая запись не выполнялась.
    /// </summary>
    /// <returns>Результат операции сохранения.</returns>
    PersistenceResult SaveSettings();
}
