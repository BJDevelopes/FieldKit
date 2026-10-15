// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace Fieldkit.Tests.Support;

/// <summary>
/// One WPF Application per test process, on one long-lived STA thread with the app theme loaded.
/// WPF allows a single Application per AppDomain and binds resources to the thread that created
/// them, so every test that renders UI must run its work here instead of creating its own.
/// </summary>
public static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <paramref name="action"/> on the shared UI thread and rethrows its failure.</summary>
    public static void Run(Action action, string theme = "Dark")
    {
        ExceptionDispatchInfo? failure = null;
        Host.Value.Invoke(() =>
        {
            try
            {
                LoadTheme(theme);
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    /// <summary>Runs async UI work on the shared thread without blocking it (continuations come back to it).</summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> func, string theme = "Dark") =>
        Host.Value.InvokeAsync(() =>
        {
            LoadTheme(theme);
            return func();
        }).Task.Unwrap();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    private static void LoadTheme(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Fieldkit;component/UI/Themes/{theme}.xaml") });
        dictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Fieldkit;component/UI/Themes/Controls.xaml") });
    }
}
