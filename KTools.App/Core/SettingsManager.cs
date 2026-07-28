// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using KTools_App.Services.Contracts;

namespace KTools_App.Core;

/// <summary>
/// Менеджер пользовательских настроек приложения K-Tools.
/// Сохраняет все параметры программы и настроек скриптов в локальный файл JSON.
/// Полностью потокобезопасен и поддерживает горячую синхронизацию.
/// </summary>
public sealed class SettingsManager : ISettingsManager
{
    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly object _lock = new();
    private readonly string _settingsFilePath;
    private Dictionary<string, Dictionary<string, object>> _cache;

    /// <summary>
    /// Инициализирует новый экземпляр класса SettingsManager с внедрением зависимостей.
    /// </summary>
    public SettingsManager(ILogService logService, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        // Создаем путь к файлу настроек в безопасной директории
        string settingsDir = _pathManager.GetSettingsDirectory();
        _settingsFilePath = Path.Combine(settingsDir, "settings.json");
        _cache = new Dictionary<string, Dictionary<string, object>>();

        LoadSettings();
        _logService.InitializeLogFile(LogDir);
    }

    /// <summary>
    /// Перезаписывать ли существующие файлы.
    /// </summary>
    public bool OverwriteExisting
    {
        get => GetSetting("General", "OverwriteExisting", false);
        set => SetSetting("General", "OverwriteExisting", value);
    }

    /// <summary>
    /// Имя подпапки для результатов по умолчанию.
    /// </summary>
    public string DefaultOutputSubfolder
    {
        get => GetSetting("General", "DefaultOutputSubfolder", "KTools_Result");
        set => SetSetting("General", "DefaultOutputSubfolder", value);
    }

    /// <summary>
    /// Использовать ли автоматическое создание подпапки.
    /// </summary>
    public bool UseAutoSubfolder
    {
        get => GetSetting("General", "UseAutoSubfolder", false);
        set => SetSetting("General", "UseAutoSubfolder", value);
    }

    /// <summary>
    /// Тема оформления интерфейса.
    /// </summary>
    public string Theme
    {
        get => GetSetting("General", "Theme", "Dark");
        set => SetSetting("General", "Theme", value);
    }

    /// <summary>
    /// Тип фона окон приложения (Mica или Acrylic).
    /// </summary>
    public string BackdropType
    {
        get => GetSetting("General", "BackdropType", "Mica");
        set => SetSetting("General", "BackdropType", value);
    }

    /// <summary>
    /// Максимальное количество параллельных задач обработки.
    /// </summary>
    public int MaxParallelTasks
    {
        get => GetSetting("General", "MaxParallelTasks", Math.Max(1, Environment.ProcessorCount / 2));
        set => SetSetting("General", "MaxParallelTasks", value);
    }

    /// <summary>
    /// Разрешить ли параллельное выполнение задач обработки.
    /// </summary>
    public bool EnableParallel
    {
        get => GetSetting("General", "EnableParallel", true);
        set => SetSetting("General", "EnableParallel", value);
    }

    /// <summary>
    /// Очищать ли очередь перед добавлением новых файлов.
    /// </summary>
    public bool ClearListOnAdd
    {
        get => GetSetting("General", "ClearListOnAdd", false);
        set => SetSetting("General", "ClearListOnAdd", value);
    }

    /// <summary>
    /// Отображать ли монитор логов (вкладку).
    /// </summary>
    public bool ShowLogsTab
    {
        get => GetSetting("Logging", "ShowLogsTab", false);
        set => SetSetting("Logging", "ShowLogsTab", value);
    }

    /// <summary>
    /// Пользовательский путь к директории хранения логов.
    /// </summary>
    public string LogDir
    {
        get => GetSetting("Logging", "LogDir", string.Empty);
        set
        {
            SetSetting("Logging", "LogDir", value);
            _logService.InitializeLogFile(value);
        }
    }

    /// <summary>
    /// Автоматически проверять обновления при старте.
    /// </summary>
    public bool AutoCheckUpdates
    {
        get => GetSetting("Updates", "AutoCheckUpdates", true);
        set => SetSetting("Updates", "AutoCheckUpdates", value);
    }

