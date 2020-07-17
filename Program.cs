using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

#nullable enable

namespace AsyncEvents
{
    /// <summary>
    /// Wraps events as IAsyncEnumerable asynchronous stream
    /// </summary>
    public class EventChannel<T> : IDisposable
    {
        private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

        public Task Completion => _channel.Reader.Completion;

        /// <summary>Queue an event item to the write side of the channel</summary>
        public bool Post(T item)
        {
            return _channel.Writer.TryWrite(item);
        }

        /// <summary>Read queued events as an async stream</summary>
        public IAsyncEnumerable<T> ToAsyncEnumerable(CancellationToken token)
        {
            return _channel.Reader.ReadAllAsync(token);
        }

        /// <summary>A simple helper to wrap even handler scope as IDisposable</summary>
        internal struct EventSubscription<TEventHandler> : IDisposable
            where TEventHandler : Delegate
        {
            private readonly Action _unsubscribe;

            public EventSubscription(
                TEventHandler handler,
                Action<TEventHandler> subscribe,
                Action<TEventHandler> unsubscribe)
            {
                subscribe(handler);
                _unsubscribe = () => unsubscribe(handler);
            }

            public void Dispose()
            {
                _unsubscribe();
            }
        }

        /// <summary>
        /// Subscribe to an event
        /// </summary>
        public IDisposable Subscribe<TEventHandler>(
            TEventHandler handler,
            Action<TEventHandler> subscribe,
            Action<TEventHandler> unsubscribe) where TEventHandler : Delegate
        {
            return new EventSubscription<TEventHandler>(handler, subscribe, unsubscribe);
        }

        public void Dispose()
        {
            _channel.Writer.Complete();
        }
    }

    /// <summary>
    /// The UI and ReadEventStreamAsync
    /// </summary>
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
