// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Converters;
using KTools_App.Core;
using Microsoft.UI.Xaml;

namespace KTools_App.Tests.Converters;

/// <summary>
/// Юнит-тесты конвертера BoolToVisibilityConverter.
/// Проверяют прямое и обратное преобразование bool в Visibility,
/// а также устойчивость к null, неожиданным типам и мусорным параметрам.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class BoolToVisibilityConverterTests
{
    private readonly BoolToVisibilityConverter _converter = new();

    /// <summary>
    /// Проверяет, что true преобразуется в Visible.
    /// </summary>
    [TestMethod]
    public void Convert_TrueValue_ReturnsVisible()
    {
        // Arrange
        object input = true;

        // Act
        var result = (Visibility)_converter.Convert(input, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что false преобразуется в Collapsed.
    /// </summary>
    [TestMethod]
    public void Convert_FalseValue_ReturnsCollapsed()
    {
        // Arrange
        object input = false;

        // Act
        var result = (Visibility)_converter.Convert(input, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что null-значение безопасно преобразуется в Collapsed.
    /// </summary>
    [TestMethod]
    public void Convert_NullValue_ReturnsCollapsed()
    {
        // Act
        var result = (Visibility)_converter.Convert(null!, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что строка вместо bool преобразуется в Collapsed без исключений.
    /// </summary>
    [TestMethod]
    public void Convert_StringValue_ReturnsCollapsed()
    {
        // Act
        var result = (Visibility)_converter.Convert("true", typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что число вместо bool преобразуется в Collapsed без исключений.
    /// </summary>
    [TestMethod]
    public void Convert_IntValue_ReturnsCollapsed()
    {
        // Act
        var result = (Visibility)_converter.Convert(42, typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что произвольный объект вместо bool преобразуется в Collapsed.
    /// </summary>
    [TestMethod]
    public void Convert_ObjectValue_ReturnsCollapsed()
    {
        // Act
        var result = (Visibility)_converter.Convert(new object(), typeof(Visibility), null!, null!);

        // Assert
        result.Should().Be(Visibility.Collapsed);
    }

    /// <summary>
    /// Проверяет, что конвертер игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    public void Convert_GarbageParameterAndNullTargetType_ReturnsVisibleForTrue()
    {
        // Arrange
        object input = true;

        // Act
        var result = (Visibility)_converter.Convert(input, null!, "мусорный параметр", string.Empty);

        // Assert
        result.Should().Be(Visibility.Visible);
    }

    /// <summary>
    /// Проверяет, что Visible обратно преобразуется в true.
    /// </summary>
    [TestMethod]
    public void ConvertBack_VisibleValue_ReturnsTrue()
    {
        // Arrange
        object input = Visibility.Visible;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что Collapsed обратно преобразуется в false.
    /// </summary>
    [TestMethod]
    public void ConvertBack_CollapsedValue_ReturnsFalse()
    {
        // Arrange
        object input = Visibility.Collapsed;

        // Act
        var result = (bool)_converter.ConvertBack(input, typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что null в ConvertBack преобразуется в false без исключений.
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
    /// Проверяет, что неожидаемый тип (строка) в ConvertBack преобразуется в false.
    /// </summary>
    [TestMethod]
    public void ConvertBack_StringValue_ReturnsFalse()
    {
        // Act
        var result = (bool)_converter.ConvertBack("Visible", typeof(bool), null!, null!);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет, что ConvertBack игнорирует мусорный параметр и targetType=null.
    /// </summary>
    [TestMethod]
    public void ConvertBack_GarbageParameterAndNullTargetType_ReturnsTrueForVisible()
    {
        // Arrange
        object input = Visibility.Visible;

        // Act
        var result = (bool)_converter.ConvertBack(input, null!, "мусор", null!);

        // Assert
        result.Should().BeTrue();
    }
}
