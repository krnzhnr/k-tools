// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Converters;
using Microsoft.UI.Xaml;

namespace KTools_App.Tests.Converters;

/// <summary>
/// Юнит-тесты конвертера InverseBoolToVisibilityConverter.
/// Проверяют инверсную логику преобразования bool в Visibility:
/// true → Collapsed, false → Visible — и обратное преобразование.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class InverseBoolToVisibilityConverterTests
{
    private readonly InverseBoolToVisibilityConverter _converter = new();

    /// <summary>
    /// Проверяет, что true преобразуется в Collapsed (элемент скрыт).
    /// </summary>
    [TestMethod]
    public void Convert_TrueValue_ReturnsCollapsed()
    {
        // Arrange
        object input = true;

        // Act
        var result = (Visibility)_converter.Convert(input, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что false преобразуется в Visible (элемент виден).
    /// </summary>
    [TestMethod]
    public void Convert_FalseValue_ReturnsVisible()
    {
        // Arrange
        object input = false;

        // Act
        var result = (Visibility)_converter.Convert(input, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что null преобразуется в Visible (fallback для не-bool).
    /// </summary>
    [TestMethod]
    public void Convert_NullValue_ReturnsVisible()
    {
        // Act
        var result = (Visibility)_converter.Convert(null!, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что строка вместо bool преобразуется в Visible.
    /// </summary>
    [TestMethod]
    public void Convert_StringValue_ReturnsVisible()
    {
        // Act
        var result = (Visibility)_converter.Convert("true", typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что число вместо bool преобразуется в Visible.
    /// </summary>
    [TestMethod]
    public void Convert_IntValue_ReturnsVisible()
    {
        // Act
        var result = (Visibility)_converter.Convert(0, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что произвольный объект преобразуется в Visible.
    /// </summary>
    [TestMethod]
    public void Convert_ObjectValue_ReturnsVisible()
    {
        // Act
        var result = (Visibility)_converter.Convert(new object(), typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что конвертер игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    public void Convert_GarbageParameterAndNullTargetType_ReturnsCollapsedForTrue()
    {
        // Arrange
        object input = true;

        // Act
        var result = (Visibility)_converter.Convert(input, null!, "мусор", string.Empty);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что Visible в ConvertBack возвращает false (инверсия).
    /// </summary>
    [TestMethod]
    public void ConvertBack_VisibleValue_ReturnsFalse()
    {
        // Arrange
        object input = Visibility.Visible;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что Collapsed в ConvertBack возвращает true (инверсия).
    /// </summary>
    [TestMethod]
    public void ConvertBack_CollapsedValue_ReturnsTrue()
    {
        // Arrange
        object input = Visibility.Collapsed;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что null в ConvertBack возвращает true (fallback для не-Visibility).
    /// </summary>
    [TestMethod]
    public void ConvertBack_NullValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.ConvertBack(null!, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что неожидаемый тип (строка) в ConvertBack возвращает true.
    /// </summary>
    [TestMethod]
    public void ConvertBack_StringValue_ReturnsTrue()
    {
        // Act
        var result = (bool)_converter.ConvertBack("Collapsed", typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что ConvertBack игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    public void ConvertBack_GarbageParameterAndNullTargetType_ReturnsFalseForVisible()
    {
        // Arrange
        object input = Visibility.Visible;

        // Act
        var result = (bool)_converter.ConvertBack(input, null!, "мусор", null!);

        // Assert
        result.Should().BeFalse();
    }
}
