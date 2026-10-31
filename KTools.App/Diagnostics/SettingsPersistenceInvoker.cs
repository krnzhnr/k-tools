using System;

using KTools_App.Services.Contracts;

namespace KTools_App.Diagnostics;

/// <summary>
/// Мост между типизированным <see cref="PersistenceResult"/> и моделью завершения
/// приложения. Разбор строк и рефлексия удалены: единственный поддерживаемый
/// источник результата — типизированный контракт persistence-слоя.
/// </summary>
public static class SettingsPersistenceInvoker
{
    /// <summary>
    /// Преобразовать типизированный результат сохранения настроек в результат завершения.
    /// Успех возвращается только при подтверждённой записи на диск.
    /// </summary>
    /// <param name="result">Результат persistence-слоя (может быть null).</param>
    /// <returns>Результат для политики завершения приложения.</returns>
    public static SettingsPersistenceResult ReadResult(PersistenceResult? result)
    {
        if (result == null)
        {
            return SettingsPersistenceResult.Failure(PersistenceErrorCodes.NoResult);
        }

        if (result.IsSuccess && result.Persisted)
        {
            return SettingsPersistenceResult.Success;
        }

        return SettingsPersistenceResult.Failure(
            string.IsNullOrEmpty(result.ErrorCode) ? PersistenceErrorCodes.NoResult : result.ErrorCode);
    }
}
