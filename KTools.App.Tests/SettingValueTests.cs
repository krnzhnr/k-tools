// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты единой нормализации значений настроек: значения с диска (JsonElement)
/// обязаны читаться так же, как их показывает интерфейс, иначе кодировщик молча
/// применяет дефолты вместо выбранных пользователем значений.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class SettingValueTests
{
    private static JsonElement JsonOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [TestMethod]
    public void AsString_JsonString_ReturnsUnquotedValue()
    {
        // Регрессия: наивный ToString() давал '"ultrafast"' в кавычках
        SettingValue.AsString(JsonOf("\"ultrafast\""), "medium").Should().Be("ultrafast");
        SettingValue.AsString(JsonOf("\"AVC / H.264\""), "").Should().Be("AVC / H.264");
    }

    [TestMethod]
    public void AsString_PlainValues_PassThrough()
    {
        SettingValue.AsString("veryfast", "medium").Should().Be("veryfast");
        SettingValue.AsString(null, "medium").Should().Be("medium");
        SettingValue.AsString(JsonOf("23"), "").Should().Be("23");
        SettingValue.AsString(JsonOf("true"), "").Should().Be("True");
        SettingValue.AsString(JsonOf("false"), "").Should().Be("False");
    }

    [TestMethod]
    public void AsBool_AllShapes_MatchInterface()
    {
        SettingValue.AsBool(true, false).Should().BeTrue();
        SettingValue.AsBool("True", false).Should().BeTrue();
        SettingValue.AsBool("False", true).Should().BeFalse();
        SettingValue.AsBool(JsonOf("true"), false).Should().BeTrue();
        SettingValue.AsBool(JsonOf("false"), true).Should().BeFalse();
        SettingValue.AsBool(JsonOf("\"True\""), false).Should().BeTrue();
        SettingValue.AsBool(null, true).Should().BeTrue();
    }

    [TestMethod]
    public void AsInt_AllShapes_MatchInterface()
    {
        SettingValue.AsInt(18, 23).Should().Be(18);
        SettingValue.AsInt(JsonOf("18"), 23).Should().Be(18);
        SettingValue.AsInt(JsonOf("\"18\""), 23).Should().Be(18);
        SettingValue.AsInt(null, 23).Should().Be(23);
        SettingValue.AsInt(JsonOf("true"), 23).Should().Be(23, "нечисловое значение даёт дефолт");
    }

    [TestMethod]
    public void AsFloat_AllShapes_MatchInterface()
    {
        SettingValue.AsFloat(2.5f, 1.0f).Should().Be(2.5f);
        SettingValue.AsFloat(JsonOf("2.5"), 1.0f).Should().BeApproximately(2.5f, 0.001f);
        SettingValue.AsFloat(JsonOf("\"2.5\""), 1.0f).Should().BeApproximately(2.5f, 0.001f);
        SettingValue.AsFloat(null, 1.0f).Should().Be(1.0f);
    }

    [TestMethod]
    public void AsValue_NullValue_ReturnsDefaults()
    {
        // Дымовой тест контракта: отсутствие значения неотличимо от null
        SettingValue.AsString(null, null).Should().Be(string.Empty);
        SettingValue.AsBool(null, false).Should().BeFalse();
        SettingValue.AsInt(null, 0).Should().Be(0);
    }
}
