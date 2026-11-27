// -*- coding: utf-8 -*-
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Dispatching;

namespace KTools_App.ViewModels;

/// <summary>
/// Базовый класс для моделей представления (ViewModel), обеспечивающий потокобезопасную
/// отправку уведомлений об изменении свойств (PropertyChanged) в поток пользовательского интерфейса (UI).
/// Очередь диспетчера не захватывается в конструкторе, а резолвится лениво через главное окно
/// при каждом уведомлении, поэтому модель безопасно создавать в фоновом потоке.
/// Все комментарии выполнены исключительно на русском языке в соответствии с регламентом.
/// </summary>
public abstract class ThreadSafeViewModel : ObservableObject
{
    /// <summary>
    /// Вызывает событие PropertyChanged. Если вызов происходит не в потоке UI,
    /// он автоматически маршалируется через очередь диспетчера главного окна.
    /// Без окна уведомление выполняется синхронно в вызывающем потоке.
    /// </summary>
    /// <param name="e">Аргументы события PropertyChanged.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        DispatcherQueue? dispatcher = ResolveDispatcherQueue();
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            base.OnPropertyChanged(e);
            return;
        }

        if (!dispatcher.TryEnqueue(() => base.OnPropertyChanged(e)))
        {
            base.OnPropertyChanged(e);
        }
    }

    /// <summary>
    /// Лениво резолвит очередь диспетчера UI-потока через главное окно.
    /// Возвращает null вне XAML-окружения (фоновые потоки, юнит-тесты).
    /// </summary>
    private static DispatcherQueue? ResolveDispatcherQueue()
    {
        try
        {
            return App.CurrentMainWindow?.DispatcherQueue;
        }
        catch
        {
            return null;
        }
    }
}
