using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace KTools_App;

/// <summary>
/// Точка входа в приложение, реализующая логику единственного экземпляра (Single-Instance).
/// </summary>
public static class Program
{
    private const string InstanceKey = "KToolsSingleInstanceKey";

    [STAThread]
    public static void Main(string[] args)
    {
        // Инициализируем COM-обертки для корректного маршалинга WinRT в WinUI 3
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Проверяем наличие активного экземпляра
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (instance.IsCurrent)
        {
            // Этот процесс является главным
            instance.Activated += OnInstanceActivated;

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        else
        {
            // Записываем свои аргументы командной строки во временный файл для главного процесса
            WriteArgsToFile(Environment.GetCommandLineArgs());

            // Этот процесс является второстепенным, перенаправляем параметры запуска главному процессу
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Сохраняет переданные аргументы во временный файл в директории PendingArgs.
    /// </summary>
    private static void WriteArgsToFile(string[] args)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = System.IO.Path.Combine(appData, "KTools", "PendingArgs");
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            string filePath = System.IO.Path.Combine(dir, $"{Guid.NewGuid()}.txt");
            System.IO.File.WriteAllLines(filePath, args);
        }
        catch
        {
            // Игнорируем ошибки при невозможности записать файл аргументов
        }
    }

    /// <summary>
    /// Вызывается в фоновом потоке главного экземпляра при получении перенаправленных аргументов.
    /// </summary>
    private static void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Передаем управление в App для обработки новых аргументов
        App.HandleActivation(args);
    }
}
