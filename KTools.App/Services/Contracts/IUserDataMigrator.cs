// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Одноразовая миграция пользовательских данных из каталога установки
/// в управляемый каталог %LocalAppData%\KTools (ишью 46).
/// Фаза A выполняется синхронно до создания SettingsManager,
/// фаза B — в фоне после запуска, когда доступны опросы версий.
/// Обе фазы обязаны выполняться на одном экземпляре: фаза B дожидается
/// фонового копирования, запущенного фазой A.
/// </summary>
public interface IUserDataMigrator
{
    /// <summary>
    /// Фаза A: копирует settings.json с проверкой хэша, перемещает bin
    /// (или запускает фоновое копирование между томами). При любом сбое
    /// включает legacy-режим PathManager: приложение стартует «по-старому».
    /// </summary>
    void EnsureMigrated();

    /// <summary>
    /// Дешёвая проверка (один маленький файл): требуется ли фазе B что-то делать.
    /// Вызывается перед опросом версий, чтобы не запускать процессы впустую.
    /// </summary>
    /// <returns>True, если перенос выполнен, но проверка/чистка ещё не завершены.</returns>
    bool NeedsVerification();

    /// <summary>
    /// Фаза B: дожидается фонового копирования, опрашивает версии бинарников
    /// по новым путям и только при полном успехе удаляет оригиналы
    /// из каталога установки.
    /// </summary>
    /// <param name="probes">Результаты опроса версий по текущим путям.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task VerifyAndCleanupAsync(IReadOnlyList<BinaryProbeResult> probes, CancellationToken cancellationToken = default);
}
