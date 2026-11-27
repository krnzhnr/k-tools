// -*- coding: utf-8 -*-
using System.Collections.Generic;

using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Scripts;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Pages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;
using FluentAssertions;

namespace KTools_App.Tests;

/// <summary>
/// Проверяет единство строк направления сдвига между скриптами и калькулятором (issue 91).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class ShiftDirectionUnityTests
{
    /// <summary>
    /// Создаёт скрипт сдвига субтитров с замоканными зависимостями.
    /// </summary>
    private static SubtitleShiftScript CreateSubtitleScript()
    {
        return new SubtitleShiftScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    /// <summary>
    /// Создаёт скрипт сдвига аудио с замоканными зависимостями.
    /// </summary>
    private static AudioShiftScript CreateAudioScript()
    {
        return new AudioShiftScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object,
            new Mock<IFFmpegRunner>().Object,
            new Mock<IEac3toRunner>().Object,
            MockBuilders.CreateMediaProbeMock().Object);
    }

    /// <summary>
    /// Возвращает поле схемы настроек по ключу, падая с понятной ошибкой при отсутствии.
    /// </summary>
    private static SettingField FindSchemaField(List<SettingField> schema, string key)
    {
        return schema.Should().ContainSingle(f => f.Key == key).Which;
    }

    /// <summary>
    /// Проверяет, что варианты направления в схеме скрипта субтитров совпадают с метками калькулятора.
    /// </summary>
    [TestMethod]
    public void SubtitleSettingsSchema_ShiftDirection_MatchesCalculatorLabels()
    {
        // Arrange.
        SettingField field = FindSchemaField(CreateSubtitleScript().SettingsSchema, "ShiftDirection");
        string forwardLabel = TimingCalculatorPage.GetDirectionLabel(1500);
        string backwardLabel = TimingCalculatorPage.GetDirectionLabel(-1500);

        // Act & Assert.
        field.Options.Should().Contain(forwardLabel, "метка «вперёд» обязана совпадать с калькулятором");
        field.Options.Should().Contain(backwardLabel, "метка «назад» обязана совпадать с калькулятором");
    }

    /// <summary>
    /// Проверяет, что варианты направления в схеме скрипта аудио совпадают с метками калькулятора.
    /// </summary>
    [TestMethod]
    public void AudioSettingsSchema_ShiftDirection_MatchesCalculatorLabels()
    {
        // Arrange.
        SettingField field = FindSchemaField(CreateAudioScript().SettingsSchema, "ShiftDirection");
        string forwardLabel = TimingCalculatorPage.GetDirectionLabel(1500);
        string backwardLabel = TimingCalculatorPage.GetDirectionLabel(-1500);

        // Act & Assert.
        field.Options.Should().Contain(forwardLabel, "метка «вперёд» обязана совпадать с калькулятором");
        field.Options.Should().Contain(backwardLabel, "метка «назад» обязана совпадать с калькулятором");
    }

    /// <summary>
    /// Проверяет, что запись истории калькулятора со сдвигом вперёд распознаётся признаком IsForward.
    /// </summary>
    [TestMethod]
    public void BuildHistoryEntry_ForwardShift_IsForwardConsistent()
    {
        // Act.
        TimingHistoryEntry entry = TimingCalculatorPage.BuildHistoryEntry("0:00:01.000", "0:00:05.000");

        // Assert.
        entry.IsForward.Should().BeTrue("положительный сдвиг обязан считаться движением вперёд");
        entry.IsBackward.Should().BeFalse();
        entry.Direction.Should().Be(TimingCalculatorPage.GetDirectionLabel(entry.ShiftMs));
    }
}
