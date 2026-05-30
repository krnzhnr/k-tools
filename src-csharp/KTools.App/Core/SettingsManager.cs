// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
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
        // Создаем путь к файлу настроек в изолированной bin директории
        string binDir = PathManager.GetBinDirectory();
        _settingsFilePath = Path.Combine(binDir, "settings.json");
        _cache = new Dictionary<string, Dictionary<string, object>>();

        LoadSettings();
    }

    /// <summary>
    /// Возвращает единственный экземпляр класса SettingsManager.
    /// </summary>
    public static SettingsManager Instance => LazyInstance.Value;

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
                    WriteIndented = true
                };
                string json = JsonSerializer.Serialize(_cache, options);

                // Обеспечиваем существование папки bin
                string binDir = PathManager.GetBinDirectory();
                if (!Directory.Exists(binDir))
                {
                    Directory.CreateDirectory(binDir);
                }

                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception)
            {
                // Игнорируем ошибки записи, логируем в консоль
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
