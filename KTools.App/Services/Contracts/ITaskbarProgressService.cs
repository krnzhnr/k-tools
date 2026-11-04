// -*- coding: utf-8 -*-
namespace KTools_App.Services.Contracts;

/// <summary>
/// Абстракция прогресса обработки в иконке на панели задач Windows.
/// Дублирует общий прогресс очереди в кнопке приложения через Win32 ITaskbarList3.
/// </summary>
public interface ITaskbarProgressService
{
    /// <summary>
    /// Показывает обычный (зелёный) прогресс выполнения очереди.
    /// </summary>
    /// <param name="percent">Общий прогресс очереди в процентах (0-100, вне диапазона обрезается).</param>
    void SetProgress(double percent);

    /// <summary>
    /// Показывает состояние ошибки (красный) с завершённым значением прогресса.
    /// Вызывается при завершении очереди с ошибками.
    /// </summary>
    /// <param name="percent">Итоговый прогресс в процентах (0-100, вне диапазона обрезается).</param>
    void SetError(double percent);

    /// <summary>
    /// Сбрасывает индикатор в обычное состояние (без прогресса).
    /// Вызывается при успешном завершении, отмене или пустой очереди.
    /// </summary>
    void Clear();
}
