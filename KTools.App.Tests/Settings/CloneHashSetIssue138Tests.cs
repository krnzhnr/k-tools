// -*- coding: utf-8 -*-
using System.Collections.Generic;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Проверка issue 138: Clone возвращает ссылку для не-List коллекций.
/// </summary>
[TestClass]
public class CloneHashSetIssue138Tests
{
    /// <summary>
    /// Проверяет, что клонирование HashSet возвращает независимую копию,
    /// а не исходную ссылку: мутация копии не меняет оригинал.
    /// </summary>
    [TestMethod]
    public void Clone_HashSet_ReturnsCopy()
    {
        // Arrange
        var source = new HashSet<string> { "a", "b" };

        // Act
        object? cloned = SettingsValueSnapshot.Clone(source);

        // Assert
        cloned.Should().NotBeNull();
        cloned.Should().NotBeSameAs(source, "клон обязан быть новым объектом, а не исходной ссылкой");
        var copy = cloned.Should().BeAssignableTo<HashSet<string>>().Subject;
        copy.Should().BeEquivalentTo(source);
        copy.Add("c");
        source.Should().NotContain("c", "мутация выданного HashSet не должна менять кэш в обход AreEqual");
    }
}
