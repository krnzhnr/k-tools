// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;

namespace KTools_App.Core;

/// <summary>
/// Чистое сравнение состава динамической схемы настроек с построенным UI.
/// Универсальный механизм для всех скриптов и энкодеров: если смена настройки
/// (выбор кодека, тумблер) меняет НАБОР полей, интерфейс перестраивается целиком,
/// иначе пользователь редактирует чужие поля, а кодировщик читает дефолты своих.
/// Все комментарии выполнены исключительно на русском языке.
/// </summary>
public static class SettingsSchemaComparer
{
    /// <summary>
    /// Возвращает плоский набор ключей схемы, включая дочерние поля экспандеров.
    /// </summary>
    public static HashSet<string> GetFlatKeys(IEnumerable<SettingField> schema)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema)
        {
            keys.Add(field.Key);
            foreach (var child in field.ChildFields)
            {
                keys.Add(child.Key);
            }
        }
        return keys;
    }

    /// <summary>
    /// Проверяет, совпадает ли набор ключей схемы с набором уже построенных элементов.
    /// </summary>
    public static bool HasSameKeys(IEnumerable<SettingField> schema, IEnumerable<string> generatedKeys)
    {
        var schemaKeys = GetFlatKeys(schema);
        var generated = new HashSet<string>(generatedKeys, StringComparer.OrdinalIgnoreCase);
        return schemaKeys.SetEquals(generated);
    }
}
