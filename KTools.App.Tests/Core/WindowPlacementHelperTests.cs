// -*- coding: utf-8 -*-
using System;
using FluentAssertions;
using KTools_App.Core;
using Microsoft.UI.Windowing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Core;

/// <summary>
/// Модульные тесты для вспомогательного класса <see cref="WindowPlacementHelper"/>.
/// Проверяют чистую логику позиционирования, ограничения в рабочей области экрана
/// и обработки состояний развернутости/сворачивания окна.
/// Все комментарии и описания выполнены строго на русском языке.
/// </summary>
[TestClass]
public class WindowPlacementHelperTests
{
    [TestMethod]
    public void HasValidPosition_WithUnspecifiedCoordinates_ReturnsFalse()
    {
        // Arrange
        int unspec = WindowPlacementHelper.UnspecifiedPosition;

        // Act & Assert
        WindowPlacementHelper.HasValidPosition(unspec, unspec).Should().BeFalse("обе координаты не заданы");
        WindowPlacementHelper.HasValidPosition(100, unspec).Should().BeFalse("координата Y не задана");
        WindowPlacementHelper.HasValidPosition(unspec, 200).Should().BeFalse("координата X не задана");
    }

    [TestMethod]
    public void HasValidPosition_WithValidCoordinates_ReturnsTrue()
    {
        // Act & Assert
        WindowPlacementHelper.HasValidPosition(0, 0).Should().BeTrue("координаты (0, 0) валидны");
        WindowPlacementHelper.HasValidPosition(1920, 1080).Should().BeTrue("положительные координаты валидны");
        WindowPlacementHelper.HasValidPosition(-1920, 0).Should().BeTrue("отрицательные координаты для второго монитора слева валидны");
    }

    [TestMethod]
    public void ClampToWorkArea_WhenWindowIsFullyInsideWorkArea_ReturnsOriginalParameters()
    {
        // Arrange — окно 800x600 на координатах (100, 100) внутри экрана 1920x1080
        int targetX = 100;
        int targetY = 100;
        int targetWidth = 800;
        int targetHeight = 600;

        int workX = 0;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1040; // без панели задач

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert
        result.X.Should().Be(100);
        result.Y.Should().Be(100);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ClampToWorkArea_WhenWindowExceedsRightBoundary_ClampsX()
    {
        // Arrange — окно 800x600 с X=1500 на экране шириной 1920: 1500+800=2300 > 1920
        int targetX = 1500;
        int targetY = 100;
        int targetWidth = 800;
        int targetHeight = 600;

        int workX = 0;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1080;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert — maxX = 1920 - 800 = 1120
        result.X.Should().Be(1120);
        result.Y.Should().Be(100);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ClampToWorkArea_WhenWindowExceedsBottomBoundary_ClampsY()
    {
        // Arrange — окно 800x600 с Y=700 на экране высотой 1000: 700+600=1300 > 1000
        int targetX = 100;
        int targetY = 700;
        int targetWidth = 800;
        int targetHeight = 600;

        int workX = 0;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1000;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert — maxY = 1000 - 600 = 400
        result.X.Should().Be(100);
        result.Y.Should().Be(400);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ClampToWorkArea_WhenWindowExceedsLeftOrTopBoundary_ClampsToWorkAreaOrigin()
    {
        // Arrange — окно левее и выше рабочей области
        int targetX = -200;
        int targetY = -50;
        int targetWidth = 800;
        int targetHeight = 600;

        int workX = 0;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1080;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert — клампится к началу рабочей области
        result.X.Should().Be(0);
        result.Y.Should().Be(0);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ClampToWorkArea_WhenWindowIsLargerThanWorkArea_ShrinksToWorkAreaSize()
    {
        // Arrange — окно 2560x1440 на экране 1920x1080
        int targetX = 50;
        int targetY = 50;
        int targetWidth = 2560;
        int targetHeight = 1440;

        int workX = 0;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1080;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert — размеры окна урезаются до размеров монитора, X и Y выравниваются
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.X.Should().Be(0);
        result.Y.Should().Be(0);
    }

    [TestMethod]
    public void ClampToWorkArea_OnSecondaryMonitorWithOffset_ClampsCorrectly()
    {
        // Arrange — второй монитор справа: X от 1920 до 3840
        int workX = 1920;
        int workY = 0;
        int workWidth = 1920;
        int workHeight = 1080;

        int targetX = 3500; // слишком далеко вправо: 3500+800=4300 > 3840
        int targetY = 200;
        int targetWidth = 800;
        int targetHeight = 600;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            workX, workY, workWidth, workHeight);

        // Assert — maxX = 1920 + 1920 - 800 = 3040
        result.X.Should().Be(3040);
        result.Y.Should().Be(200);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ClampToWorkArea_WithZeroOrNegativeWorkArea_ReturnsOriginalParameters()
    {
        // Arrange — некорректная рабочая область
        int targetX = 100;
        int targetY = 100;
        int targetWidth = 800;
        int targetHeight = 600;

        // Act
        var result = WindowPlacementHelper.ClampToWorkArea(
            targetX, targetY, targetWidth, targetHeight,
            0, 0, 0, 0);

        // Assert — безопасный возврат исходных параметров
        result.X.Should().Be(100);
        result.Y.Should().Be(100);
        result.Width.Should().Be(800);
        result.Height.Should().Be(600);
    }

    [TestMethod]
    public void ShouldUpdateNormalBounds_Restored_ReturnsTrue()
    {
        // Act & Assert
        WindowPlacementHelper.ShouldUpdateNormalBounds(OverlappedPresenterState.Restored)
            .Should().BeTrue("в обычном режиме нормальные габариты должны обновляться");
    }

    [TestMethod]
    public void ShouldUpdateNormalBounds_MaximizedOrMinimized_ReturnsFalse()
    {
        // Act & Assert
        WindowPlacementHelper.ShouldUpdateNormalBounds(OverlappedPresenterState.Maximized)
            .Should().BeFalse("в развернутом режиме нормальные размеры не должны затираться");

        WindowPlacementHelper.ShouldUpdateNormalBounds(OverlappedPresenterState.Minimized)
            .Should().BeFalse("в свернутом режиме нормальные размеры не должны затираться координатами -32000");
    }

    [TestMethod]
    public void ResolveIsMaximized_StateChanges_ResolvesExpectedValue()
    {
        // Act & Assert
        // При переходе в Maximized -> всегда true
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Maximized, previousIsMaximized: false)
            .Should().BeTrue();
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Maximized, previousIsMaximized: true)
            .Should().BeTrue();

        // При переходе в Restored -> всегда false
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Restored, previousIsMaximized: true)
            .Should().BeFalse();
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Restored, previousIsMaximized: false)
            .Should().BeFalse();

        // При переходе в Minimized -> сохраняет предыдущее значение
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Minimized, previousIsMaximized: true)
            .Should().BeTrue("если окно было развернуто до сворачивания, флаг сохраняется");
        WindowPlacementHelper.ResolveIsMaximized(OverlappedPresenterState.Minimized, previousIsMaximized: false)
            .Should().BeFalse("если окно было обычным до сворачивания, флаг сохраняется");
    }
}
