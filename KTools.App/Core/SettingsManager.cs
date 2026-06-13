// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KTools_App.Core;

/// <summary>
/// Менеджер пользовательских настроек приложения K-Tools.
/// Сохраняет все параметры программы и настроек скриптов в локальный файл JSON.
/// Полностью потокобезопасен и поддерживает горячую синхронизацию.
/// </summary>
public sealed class SettingsManager
{
    private static readonly Lazy<SettingsManager> LazyInstance =
        new(() => new SettingsManager());

    private readonly object _lock = new();
    private readonly string _settingsFilePath;
    private Dictionary<string, Dictionary<string, object>> _cache;

    private SettingsManager()
    {
        // Создаем путь к файлу настроек в безопасной директории
        string settingsDir = PathManager.GetSettingsDirectory();
        _settingsFilePath = Path.Combine(settingsDir, "settings.json");
        _cache = new Dictionary<string, Dictionary<string, object>>();

        LoadSettings();
    }

    /// <summary>
    /// Возвращает единственный экземпляр класса SettingsManager.
    /// </summary>
    public static SettingsManager Instance => LazyInstance.Value;

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
        set => SetSetting("Logging", "LogDir", value);
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
    /// Включать ли бета-версии при поиске обновлений.
    /// </summary>
    public bool IncludePreReleases
    {
        get => GetSetting("Updates", "IncludePreReleases", false);
        set => SetSetting("Updates", "IncludePreReleases", value);
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
                LogService.Instance.DebugLog("Конфигурация успешно сохранена на диск", "SettingsManager");
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"Ошибка сохранения конфигурации на диск: {ex.Message}", "SettingsManager");
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
                                return (T)(object)jsonElem.GetString()!;
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

            LogService.Instance.Info($"Изменён параметр [{group}/{key}] -> '{value}'", "SettingsManager");
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
            if (!HasSetting("General", "ClearListOnAdd"))
            {
                SetSettingInternal("General", "ClearListOnAdd", false);
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
                SetSettingInternal("Updates", "IncludePreReleases", false);
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
            }

            if (modified)
            {
                LogService.Instance.Warn("Выполнена инициализация настроек по умолчанию", "SettingsManager");
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
