// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Models;

namespace KTools_App.Encoders.Capabilities;

/// <summary>
/// Базовый класс программных кодеков CPU. Содержит исключительно общие помощники
/// (чтение настроек, пустые входные аргументы, отсутствие тега контейнера по умолчанию).
/// Проверенные параметры и аргументы живут в наследниках — по одному файлу на кодек.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public abstract class BaseSoftwareCapabilities : ISoftwareCodecCapabilities
{
    public abstract string CodecName { get; }
    public abstract string FfmpegCodecName { get; }
    public abstract bool Supports10Bit { get; }

    public virtual string GetPixelFormat(bool force10Bit)
    {
        return (Supports10Bit && force10Bit) ? "yuv420p10le" : "yuv420p";
    }

    public abstract List<SettingField> GetCodecSettings(Dictionary<string, object>? currentSettings = null);

    public abstract List<string> BuildCodecArguments(Dictionary<string, object> settings, EncoderSharedContext context);

    public virtual Task<List<string>> BuildInputArgumentsAsync(MediaStructure mediaStructure, CancellationToken ct)
    {
        return Task.FromResult(new List<string>());
    }

    public virtual string? GetContainerTag(Dictionary<string, object> settings, EncoderSharedContext context)
    {
        return null;
    }

    /// <summary>
    /// Проверяет, включён ли режим Lossless в общих настройках скрипта.
    /// </summary>
    protected static bool IsLosslessSetting(Dictionary<string, object>? currentSettings)
    {
        if (currentSettings != null && currentSettings.TryGetValue("lossless", out var losslessObj))
        {
            return KTools_App.Core.SettingValue.AsBool(losslessObj, false);
        }
        return false;
    }

    /// <summary>
    /// Безопасно читает значение настройки через единую нормализацию
    /// <see cref="KTools_App.Core.SettingValue"/>: корректно обрабатывает значения,
    /// загруженные с диска (JsonElement), без расхождения с интерфейсом.
    /// </summary>
    protected static T GetSettingValue<T>(Dictionary<string, object> settings, string key, T defaultValue)
    {
        if (!settings.TryGetValue(key, out object? val) || val == null)
        {
            return defaultValue;
        }
        try
        {
            if (typeof(T) == typeof(int))
            {
                return (T)(object)KTools_App.Core.SettingValue.AsInt(val, (int)(object)defaultValue!);
            }
            if (typeof(T) == typeof(float))
            {
                return (T)(object)KTools_App.Core.SettingValue.AsFloat(val, (float)(object)defaultValue!);
            }
            if (typeof(T) == typeof(bool))
            {
                return (T)(object)KTools_App.Core.SettingValue.AsBool(val, (bool)(object)defaultValue!);
            }
            if (typeof(T) == typeof(string))
            {
                object? boxedDefault = defaultValue;
                return (T)(object)KTools_App.Core.SettingValue.AsString(val, boxedDefault as string);
            }
            return (T)Convert.ChangeType(val, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }
}
