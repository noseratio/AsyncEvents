using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

#nullable enable

namespace AsyncEvents
{
    class Program
    {
        /// <summary>process the async stream of events</summary>
        static async Task ReadEventStreamAsync(
            IAsyncEnumerable<(Component, string)> source, 
            CancellationToken token)
        {
            await foreach (var (component, text) in source.WithCancellation(token))
            {
                // e.g., delay processing
                await Task.Delay(100, token);
                Console.WriteLine($"{component.GetType().Name}: {text}");
            }
        }

        /// <summary>Creates the UI and runs the event loop</summary>
        static void RunApplication(CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            // create a form 
            using var form = new Form { Text = typeof(Program).FullName, Width = 640, Height = 480 };
            form.FormClosing += (s, e) => cts.Cancel();

            // with a panel
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill };
            form.Controls.Add(panel);

            // with a button
            var button = new Button { Text = "Click me", Anchor = AnchorStyles.None, AutoSize = true };
            panel.Controls.Add(button);
            Console.WriteLine("Window created.");

            // create a timer
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Start();

            // main event loop
            async void runEventLoopAsync(object? _)
            {
                try
                {
                    using var eventChannel = new EventChannel<(Component, string)>();

                    // push Click events to the channel
                    using var clickHandler = eventChannel.Subscribe<EventHandler>(
                        (s, e) => eventChannel.Post((button as Component, $"Cicked on {DateTime.Now}")),
                        handler => button!.Click += handler,
                        handler => button!.Click -= handler);

                    // push Tick events to the channel
                    using var tickHandler = eventChannel.Subscribe<EventHandler>(
                        (s, e) => eventChannel.Post((timer as Component, $"Ticked on {DateTime.Now}")),
                        handler => timer!.Tick += handler,
                        handler => timer!.Tick -= handler);

                    // process events as async stream via ToAsyncEnumerable(),
                    await ReadEventStreamAsync(eventChannel.ToAsyncEnumerable(cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Cancelled.");
                }
                finally
                {
                    form.Close();
                }
            }

            SynchronizationContext.Current!.Post(runEventLoopAsync, null);
            Application.Run(form);
        }

        [STAThread]
        static void Main()
        {
            Console.Title = typeof(Program).FullName;

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            RunApplication(cts.Token);
        }
    }
}
