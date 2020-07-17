using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

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
}