    /// <summary>
    /// Определяет, является ли текущая сборка пре-релизом (Preview/Alpha/Beta/RC).
    /// </summary>
    public static bool IsPreviewBuild
    {
        get
        {
            var infoVer = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;

            return infoVer.Contains("-preview", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-alpha", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-beta", StringComparison.OrdinalIgnoreCase) ||
                   infoVer.Contains("-rc", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Включать ли бета-версии при поиске обновлений.
    /// </summary>
    public bool IncludePreReleases
    {
        get => GetSetting("Updates", "IncludePreReleases", IsPreviewBuild);
        set => SetSetting("Updates", "IncludePreReleases", value);
    }

    /// <summary>
    /// Имитировать ли старую версию при проверке обновлений.
    /// </summary>
    public bool DebugSimulateOldVersion
    {
        get => GetSetting("Debug", "DebugSimulateOldVersion", false);
        set => SetSetting("Debug", "DebugSimulateOldVersion", value);
    }

    /// <summary>
    /// Отключать ли действие кнопок обновления и скачивания (имитация пустышек).
    /// </summary>
    public bool DebugDisableUpdateAction
    {
        get => GetSetting("Debug", "DebugDisableUpdateAction", false);
        set => SetSetting("Debug", "DebugDisableUpdateAction", value);
    }

    /// <summary>
    /// Флаг включения переименования выходных файлов по регулярным выражениям (Regex).
    /// </summary>
    public bool RenameEnableRegex
    {
        get => GetSetting("General", "RenameEnableRegex", false);
        set => SetSetting("General", "RenameEnableRegex", value);
    }

    /// <summary>
    /// Шаблон поиска (регулярное выражение) для переименования выходных файлов.
    /// </summary>
    public string RenameRegexSearch
    {
        get => GetSetting("General", "RenameRegexSearch", string.Empty);
        set => SetSetting("General", "RenameRegexSearch", value);
    }

    /// <summary>
    /// Строка замены для переименования выходных файлов.
    /// </summary>
    public string RenameRegexReplace
    {
        get => GetSetting("General", "RenameRegexReplace", string.Empty);
        set => SetSetting("General", "RenameRegexReplace", value);
    }

    /// <summary>
    /// Пользовательские шаблоны для поиска.
    /// </summary>
    public List<TemplateItem> SearchTemplates
    {
        get => GetSetting("General", "SearchTemplates", GetDefaultSearchTemplates());
        set => SetSetting("General", "SearchTemplates", value);
    }

    /// <summary>
    /// Пользовательские шаблоны для замены.
    /// </summary>
    public List<TemplateItem> ReplaceTemplates
    {
        get => GetSetting("General", "ReplaceTemplates", GetDefaultReplaceTemplates());
        set => SetSetting("General", "ReplaceTemplates", value);
    }

    private static List<TemplateItem> GetDefaultSearchTemplates()
    {
        return new List<TemplateItem>
        {
            new() { Pattern = " - (\\d+)", Description = "поиск серии" },
            new() { Pattern = "\\d+", Description = "поиск любых цифр" },
            new() { Pattern = "\\.mkv$", Description = "поиск расширения '.mkv'" },
            new() { Pattern = "\\s+", Description = "пробелы" },
            new() { Pattern = "[^a-zA-Z0-9]", Description = "спецсимволы" }
        };
    }

    private static List<TemplateItem> GetDefaultReplaceTemplates()
    {
        return new List<TemplateItem>
        {
            new() { Pattern = "$1", Description = "первая группа" },
            new() { Pattern = "$2", Description = "вторая группа" },
            new() { Pattern = " - [$1]", Description = "замена в [скобки]" },
            new() { Pattern = "${num}", Description = "порядковый номер" },
            new() { Pattern = "${num:2}", Description = "номер с нулями (01, 02)" },
            new() { Pattern = "${ruuidv4}", Description = "случайный UUID v4" },
            new() { Pattern = "${YYYY}", Description = "текущий год" },
            new() { Pattern = "${MM}", Description = "текущий месяц" },
            new() { Pattern = "${DD}", Description = "текущий день" },
            new() { Pattern = "${hh}", Description = "часы (24-часовой)" },
            new() { Pattern = "${mm}", Description = "минуты" },
            new() { Pattern = "${ss}", Description = "секунды" }
        };
    }

    /// <summary>
    /// Использовать ли регулярные выражения для переименования выходных файлов.
    /// </summary>
    public bool RenameUseRegex
    {
        get => GetSetting("General", "RenameUseRegex", true);
        set => SetSetting("General", "RenameUseRegex", value);
    }

    /// <summary>
    /// Учитывать ли регистр при переименовании выходных файлов.
    /// </summary>
    public bool RenameCaseSensitive
    {
        get => GetSetting("General", "RenameCaseSensitive", false);
        set => SetSetting("General", "RenameCaseSensitive", value);
    }

    /// <summary>
    /// Загрузить настройки из JSON-файла на диске в кэш.
    /// </summary>
    public void LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var data = JsonSerializer.Deserialize<
                        Dictionary<string, Dictionary<string, object>>
                    >(json);

                    if (data != null)
                    {
                        _cache = data;
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Резервный пустой кэш при ошибках десериализации
            }
            _cache = new Dictionary<string, Dictionary<string, object>>();
        }
    }

    /// <summary>
    /// Сохранить текущий кэш настроек в JSON-файл на диске.
    /// </summary>
    public void SaveSettings()
    {
        lock (_lock)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(_cache, options);

                string? dir = Path.GetDirectoryName(_settingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_settingsFilePath, json);
                _logService.DebugLog("Конфигурация успешно сохранена на диск", "SettingsManager");
            }
            catch (Exception ex)
            {
                _logService.Error($"Ошибка сохранения конфигурации на диск: {ex.Message}", "SettingsManager");
            }
        }
    }

    /// <summary>
    /// Получить значение настройки.
    /// </summary>
    public T GetSetting<T>(string group, string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(group, out var groupDict))
            {
                if (groupDict.TryGetValue(key, out var val))
                {
                    try
                    {
                        if (val is JsonElement jsonElem)
                        {
                            // Конвертация типов JsonElement в нативные типы C#
                            if (typeof(T) == typeof(bool))
                            {
                                return (T)(object)jsonElem.GetBoolean();
                            }
                            if (typeof(T) == typeof(int))
                            {
                                return (T)(object)jsonElem.GetInt32();
                            }
                            if (typeof(T) == typeof(string))
                            {
                                // Если элемент является строкой, возвращаем её значение.
                                if (jsonElem.ValueKind == JsonValueKind.String)
                                {
                                    return (T)(object)jsonElem.GetString()!;
                                }
                                // Если элемент является логическим значением, возвращаем строковое представление ("True"/"False").
                                if (jsonElem.ValueKind == JsonValueKind.True || jsonElem.ValueKind == JsonValueKind.False)
                                {
                                    return (T)(object)jsonElem.GetBoolean().ToString();
                                }
                                // Если элемент является числом, возвращаем его сырое текстовое представление.
                                if (jsonElem.ValueKind == JsonValueKind.Number)
                                {
                                    return (T)(object)jsonElem.GetRawText();
                                }
                                // Для всех прочих типов используем стандартный ToString().
                                return (T)(object)jsonElem.ToString();
                            }
                            
                            // fallback-десериализация для сложных типов
                            var deserialized = jsonElem.Deserialize<T>();
                            if (deserialized != null)
                            {
                                return deserialized;
                            }
                        }
                        else if (val is T typedVal)
                        {
                            return typedVal;
                        }
                        else
                        {
                            return (T)Convert.ChangeType(val, typeof(T));
                        }
                    }
                    catch (Exception)
                    {
                        // При ошибке приведения возвращаем дефолт
                    }
                }
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// Записать значение настройки.
    /// </summary>
    public void SetSetting<T>(string group, string key, T value)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(group, out var groupDict))
            {
                groupDict = new Dictionary<string, object>();
                _cache[group] = groupDict;
            }

            if (value == null)
            {
                groupDict.Remove(key);
            }
            else
            {
                groupDict[key] = value;
            }

            _logService.Info($"Изменён параметр [{group}/{key}] -> '{value}'", "SettingsManager");
            SaveSettings();
        }
    }

    /// <summary>
    /// Инициализировать настройки по умолчанию на основе схемы скрипта.
    /// </summary>
    public void InitializeDefaults(List<AbstractScript> scripts)
    {
        lock (_lock)
        {
            bool modified = false;

            // Общие глобальные настройки
            if (!HasSetting("General", "OverwriteExisting"))
            {
                SetSettingInternal("General", "OverwriteExisting", false);
                modified = true;
            }
            if (!HasSetting("General", "DefaultOutputSubfolder"))
            {
                SetSettingInternal("General", "DefaultOutputSubfolder", "KTools_Result");
                modified = true;
            }
            if (!HasSetting("General", "UseAutoSubfolder"))
            {
                SetSettingInternal("General", "UseAutoSubfolder", false);
                modified = true;
            }
            if (!HasSetting("General", "Theme"))
            {
                SetSettingInternal("General", "Theme", "Dark");
                modified = true;
            }
            if (!HasSetting("General", "BackdropType"))
            {
                SetSettingInternal("General", "BackdropType", "Mica");
                modified = true;
            }
            if (!HasSetting("General", "MaxParallelTasks"))
            {
                SetSettingInternal("General", "MaxParallelTasks", Math.Max(1, Environment.ProcessorCount / 2));
                modified = true;
            }
            if (!HasSetting("General", "EnableParallel"))
            {
                SetSettingInternal("General", "EnableParallel", true);
                modified = true;
            }
            if (!HasSetting("General", "ClearListOnAdd"))
            {
                SetSettingInternal("General", "ClearListOnAdd", false);
                modified = true;
            }
            if (!HasSetting("General", "RenameEnableRegex"))
            {
                SetSettingInternal("General", "RenameEnableRegex", false);
                modified = true;
            }
            if (!HasSetting("General", "RenameRegexSearch"))
            {
                SetSettingInternal("General", "RenameRegexSearch", string.Empty);
                modified = true;
            }
            if (!HasSetting("General", "RenameRegexReplace"))
            {
                SetSettingInternal("General", "RenameRegexReplace", string.Empty);
                modified = true;
            }
            if (!HasSetting("General", "RenameUseRegex"))
            {
                SetSettingInternal("General", "RenameUseRegex", true);
                modified = true;
            }
            if (!HasSetting("General", "RenameCaseSensitive"))
            {
                SetSettingInternal("General", "RenameCaseSensitive", false);
                modified = true;
            }

            // Настройки логирования
            if (!HasSetting("Logging", "ShowLogsTab"))
            {
                SetSettingInternal("Logging", "ShowLogsTab", false);
                modified = true;
            }
            if (!HasSetting("Logging", "LogDir"))
            {
                SetSettingInternal("Logging", "LogDir", string.Empty);
                modified = true;
            }

            // Настройки обновлений
            if (!HasSetting("Updates", "AutoCheckUpdates"))
            {
                SetSettingInternal("Updates", "AutoCheckUpdates", true);
                modified = true;
            }
            if (!HasSetting("Updates", "IncludePreReleases"))
            {
                SetSettingInternal("Updates", "IncludePreReleases", IsPreviewBuild);
                modified = true;
            }
            if (!HasSetting("Debug", "DebugSimulateOldVersion"))
            {
                SetSettingInternal("Debug", "DebugSimulateOldVersion", false);
                modified = true;
            }

            // Инициализация настроек для каждого скрипта
            foreach (var script in scripts)
            {
                string groupName = GetSafeGroupName(script.Name);
                foreach (var field in script.SettingsSchema)
                {
                    if (field.Type == SettingType.Subtitle)
                    {
                        continue;
                    }

                    if (!HasSetting(groupName, field.Key))
                    {
                        SetSettingInternal(groupName, field.Key, field.DefaultValue);
                        modified = true;
                    }
                }

                // Нормализация настроек: удаление лишних ключей, которых нет в схеме
                if (_cache.TryGetValue(groupName, out var groupDict))
                {
                    var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in script.SettingsSchema)
                    {
                        if (field.Type != SettingType.Subtitle)
                        {
                            validKeys.Add(field.Key);
                        }
                    }

                    var keysToRemove = new List<string>();
                    foreach (var key in groupDict.Keys)
                    {
                        if (!validKeys.Contains(key))
                        {
                            keysToRemove.Add(key);
                        }
                    }

                    if (keysToRemove.Count > 0)
                    {
                        foreach (var key in keysToRemove)
                        {
                            groupDict.Remove(key);
                            _logService.Info($"[Нормализация] Удален некорректный параметр [{groupName}/{key}]", "SettingsManager");
                        }
                        modified = true;
                    }
                }
            }


            if (modified)
            {
                _logService.Warn("Выполнена инициализация настроек по умолчанию", "SettingsManager");
                SaveSettings();
            }
        }
    }

    private bool HasSetting(string group, string key)
    {
        return _cache.TryGetValue(group, out var groupDict) &&
               groupDict.ContainsKey(key);
    }

    private void SetSettingInternal(string group, string key, object value)
    {
        if (!_cache.TryGetValue(group, out var groupDict))
        {
            groupDict = new Dictionary<string, object>();
            _cache[group] = groupDict;
        }
        groupDict[key] = value;
    }

    /// <summary>
    /// Нормализовать имя скрипта для использования в качестве имени секции (группы) JSON.
    /// </summary>
    public string GetSafeGroupName(string scriptName)
    {
        // Нормализация имени группы
        return "Script_" + scriptName
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(" ", "_")
            .Replace("→", "_");
    }
}

/// <summary>
/// Представляет один элемент шаблона (регулярного выражения или переменной автозамены) с описанием.
/// </summary>
public class TemplateItem
{
    /// <summary>
    /// Шаблон регулярного выражения или переменная.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Русское описание назначения шаблона.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
