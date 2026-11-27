// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;

namespace KTools_App.Encoders;

/// <summary>
/// Общий резолвер подкодека по настройке выбора кодека.
/// Одинаковый метод GetCapabilityProvider раньше дублировался
/// в NvencEncoder и CpuEncoder. Ключи настроек не меняются —
/// каждый энкодер передаёт свой ключ, свою карту и свой дефолт.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class EncoderCapabilitySelector
{
    /// <summary>
    /// Возвращает возможности подкодека по значению настройки.
    /// Неизвестное или отсутствующее значение даёт дефолт,
    /// поэтому старые конфигурации без ключа продолжают работать.
    /// </summary>
    /// <param name="settings">Словарь настроек запуска.</param>
    /// <param name="settingKey">Ключ настройки выбора кодека.</param>
    /// <param name="capabilitiesMap">Карта «имя кодека — возможности».</param>
    /// <param name="defaultCapabilities">Возможности по умолчанию.</param>
    public static TCapabilities Resolve<TCapabilities>(
        Dictionary<string, object>? settings,
        string settingKey,
        IReadOnlyDictionary<string, TCapabilities> capabilitiesMap,
        TCapabilities defaultCapabilities)
        where TCapabilities : class
    {
        if (settings != null && settings.TryGetValue(settingKey, out object? codecObj) && codecObj != null)
        {
            string codecStr = SettingValue.AsString(codecObj, string.Empty);
            if (capabilitiesMap.TryGetValue(codecStr, out TCapabilities? cap) && cap != null)
            {
                return cap;
            }
        }
        return defaultCapabilities;
    }
}
