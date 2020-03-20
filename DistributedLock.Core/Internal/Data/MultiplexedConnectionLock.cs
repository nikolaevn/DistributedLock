using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Internal.Data
{
    /// <summary>
    /// Allows multiple SQL application locks to be taken on a single connection.
    /// 
    /// This class is thread-safe except for <see cref="IAsyncDisposable.DisposeAsync"/>
    /// </summary>
    internal sealed class MultiplexedConnectionLock : IAsyncDisposable
    {
        /// <summary>
        /// Protects access to <see cref="_heldLocks"/> and <see cref="_connection"/>
        /// </summary>
        private readonly AsyncLock _mutex = AsyncLock.Create();
        private readonly HashSet<string> _heldLocks = new HashSet<string>();
        private readonly DatabaseConnection _connection;

        public MultiplexedConnectionLock(DatabaseConnection connection)
        {
            this._connection = connection;
        }

        public async ValueTask<Result> TryAcquireAsync<TLockCookie>(
            string name,
            TimeoutValue timeout,
            IDbSynchronizationStrategy<TLockCookie> strategy,
            CancellationToken cancellationToken,
            bool opportunistic)
            where TLockCookie : class
        {
            using var mutextHandle = await this._mutex.TryAcquireAsync(opportunistic ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            if (mutextHandle == null)
            {
                // mutex wasn't free, so just give up
                Invariant.Require(opportunistic);
                // The current lock is busy so we allow retry but on a different lock instance. We can't safely dispose
                // since we never acquired the mutex so we can't check _heldLocks
                return new Result(MultiplexedConnectionLockRetry.Retry, canSafelyDispose: false);
            }

            try
            {
                if (this._heldLocks.Contains(name))
                {
                    // we won't try to hold the same lock twice on one connection. At some point, we could
                    // support this case in-memory using a counter for each multiply-held lock name and being careful
                    // with modes
                    return this.GetFailureResultNoLock(isAlreadyHeld: true, opportunistic, timeout);
                }

                if (!this._connection.CanExecuteQueries)
                {
                    await this._connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                }

                var lockCookie = await strategy.TryAcquireAsync(this._connection, name, opportunistic ? TimeSpan.Zero : timeout, cancellationToken).ConfigureAwait(false);
                if (lockCookie != null)
                {
                    var handle = new Handle<TLockCookie>(this, strategy, name, lockCookie).WithManagedFinalizer();
                    this._heldLocks.Add(name);
                    return new Result(handle);
                }

                // we failed to acquire the lock, so we should retry if we were being opportunistic and artificially
                // shortened the timeout
                return this.GetFailureResultNoLock(isAlreadyHeld: false, opportunistic, timeout);
            }
            finally
            {
                await this.CloseConnectionIfNeededNoLockAsync().ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            Invariant.Require(this._heldLocks.Count == 0);

            return this._connection.DisposeAsync();
        }

        public async ValueTask<bool> GetIsInUseAsync()
        {
            using var mutexHandle = await this._mutex.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            return mutexHandle == null || this._heldLocks.Count == 0;
        }

        private Result GetFailureResultNoLock(bool isAlreadyHeld, bool opportunistic, TimeoutValue timeout)
        {
            // only opportunistic acquisitions trigger retries
            if (!opportunistic) 
            {
                return new Result(MultiplexedConnectionLockRetry.NoRetry, canSafelyDispose: this._heldLocks.Count == 0); 
            }

            if (isAlreadyHeld)
            {
                // We're already holding the lock so we allow retry but on a different lock instance.
                // We can't safely dispose because we're holding the lock
                return new Result(MultiplexedConnectionLockRetry.Retry, canSafelyDispose: false);
            }

            // if we get here, we failed due to a timeout
            var isHoldingLocks = this._heldLocks.Count != 0;

            if (timeout.IsZero)
            {
                // if acquire timed out and the caller requested a zero timeout, that's conventional failure
                // and we shouldn't retry
                return new Result(MultiplexedConnectionLockRetry.NoRetry, canSafelyDispose: !isHoldingLocks);
            }

            if (isHoldingLocks)
            {
                // if we're holding other locks, then we should retry on another lock
                return new Result(MultiplexedConnectionLockRetry.Retry, canSafelyDispose: false);
            }

            // If we're not holding anything, then it's safe to retry on this instance since we can't
            // possibly block a release. It's also safe to dispose this lock, but that won't happen since
            // we're going to re-try on it instead
            return new Result(MultiplexedConnectionLockRetry.RetryOnThisLock, canSafelyDispose: true);
        }

        private async ValueTask ReleaseAsync<TLockCookie>(IDbSynchronizationStrategy<TLockCookie> strategy, string name, TLockCookie lockCookie)
            where TLockCookie : class
        {
            using var _ = await this._mutex.AcquireAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await strategy.ReleaseAsync(this._connection, name, lockCookie).ConfigureAwait(false);
            }
            finally
            {
                this._heldLocks.Remove(name);
                await this.CloseConnectionIfNeededNoLockAsync().ConfigureAwait(false);
            }
        }

        private ValueTask CloseConnectionIfNeededNoLockAsync()
        {
            return this._heldLocks.Count == 0 && this._connection.CanExecuteQueries
                ? this._connection.CloseAsync()
                : default;
        }

        public readonly struct Result
        {
            public Result(IDistributedLockHandle handle)
            {
                this.Handle = handle;
                this.Retry = MultiplexedConnectionLockRetry.NoRetry;
                this.CanSafelyDispose = false; // since we have handle
            }

            public Result(MultiplexedConnectionLockRetry retry, bool canSafelyDispose)
            {
                this.Handle = null;
                this.Retry = retry;
                this.CanSafelyDispose = canSafelyDispose;
            }

            public IDistributedLockHandle? Handle { get; }
            public MultiplexedConnectionLockRetry Retry { get; }
            public bool CanSafelyDispose { get; }
        }

        private sealed class Handle<TLockCookie> : IDistributedLockHandle
            where TLockCookie : class
        {
            private readonly string _name;
            private RefBox<(MultiplexedConnectionLock @lock, IDbSynchronizationStrategy<TLockCookie> strategy, TLockCookie lockCookie)>? _box;

            public Handle(MultiplexedConnectionLock @lock, IDbSynchronizationStrategy<TLockCookie> strategy, string name, TLockCookie lockCookie)
            {
                this._name = name;
                this._box = RefBox.Create((@lock, strategy, lockCookie));
            }

            public CancellationToken HandleLostToken => throw new NotImplementedException();

            public ValueTask DisposeAsync()
            {
                return RefBox.TryConsume(ref this._box, out var contents)
                    ? contents.@lock.ReleaseAsync(contents.strategy, this._name, contents.lockCookie)
                    : default;
            }

            void IDisposable.Dispose() => SyncOverAsync.Run(@this => @this.DisposeAsync(), this, willGoAsync: false);
        }
    }

    internal enum MultiplexedConnectionLockRetry
    {
        NoRetry,
        RetryOnThisLock,
        Retry,
    }
}