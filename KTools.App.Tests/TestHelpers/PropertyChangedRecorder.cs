// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Записанная пара: имя свойства и его значение в момент события PropertyChanged.
/// </summary>
/// <param name="PropertyName">Имя изменившегося свойства.</param>
/// <param name="Value">Значение свойства, прочитанное через рефлексию в момент события.</param>
public sealed record PropertyChangedEvent(string PropertyName, object? Value);

/// <summary>
/// Универсальный рекордер уведомлений INotifyPropertyChanged.
/// Фиксирует каждое событие PropertyChanged вместе со значением свойства
/// в момент генерации события — для глубоких проверок синхронизации UI-состояний.
/// </summary>
public sealed class PropertyChangedRecorder
{
    private readonly object _source;
    private readonly List<PropertyChangedEvent> _events = new();
    private readonly object _lock = new();

    /// <summary>
    /// Подписывается на уведомления об изменении свойств объекта.
    /// </summary>
    /// <param name="source">Отслеживаемый объект, реализующий INotifyPropertyChanged.</param>
    /// <exception cref="ArgumentNullException">source равен null.</exception>
    public PropertyChangedRecorder(INotifyPropertyChanged source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        source.PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// Все зафиксированные события в порядке поступления (потокобезопасный снимок).
    /// </summary>
    public IReadOnlyList<PropertyChangedEvent> AllEvents
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>
    /// Возвращает события только для указанного свойства.
    /// </summary>
    /// <param name="propertyName">Имя свойства.</param>
    /// <returns>Список событий указанного свойства.</returns>
    public IReadOnlyList<PropertyChangedEvent> GetEventsFor(string propertyName)
    {
        lock (_lock)
        {
            return _events.FindAll(e => e.PropertyName == propertyName);
        }
    }

    /// <summary>
    /// Отписывается от уведомлений объекта-источника.
    /// </summary>
    public void Detach()
    {
        ((INotifyPropertyChanged)_source).PropertyChanged -= OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        object? value = null;
        try
        {
            PropertyInfo? prop = sender?.GetType().GetProperty(e.PropertyName ?? string.Empty);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                value = prop.GetValue(sender);
            }
        }
        catch
        {
            // Значение недоступно — фиксируем только имя свойства
        }

        lock (_lock)
        {
            _events.Add(new PropertyChangedEvent(e.PropertyName ?? string.Empty, value));
        }
    }
}
