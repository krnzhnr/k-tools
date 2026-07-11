// -*- coding: utf-8 -*-
using System.Collections.Generic;
using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Интерфейс менеджера настроек приложения K-Tools.
/// </summary>
public interface ISettingsManager
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
    /// </summary>
    string LogDir { get; set; }

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
    /// </summary>
    T GetSetting<T>(string group, string key, T defaultValue);

    /// <summary>
    /// Записать значение настройки.
    /// </summary>
    void SetSetting<T>(string group, string key, T value);

    /// <summary>
    /// Инициализировать настройки по умолчанию на основе схемы скриптов.
    /// </summary>
    void InitializeDefaults(List<AbstractScript> scripts);

    /// <summary>
    /// Нормализовать имя скрипта для использования в качестве имени секции (группы) JSON.
    /// </summary>
    string GetSafeGroupName(string scriptName);

    /// <summary>
    /// Сохранить текущее состояние настроек на диск.
    /// </summary>
    void SaveSettings();
}
