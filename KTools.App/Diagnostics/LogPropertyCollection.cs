// -*- coding: utf-8 -*-
using System;
using System.Collections;
using System.Collections.Generic;

namespace KTools_App.Diagnostics;

/// <summary>
/// Неизменяемый снимок свойств события журнала, упорядоченный по ключу (StringComparer.Ordinal).
/// Снимок создаётся один раз на producer-пути сразу после redaction и дальше только читается:
/// файловый канал сериализует его, а все подписчики получают один и тот же экземпляр.
/// Это убирает копирование словаря на каждого подписчика и на каждого подписчика в цепочке,
/// сохраняя детерминированный (отсортированный) порядок свойств в JSONL.
/// </summary>
public sealed class LogPropertyCollection : IReadOnlyDictionary<string, object?>
{
    /// <summary>
    /// Разделяемый пустой снимок: события без свойств не создают новых объектов.
    /// </summary>
    public static LogPropertyCollection Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<object?>());

    private readonly string[] _keys;
    private readonly object?[] _values;

    private LogPropertyCollection(string[] keys, object?[] values)
    {
        _keys = keys;
        _values = values;
    }

    public int Count => _keys.Length;

    public IEnumerable<string> Keys
    {
        get
        {
            string[] keys = _keys;
            for (int index = 0; index < keys.Length; index++)
            {
                yield return keys[index];
            }
        }
    }

    public IEnumerable<object?> Values
    {
        get
        {
            object?[] values = _values;
            for (int index = 0; index < values.Length; index++)
            {
                yield return values[index];
            }
        }
    }

    public object? this[string key]
    {
        get
        {
            int index = IndexOf(key);
            return index < 0 ? throw new KeyNotFoundException(key) : _values[index];
        }
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public bool TryGetValue(string key, out object? value)
    {
        int index = IndexOf(key);
        if (index < 0)
        {
            value = null;
            return false;
        }

        value = _values[index];
        return true;
    }

    /// <summary>
    /// Перечисление без выделения памяти для внутренних потребителей логгера.
    /// </summary>
    internal Enumerator GetEnumerator() => new(_keys, _values);

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() =>
        new Enumerator(_keys, _values);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_keys, _values);

    /// <summary>
    /// Строит снимок из подготовленного словаря, сохраняя порядок StringComparer.Ordinal.
    /// </summary>
    public static LogPropertyCollection Create(Dictionary<string, object?> staged)
    {
        if (staged is null || staged.Count == 0)
        {
            return Empty;
        }

        int count = staged.Count;
        string[] keys = new string[count];
        object?[] values = new object?[count];
        int index = 0;
        foreach (KeyValuePair<string, object?> pair in staged)
        {
            keys[index] = pair.Key;
            values[index] = pair.Value;
            index++;
        }

        Sort(keys, values);
        return new LogPropertyCollection(keys, values);
    }

    /// <summary>
    /// Возвращает готовый неизменяемый снимок: уже иммутабельный источник переиспользуется,
    /// иначе содержимое копируется один раз.
    /// </summary>
    public static LogPropertyCollection Freeze(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is LogPropertyCollection snapshot)
        {
            return snapshot;
        }

        if (source is null || source.Count == 0)
        {
            return Empty;
        }

        Dictionary<string, object?> staged = new(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in source)
        {
            staged[pair.Key] = pair.Value;
        }

        return Create(staged);
    }

    /// <summary>
    /// Снимок на основе существующего снимка и дополнительных пар.
    /// Используется для маркеров усечения исключения, которые добавляются после redaction.
    /// </summary>
    public static LogPropertyCollection WithEntries(
        IReadOnlyDictionary<string, object?> source,
        string firstKey,
        object? firstValue,
        string secondKey,
        object? secondValue,
        string thirdKey,
        object? thirdValue)
    {
        Dictionary<string, object?> staged = new(
            source.Count + 3,
            StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in source)
        {
            staged[pair.Key] = pair.Value;
        }

        staged[firstKey] = firstValue;
        staged[secondKey] = secondValue;
        staged[thirdKey] = thirdValue;
        return Create(staged);
    }

    private static void Sort(string[] keys, object?[] values)
    {
        for (int index = 1; index < keys.Length; index++)
        {
            string key = keys[index];
            object? value = values[index];
            int target = index - 1;
            while (target >= 0 && string.CompareOrdinal(keys[target], key) > 0)
            {
                keys[target + 1] = keys[target];
                values[target + 1] = values[target];
                target--;
            }

            keys[target + 1] = key;
            values[target + 1] = value;
        }
    }

    private int IndexOf(string key)
    {
        string[] keys = _keys;
        for (int index = 0; index < keys.Length; index++)
        {
            if (string.Equals(keys[index], key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal struct Enumerator : IEnumerator<KeyValuePair<string, object?>>
    {
        private readonly string[] _keys;
        private readonly object?[] _values;
        private int _index;

        internal Enumerator(string[] keys, object?[] values)
        {
            _keys = keys;
            _values = values;
            _index = -1;
        }

        public KeyValuePair<string, object?> Current => new(_keys[_index], _values[_index]);

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _keys.Length)
            {
                _index = _keys.Length;
                return false;
            }

            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}
