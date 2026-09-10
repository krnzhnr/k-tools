// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Converters;

namespace KTools_App.Tests.Converters;

/// <summary>
/// Юнит-тесты конвертера InverseBoolConverter.
/// Проверяют инверсию булевых значений в обе стороны,
/// а также поведение при null, неожиданных типах и мусорных параметрах.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class InverseBoolConverterTests
{
    private readonly InverseBoolConverter _converter = new();

    /// <summary>
    /// Проверяет, что true инвертируется в false.
    /// </summary>
    [TestMethod]
    public void Convert_TrueValue_ReturnsFalse()
    {
        // Arrange
        object input = true;

        // Act
        var result = (bool)_converter.Convert(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что false инвертируется в true.
    /// </summary>
    [TestMethod]
    public void Convert_FalseValue_ReturnsTrue()
    {
        // Arrange
        object input = false;

        // Act
        var result = (bool)_converter.Convert(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что null инвертируется в true (fallback-значение для не-bool).
    /// </summary>
    [TestMethod]
    public void Convert_NullValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.Convert(null!, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что строка вместо bool инвертируется в true без исключений.
    /// </summary>
    [TestMethod]
    public void Convert_StringValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.Convert("false", typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что число вместо bool инвертируется в true.
    /// </summary>
    [TestMethod]
    public void Convert_IntValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.Convert(1, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что произвольный объект инвертируется в true.
    /// </summary>
    [TestMethod]
    public void Convert_ObjectValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.Convert(new object(), typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что конвертер игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    public void Convert_GarbageParameterAndNullTargetType_ReturnsFalseForTrue()
    {
        // Arrange
        object input = true;

        // Act
        var result = (bool)_converter.Convert(input, null!, "мусор", null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что true в ConvertBack инвертируется в false.
    /// </summary>
    [TestMethod]
    public void ConvertBack_TrueValue_ReturnsFalse()
    {
        // Arrange
        object input = true;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что false в ConvertBack инвертируется в true.
    /// </summary>
    [TestMethod]
    public void ConvertBack_FalseValue_ReturnsTrue()
    {
        // Arrange
        object input = false;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что null в ConvertBack возвращает false (fallback для не-bool).
    /// </summary>
    [TestMethod]
    public void ConvertBack_NullValue_ReturnsFalse()
    {
        // Act
        var result = (bool)_converter.ConvertBack(null!, typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что неожидаемый тип в ConvertBack возвращает false.
    /// </summary>
    [TestMethod]
    public void ConvertBack_StringValue_ReturnsFalse()
    {
        // Act
        var result = (bool)_converter.ConvertBack("true", typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет round-trip: двойная инверсия возвращает исходное значение.
    /// </summary>
    [TestMethod]
    public void ConvertAndBack_RoundTrip_PreservesOriginalValue()
    {
        // Arrange
        bool original = true;

        // Act
        var once = (bool)_converter.Convert(original, typeof(bool), null!, null!);
        var twice = (bool)_converter.ConvertBack(once, typeof(bool), null!, null!);

        // Assert
        twice.Should().Be(original, "двойная инверсия должна возвращать исходное значение");
    }
}
