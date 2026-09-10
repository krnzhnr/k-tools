// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Converters;
using KTools_App.Core;
using Microsoft.UI.Xaml.Media;

namespace KTools_App.Tests.Converters;

/// <summary>
/// Юнит-тесты конвертера LogLevelToBrushConverter.
/// ВАЖНО: конвертер создаёт пять статических SolidColorBrush в static initializer.
/// SolidColorBrush из Microsoft.UI.Xaml.Media — это WinRT/XAML-тип, для создания
/// которого требуется запущенная XAML-среда (Microsoft.UI.Xaml runtime). В headless
/// юнит-тестах (dotnet test без WinUI-приложения) обращение к классу вызывает
/// TypeInitializationException -> COMException (0x800401F0 / CO_E_NOT_SUPPORTED и т.п.),
/// эмпирически подтверждено прогоном dotnet test --filter ConvertersTests.
/// Поэтому все методы помечены [Ignore] до появления инфраструктуры XAML-хостинга.
/// Тесты активируются простым снятием атрибута [Ignore] при запуске в среде с XAML.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class LogLevelToBrushConverterTests
{
    private readonly LogLevelToBrushConverter _converter = new();

    /// <summary>
    /// Проверяет, что LogLevel.Debug возвращает кисть Debug (серый цвет 128,128,128).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_DebugLevel_ReturnsDebugBrush()
    {
        // Arrange
        object input = LogLevel.Debug;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        brush.Color.R.Should().Be(128);
        brush.Color.G.Should().Be(128);
        brush.Color.B.Should().Be(128);
        brush.Color.A.Should().Be(255);
    }

    /// <summary>
    /// Проверяет, что LogLevel.Info возвращает кисть Info (светло-серый цвет 220,220,220).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_InfoLevel_ReturnsInfoBrush()
    {
        // Arrange
        object input = LogLevel.Info;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        brush.Color.R.Should().Be(220);
        brush.Color.G.Should().Be(220);
        brush.Color.B.Should().Be(220);
    }

    /// <summary>
    /// Проверяет, что LogLevel.Warning возвращает кисть Warning (янтарный цвет 255,184,0).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_WarningLevel_ReturnsWarningBrush()
    {
        // Arrange
        object input = LogLevel.Warning;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        brush.Color.R.Should().Be(255);
        brush.Color.G.Should().Be(184);
        brush.Color.B.Should().Be(0);
    }

    /// <summary>
    /// Проверяет, что LogLevel.Error возвращает кисть Error (красный цвет 255,77,77).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_ErrorLevel_ReturnsErrorBrush()
    {
        // Arrange
        object input = LogLevel.Error;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        brush.Color.R.Should().Be(255);
        brush.Color.G.Should().Be(77);
        brush.Color.B.Should().Be(77);
    }

    /// <summary>
    /// Проверяет, что LogLevel.Fatal возвращает кисть Fatal (ярко-красный цвет 255,0,0).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_FatalLevel_ReturnsFatalBrush()
    {
        // Arrange
        object input = LogLevel.Fatal;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        brush.Color.R.Should().Be(255);
        brush.Color.G.Should().Be(0);
        brush.Color.B.Should().Be(0);
    }

    /// <summary>
    /// Проверяет, что для одного и того же уровня конвертер возвращает
    /// один и тот же экземпляр кисти (статическое кэширование).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_SameLevelTwice_ReturnsSameBrushInstance()
    {
        // Arrange
        object first = LogLevel.Error;
        object second = LogLevel.Error;

        // Act
        var brush1 = _converter.Convert(first, typeof(Brush), null!, null!);
        var brush2 = _converter.Convert(second, typeof(Brush), null!, null!);

        // Assert
        brush1.Should().BeSameAs(brush2, "кисти должны кэшироваться в статических полях для экономии памяти UI-потока");
    }

    /// <summary>
    /// Проверяет, что разные уровни возвращают разные экземпляры кистей.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_DifferentLevels_ReturnDifferentBrushInstances()
    {
        // Act
        var debugBrush = _converter.Convert(LogLevel.Debug, typeof(Brush), null!, null!);
        var errorBrush = _converter.Convert(LogLevel.Error, typeof(Brush), null!, null!);

        // Assert
        debugBrush.Should().NotBeSameAs(errorBrush);
    }

    /// <summary>
    /// Проверяет, что null-значение возвращает Info-кисть (fallback).
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_NullValue_ReturnsInfoBrush()
    {
        // Act
        var result = _converter.Convert(null!, typeof(Brush), null!, null!);

        // Assert
        result.Should().BeSameAs(_converter.Convert(LogLevel.Info, typeof(Brush), null!, null!),
            "для невалидных значений должен использоваться fallback на Info-кисть");
    }

    /// <summary>
    /// Проверяет, что неожидаемый тип (строка) возвращает Info-кисть.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_StringValue_ReturnsInfoBrush()
    {
        // Arrange
        object input = "Warning";

        // Act
        var result = _converter.Convert(input, typeof(Brush), null!, null!);

        // Assert
        result.Should().BeSameAs(_converter.Convert(LogLevel.Info, typeof(Brush), null!, null!));
    }

    /// <summary>
    /// Проверяет, что число вместо LogLevel возвращает Info-кисть.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_IntValue_ReturnsInfoBrush()
    {
        // Act
        var result = _converter.Convert(12345, typeof(Brush), null!, null!);

        // Assert
        result.Should().BeSameAs(_converter.Convert(LogLevel.Info, typeof(Brush), null!, null!));
    }

    /// <summary>
    /// Проверяет, что произвольный объект возвращает Info-кисть.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_ObjectValue_ReturnsInfoBrush()
    {
        // Act
        var result = _converter.Convert(new object(), typeof(Brush), null!, null!);

        // Assert
        result.Should().BeSameAs(_converter.Convert(LogLevel.Info, typeof(Brush), null!, null!));
    }

    /// <summary>
    /// Проверяет, что конвертер игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException).")]
    public void Convert_GarbageParameterAndNullTargetType_ReturnsFatalBrush()
    {
        // Arrange
        object input = LogLevel.Fatal;

        // Act
        var brush = (SolidColorBrush)_converter.Convert(input, null!, "мусор", string.Empty);

        // Assert
        brush.Color.R.Should().Be(255);
        brush.Color.G.Should().Be(0);
        brush.Color.B.Should().Be(0);
    }

    /// <summary>
    /// Проверяет, что ConvertBack бросает NotImplementedException с точным текстом.
    /// </summary>
    [TestMethod]
    [Ignore("Static initializer конвертера создаёт WinRT-кисти, что требует XAML-среды; в headless-тестах падает TypeInitializationException (COMException). Тело теста валидно и активируется снятием атрибута.")]
    public void ConvertBack_AnyValue_ThrowsNotImplementedExceptionWithExactMessage()
    {
        // Arrange
        object input = LogLevel.Info;

        // Act
        Action act = () => _converter.ConvertBack(input, typeof(LogLevel), null!, null!);

        // Assert
        act.Should().Throw<NotImplementedException>()
            .WithMessage("Обратное преобразование уровня логов в кисть не поддерживается.");
    }
}
