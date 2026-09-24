// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Services.Contracts;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Минимальная тестовая реализация скрипта обработки медиа на базе AbstractScript.
/// Позволяет тестировать логику базового класса и WorkPanelViewModel
/// без запуска реальных внешних процессов (ffmpeg и т.п.).
/// </summary>
public sealed class StubScript : AbstractScript
{
    /// <summary>
    /// Делегат обработки файла. Если не задан, используется <see cref="DefaultExecuteHandler"/>.
    /// </summary>
    public Func<string, Dictionary<string, object>, Task<List<string>>>? ExecuteHandler
    {
        get;
        set;
    }

    /// <summary>
    /// Необязательный обработчик прогресса: вызывается перед основным обработчиком
    /// и позволяет тестам эмитировать поток обновлений прогресса (проверки троттлинга).
    /// </summary>
    public Action<ScriptProgressCallback, int, int>? ProgressHandler
    {
        get;
        set;
    }

    /// <summary>
    /// Имя скрипта по умолчанию для StubScript.
    /// </summary>
    public const string DefaultName = "Тестовый скрипт";

    private readonly bool _supportsParallel;
    private readonly bool _useCustomWidget;
    private readonly string[] _dependencies;

    /// <summary>
    /// Инициализирует тестовый скрипт с настраиваемым поведением.
    /// </summary>
    /// <param name="logService">Сервис логирования.</param>
    /// <param name="settingsManager">Менеджер настроек.</param>
    /// <param name="pathManager">Менеджер путей.</param>
    /// <param name="supportsParallel">Поддержка параллельной обработки.</param>
    /// <param name="useCustomWidget">Использование кастомного виджета дорожек.</param>
    /// <param name="dependencies">Ключи требуемых внешних зависимостей.</param>
    public StubScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        bool supportsParallel = false,
        bool useCustomWidget = false,
        string[]? dependencies = null)
        : base(logService, settingsManager, pathManager)
    {
        _supportsParallel = supportsParallel;
        _useCustomWidget = useCustomWidget;
        _dependencies = dependencies ?? Array.Empty<string>();
    }

    /// <summary>Отображаемое имя скрипта.</summary>
    public override string Name => DefaultName;

    /// <summary>Описание назначения скрипта.</summary>
    public override string Description => "Тестовый скрипт для юнит-тестов";

    /// <summary>Категория скрипта.</summary>
    public override string Category => "Тесты";

    /// <summary>Имя иконки для отображения.</summary>
    public override string IconName => "TestIcon";

    /// <summary>Список поддерживаемых расширений файлов.</summary>
    public override string[] FileExtensions => new[] { ".mkv", ".mp4" };

    /// <summary>Поддержка параллельной обработки.</summary>
    public override bool SupportsParallel => _supportsParallel;

    /// <summary>Использование кастомного виджета выбора дорожек.</summary>
    public override bool UseCustomWidget => _useCustomWidget;

    /// <summary>Ключи требуемых внешних зависимостей.</summary>
    public override string[] RequiredDependencies => _dependencies;

    /// <summary>
    /// Выполняет обработку одного файла, delegируя настраиваемому обработчику.
    /// </summary>
    public override Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        ProgressHandler?.Invoke(progressCallback, fileIndex, totalCount);

        var handler = ExecuteHandler ?? DefaultExecuteHandler;
        return handler(filePath, settings);
    }

    private static Task<List<string>> DefaultExecuteHandler(
        string filePath,
        Dictionary<string, object> settings)
    {
        return Task.FromResult(new List<string> { $"✅ Готово: {filePath}" });
    }
}
