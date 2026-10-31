// -*- coding: utf-8 -*-
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты сравнения состава схемы: смена кодека обязана детектироваться
/// по набору ключей, чтобы интерфейс перестраивался, а не показывал чужие поля.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SettingsSchemaComparerTests
{
    private static SettingField Combo(string key)
    {
        return new SettingField(key, key, SettingType.Combo, "x", "Видео:Кодирование");
    }

    [TestMethod]
    public void GetFlatKeys_ExpanderChildren_Included()
    {
        // Arrange
        var expander = new SettingField(
            "x264_advanced_options", "Расширенные параметры", SettingType.Expander, false, "Видео:Расширенные параметры",
            childFields: new List<SettingField> { Combo("x264_tune"), Combo("x264_profile") });

        // Act
        var keys = SettingsSchemaComparer.GetFlatKeys(new[] { Combo("cpu_codec"), expander });

        // Assert
        keys.Should().BeEquivalentTo("cpu_codec", "x264_advanced_options", "x264_tune", "x264_profile");
    }

    [TestMethod]
    public void HasSameKeys_IdenticalSets_ReturnsTrue()
    {
        // Arrange
        var schema = new[] { Combo("a"), Combo("b") };

        // Act & Assert
        SettingsSchemaComparer.HasSameKeys(schema, new[] { "a", "b" }).Should().BeTrue();
        SettingsSchemaComparer.HasSameKeys(schema, new[] { "B", "A" }).Should().BeTrue("сравнение регистронезависимо");
    }

    [TestMethod]
    public void HasSameKeys_CodecSwitchDetected_ReturnsFalse()
    {
        // Arrange: было поле x265, стало x264 — состав сменился, нужен ребилд
        var hevcSchema = new[] { Combo("cpu_codec"), Combo("x265_preset") };
        var h264Schema = new[] { Combo("cpu_codec"), Combo("x264_preset") };

        // Act & Assert
        SettingsSchemaComparer.HasSameKeys(h264Schema, new[] { "cpu_codec", "x265_preset" }).Should().BeFalse();
        SettingsSchemaComparer.HasSameKeys(hevcSchema, new[] { "cpu_codec", "x265_preset" }).Should().BeTrue();
    }

    [TestMethod]
    public void HasSameKeys_ExtraOrMissingKey_ReturnsFalse()
    {
        // Arrange
        var schema = new[] { Combo("a"), Combo("b") };

        // Act & Assert
        SettingsSchemaComparer.HasSameKeys(schema, new[] { "a", "b", "c" }).Should().BeFalse();
        SettingsSchemaComparer.HasSameKeys(schema, new[] { "a" }).Should().BeFalse();
        SettingsSchemaComparer.HasSameKeys(schema, new string[0]).Should().BeFalse();
    }
}
