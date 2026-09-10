// -*- coding: utf-8 -*-
using System;
using System.Collections.Concurrent;
using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Утилита изоляции глобального мессенджера сообщений между тестами.
/// CommunityToolkit не предоставляет публичного API для замены WeakReferenceMessenger.Default,
/// поэтому изоляция выполняется двумя стратегиями:
/// 1. Reflection-подмена внутреннего статического поля на свежий экземпляр (полная изоляция);
/// 2. Если поле недоступно — регистрация всех созданных получателей и их принудительная
///    отписка через UnregisterAll после теста.
/// </summary>
public static class MessengerIsolation
{
    private static readonly ConcurrentDictionary<object, byte> TrackedRecipients = new();

    private static readonly Lazy<Func<bool>> ResetDefaultMessengerLazy = new(CreateResetDelegate);

    /// <summary>
    /// Пытается подменить внутреннее статическое поле Default нового экземпляром.
    /// Возвращает true, если reflection-подмена поддерживается текущей версией toolkit.
    /// </summary>
    /// <returns>Признак успешной подмены.</returns>
    public static bool TryReplaceDefaultMessenger()
    {
        return ResetDefaultMessengerLazy.Value();
    }

    /// <summary>
    /// Полный сброс: подменяет Default-мессенджер при поддержке reflection
    /// и принудительно отписывает всех отслеживаемых получателей из старого мессенджера.
    /// Вызывать в TestInitialize и TestCleanup всех тестов, работающих с messenger-подписками.
    /// </summary>
    public static void ResetMessenger()
    {
        // Отписываем всех получателей, зарегистрированных через Track(), из текущего мессенджера
        foreach (var recipient in TrackedRecipients.Keys)
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        TrackedRecipients.Clear();

        // Подменяем глобальный мессенджер, если reflection-путь доступен
        TryReplaceDefaultMessenger();
    }

    /// <summary>
    /// Регистрирует получателя для гарантированной отписки в ResetMessenger.
    /// Используется для ViewModels, подписывающихся на глобальный messenger в конструкторе.
    /// </summary>
    /// <param name="recipient">Получатель (обычно ViewModel).</param>
    public static void Track(object recipient)
    {
        TrackedRecipients[recipient] = 0;
    }

    private static Func<bool> CreateResetDelegate()
    {
        const string fieldName = "DefaultMessenger";

        FieldInfo? field = typeof(WeakReferenceMessenger).GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic);

        if (field == null)
        {
            // Фолбэк: автосвойство Default с бэкинг-полем
            field = typeof(WeakReferenceMessenger).GetField(
                "<Default>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        if (field == null)
        {
            return () => false;
        }

        return () =>
        {
            try
            {
                field.SetValue(null, new WeakReferenceMessenger());
                return true;
            }
            catch
            {
                return false;
            }
        };
    }
}

/// <summary>
/// Базовый класс тестов, работающих с глобальным мессенджером сообщений.
/// Гарантирует детерминированную изоляцию подписок между тестами:
/// перед тестом и после теста все получатели отписываются, а Default-мессенджер
/// подменяется свежим экземпляром (если позволяет версия toolkit).
/// Класс помечен DoNotParallelize: мутации глобального статического состояния
/// несовместимы с параллельным выполнением тестов.
/// </summary>
[TestClass]
[DoNotParallelize]
public abstract class IsolatedMessengerTestBase
{
    /// <summary>
    /// Полная изоляция мессенджера перед началом каждого теста.
    /// </summary>
    [TestInitialize]
    public void MessengerSetup()
    {
        MessengerIsolation.ResetMessenger();
    }

    /// <summary>
    /// Полная изоляция мессенджера после завершения каждого теста,
    /// чтобы подписки созданного ViewModel не утекли в другие тесты.
    /// </summary>
    [TestCleanup]
    public void MessengerCleanup()
    {
        MessengerIsolation.ResetMessenger();
    }
}
