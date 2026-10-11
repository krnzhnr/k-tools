// -*- coding: utf-8 -*-
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты универсального механизма оценки условий отключения полей настроек.
/// Ключевое правило: условие, ссылающееся на скрытый управляющий параметр
/// (настройка невыбранного энкодера), игнорируется и не глушит поле.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SettingConditionEvaluatorTests
{
    private static SettingField BitrateFieldWith(params SettingDisableCondition[] conditions)
    {
        return new SettingField(
            "x265_min_bitrate",
            "Мин. битрейт (кбит/с)",
            SettingType.Int,
            4000,
            "Видео:Битрейт",
            disableConditions: new List<SettingDisableCondition>(conditions));
    }

    [TestMethod]
    public void IsDisabled_NoConditions_ReturnsFalse()
    {
        // Arrange
        var field = new SettingField("x265_v_bitrate", "Битрейт видео (кбит/с)", SettingType.Int, 4000, "Видео:Битрейт");

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, _ => null, _ => true);

        // Assert
        disabled.Should().BeFalse();
    }

    [TestMethod]
    public void IsDisabled_OwnControllerEnabled_ReturnsFalse()
    {
        // Arrange: авторасчёт x265 выключен — поле должно быть активно
        var field = BitrateFieldWith(
            new SettingDisableCondition("x265_auto_bitrate", "True"),
            new SettingDisableCondition("lossless", "True"));
        var values = new Dictionary<string, string>
        {
            { "x265_auto_bitrate", "False" },
            { "lossless", "False" }
        };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], _ => true);

        // Assert
        disabled.Should().BeFalse();
    }

    [TestMethod]
    public void IsDisabled_OwnControllerDisabled_ReturnsTrue()
    {
        // Arrange: авторасчёт x265 включён — поле глушится
        var field = BitrateFieldWith(new SettingDisableCondition("x265_auto_bitrate", "True"));
        var values = new Dictionary<string, string> { { "x265_auto_bitrate", "True" } };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], _ => true);

        // Assert
        disabled.Should().BeTrue();
    }

    [TestMethod]
    public void IsDisabled_HiddenForeignControllerIgnored_ReturnsFalse()
    {
        // Arrange: регрессия reported-бага — чужой включённый контроллер скрыт (невыбранный энкодер),
        // свой выключен. Поле обязано остаться активным.
        var field = BitrateFieldWith(
            new SettingDisableCondition("x265_auto_bitrate", "True"),
            new SettingDisableCondition("auto_bitrate", "True"));
        var values = new Dictionary<string, string>
        {
            { "x265_auto_bitrate", "False" },
            { "auto_bitrate", "True" }
        };
        var visibility = new Dictionary<string, bool>
        {
            { "x265_auto_bitrate", true },
            { "auto_bitrate", false }
        };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], k => visibility[k]);

        // Assert
        disabled.Should().BeFalse("скрытый контроллер чужого энкодера не должен глушить поле");
    }

    [TestMethod]
    public void IsDisabled_VisibleForeignControllerStillDisables_ReturnsTrue()
    {
        // Arrange: оба контроллера видимы и чужой включён — глушение сохраняется
        var field = BitrateFieldWith(
            new SettingDisableCondition("x265_auto_bitrate", "True"),
            new SettingDisableCondition("auto_bitrate", "True"));
        var values = new Dictionary<string, string>
        {
            { "x265_auto_bitrate", "False" },
            { "auto_bitrate", "True" }
        };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], _ => true);

        // Assert
        disabled.Should().BeTrue();
    }

    [TestMethod]
    public void IsDisabled_NegatedCondition_InvertsMatch()
    {
        // Arrange
        var field = BitrateFieldWith(new SettingDisableCondition("nvenc_rc", new List<string> { "constqp" }, negate: true));
        var values = new Dictionary<string, string> { { "nvenc_rc", "vbr" } };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], _ => true);

        // Assert
        disabled.Should().BeTrue("инвертированное условие срабатывает при несовпадении значения");
    }

    [TestMethod]
    public void IsDisabled_ValueComparison_IsCaseInsensitive()
    {
        // Arrange: в настройках хранится bool, сравнение идёт со строкой "True"
        var field = BitrateFieldWith(new SettingDisableCondition("lossless", "True"));
        var values = new Dictionary<string, string> { { "lossless", true.ToString() } };

        // Act
        bool disabled = SettingConditionEvaluator.IsDisabled(field, k => values[k], _ => true);

        // Assert
        disabled.Should().BeTrue();
    }

    [TestMethod]
    public void IsVisible_MatchingVisibilityCondition_ReturnsTrue()
    {
        // Arrange
        var field = new SettingField(
            "x265_crf",
            "CRF / Качество",
            SettingType.Int,
            23,
            "Видео:Битрейт",
            visibilityConditions: new List<SettingVisibilityCondition> { new("x265_rc", "CRF") });
        var values = new Dictionary<string, string> { { "x265_rc", "CRF" } };

        // Act
        bool visible = SettingConditionEvaluator.IsVisible(field, k => values[k]);

        // Assert
        visible.Should().BeTrue();
    }

    [TestMethod]
    public void IsVisible_NonMatchingVisibilityCondition_ReturnsFalse()
    {
        // Arrange
        var field = new SettingField(
            "x265_crf",
            "CRF / Качество",
            SettingType.Int,
            23,
            "Видео:Битрейт",
            visibilityConditions: new List<SettingVisibilityCondition> { new("x265_rc", "CRF") });
        var values = new Dictionary<string, string> { { "x265_rc", "Битрейт (ABR)" } };

        // Act
        bool visible = SettingConditionEvaluator.IsVisible(field, k => values[k]);

        // Assert
        visible.Should().BeFalse();
    }
}
