// -*- coding: utf-8 -*-
using FluentAssertions;
using KTools_App.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests;

/// <summary>
/// Гонка проб дорожек в AudioTransplantControl (ишью #90):
/// при быстром вводе пути две пробы без защиты складывают результаты в один список,
/// и опоздавшая проба показывает чужие дорожки при актуальном тексте поля.
/// Проверяется решающее правило, которым Populate*Async сверяет завершившуюся
/// пробу с актуальным состоянием после await.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public sealed class AudioTransplantProbeRaceTests
{
    [TestMethod]
    public void ShouldApplyProbeResult_StaleSequence_ReturnsFalse()
    {
        // Arrange: ввод A запустил пробу №1, ввод B — пробу №2; проба A вернулась последней.
        const int staleSeq = 1;
        const int currentSeq = 2;

        // Act.
        bool actual = AudioTransplantControl.ShouldApplyProbeResult(staleSeq, currentSeq, @"C:\Видео\a.mkv", @"C:\Видео\b.mkv");

        // Assert: опоздавший результат отбрасывается, список остаётся за путём B.
        actual.Should().BeFalse("опоздавшая проба не должна перезаписывать список актуального пути");
    }

    [TestMethod]
    public void ShouldApplyProbeResult_PathChangedAfterAwait_ReturnsFalse()
    {
        // Arrange: номер пробы совпал, но текст поля за время await уже изменился.

        // Act.
        bool actual = AudioTransplantControl.ShouldApplyProbeResult(2, 2, @"C:\Видео\a.mkv", @"C:\Видео\b.mkv");

        // Assert.
        actual.Should().BeFalse("результат чужого пути нельзя применять даже при совпадении номера пробы");
    }

    [TestMethod]
    public void ShouldApplyProbeResult_CurrentSequenceAndSamePath_ReturnsTrue()
    {
        // Arrange: последняя проба вернулась для актуального пути.

        // Act.
        bool actual = AudioTransplantControl.ShouldApplyProbeResult(2, 2, @"C:\Видео\b.mkv", @"C:\Видео\b.mkv");

        // Assert.
        actual.Should().BeTrue("актуальный результат обязан попадать в список");
    }

    [TestMethod]
    public void ShouldApplyProbeResult_PathsDifferOnlyByCaseAndSpaces_ReturnsTrue()
    {
        // Arrange: сверка терпит регистр и краевые пробелы, как TextBox.Text.Trim() в обработчиках.

        // Act.
        bool actual = AudioTransplantControl.ShouldApplyProbeResult(3, 3, @"c:\видео\b.mkv  ", @"C:\Видео\b.mkv");

        // Assert.
        actual.Should().BeTrue("технические различия написания пути не должны отбрасывать актуальную пробу");
    }
}
