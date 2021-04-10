﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Logging;
using Couchbase.Core.Retry;
using Couchbase.Transactions.Cleanup;
using Couchbase.Transactions.Cleanup.LostTransactions;
using Couchbase.Transactions.Config;
using Couchbase.Transactions.DataAccess;
using Couchbase.Transactions.Error;
using Couchbase.Transactions.Error.External;
using Couchbase.Transactions.Internal.Test;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Couchbase.Transactions
{
    /// <summary>
    /// A class for running transactional operations against a Couchbase Cluster.
    /// </summary>
    public class Transactions : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// A standard delay between retried operations.
        /// </summary>
        public static readonly TimeSpan OpRetryDelay = TimeSpan.FromMilliseconds(3);
        private static long InstancesCreated = 0;
        private static long InstancesCreatedDoingBackgroundCleanup = 0;
        private readonly ICluster _cluster;
        private bool _disposedValue;
        private readonly IRedactor _redactor;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<Transactions> _logger;
        private readonly CleanupWorkQueue _cleanupWorkQueue;
        private readonly Cleaner _cleaner;
        private readonly IAsyncDisposable? _lostTransactionsCleanup;

        /// <summary>
        /// Gets the <see cref="TransactionConfig"/> to apply to all transaction runs from this instance.
        /// </summary>
        public TransactionConfig Config { get; }

        internal ICluster Cluster => _cluster;

        internal ITestHooks TestHooks { get; set; } = DefaultTestHooks.Instance;
        internal IDocumentRepository? DocumentRepository { get; set; } = null;
        internal IAtrRepository? AtrRepository { get; set; } = null;
        internal int? CleanupQueueLength => Config.CleanupClientAttempts ?_cleanupWorkQueue?.QueueLength : null;

        internal ICleanupTestHooks CleanupTestHooks
        {
            get => _cleaner.TestHooks;
            set
            {
                _cleaner.TestHooks = value;
                _cleanupWorkQueue.TestHooks = value;
            }
        }

        internal void ConfigureTestHooks(ITestHooks testHooks, ICleanupTestHooks cleanupHooks)
        {
            TestHooks = testHooks;
            CleanupTestHooks = cleanupHooks;
        }

        private Transactions(ICluster cluster, TransactionConfig config)
        {
            _cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _redactor = _cluster.ClusterServices?.GetService(typeof(IRedactor)) as IRedactor ?? throw new ArgumentNullException(nameof(IRedactor), "Redactor implementation not registered.");
            Interlocked.Increment(ref InstancesCreated);
            if (config.CleanupLostAttempts)
            {
                Interlocked.Increment(ref InstancesCreatedDoingBackgroundCleanup);
            }

            loggerFactory = config.LoggerFactory
                ?? _cluster.ClusterServices?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                ?? NullLoggerFactory.Instance;
            _logger = loggerFactory.CreateLogger<Transactions>();

            _cleanupWorkQueue = new CleanupWorkQueue(_cluster, Config.KeyValueTimeout,loggerFactory, config.CleanupClientAttempts);

            _cleaner = new Cleaner(cluster, Config.KeyValueTimeout, loggerFactory, creatorName: nameof(Transactions));

            if (config.CleanupLostAttempts)
            {
                _lostTransactionsCleanup = new LostTransactionManager(_cluster, loggerFactory, config.CleanupWindow, config.KeyValueTimeout);
            }

            // TODO: whatever the equivalent of 'cluster.environment().eventBus().publish(new TransactionsStarted(config));' is.
        }

        /// <summary>
        /// Create a <see cref="Transactions"/> instance for running transactions against the specified <see cref="ICluster">Cluster</see>.
        /// </summary>
        /// <param name="cluster">The cluster where your documents will be located.</param>
        /// <returns>A <see cref="Transactions"/> instance.</returns>
        /// <remarks>The instance returned from this method should be kept for the lifetime of your application and used like a singleton per Couchbase cluster you will be accessing.</remarks>
        public static Transactions Create(ICluster cluster) => Create(cluster, TransactionConfigBuilder.Create().Build());

        /// <summary>
        /// Create a <see cref="Transactions"/> instance for running transactions against the specified <see cref="ICluster">Cluster</see>.
        /// </summary>
        /// <param name="cluster">The cluster where your documents will be located.</param>
        /// <param name="config">The <see cref="TransactionConfig"/> to use for all transactions against this cluster.</param>
        /// <returns>A <see cref="Transactions"/> instance.</returns>
        /// <remarks>The instance returned from this method should be kept for the lifetime of your application and used like a singleton per Couchbase cluster you will be accessing.</remarks>
        public static Transactions Create(ICluster cluster, TransactionConfig config) => new Transactions(cluster, config);

        /// <summary>
        /// Create a <see cref="Transactions"/> instance for running transactions against the specified <see cref="ICluster">Cluster</see>.
        /// </summary>
        /// <param name="cluster">The cluster where your documents will be located.</param>
        /// <param name="config">The <see cref="TransactionConfigBuilder"/> to generate a <see cref="TransactionConfig"/> to use for all transactions against this cluster.</param>
        /// <returns>A <see cref="Transactions"/> instance.</returns>
        /// <remarks>The instance returned from this method should be kept for the lifetime of your application and used like a singleton per Couchbase cluster you will be accessing.</remarks>
        public static Transactions Create(ICluster cluster, TransactionConfigBuilder configBuilder) =>
            Create(cluster, configBuilder.Build());

        /// <summary>
        /// Run a transaction agains the cluster.
        /// </summary>
        /// <param name="transactionLogic">A func representing the transaction logic. All data operations should use the methods on the <see cref="AttemptContext"/> provided.  Do not mix and match non-transactional data operations.</param>
        /// <returns>The result of the transaction.</returns>
        public Task<TransactionResult> RunAsync(Func<AttemptContext, Task> transactionLogic) =>
            RunAsync(transactionLogic, PerTransactionConfigBuilder.Create().Build());

        /// <summary>
        /// Run a transaction agains the cluster.
        /// </summary>
        /// <param name="transactionLogic">A func representing the transaction logic. All data operations should use the methods on the <see cref="AttemptContext"/> provided.  Do not mix and match non-transactional data operations.</param>
        /// <param name="perConfig">A config with values unique to this specific transaction.</param>
        /// <returns>The result of the transaction.</returns>
        public async Task<TransactionResult> RunAsync(Func<AttemptContext, Task> transactionLogic, PerTransactionConfig perConfig)
        {
            // https://hackmd.io/foGjnSSIQmqfks2lXwNp8w?view#The-Core-Loop

            var overallContext = new TransactionContext(
                transactionId: Guid.NewGuid().ToString(),
                startTime: DateTimeOffset.UtcNow,
                config: Config,
                perConfig: perConfig
                );

            var result = new TransactionResult() { TransactionId =  overallContext.TransactionId };
            var opRetryBackoffMillisecond = 1;
            var randomJitter = new Random();

            while (true)
            {
                try
                {
                    await ExecuteApplicationLambda(transactionLogic, overallContext, loggerFactory, result).CAF();
                    return result;
                }
                catch (TransactionOperationFailedException ex)
                {
                    // If anything above fails with error err
                    if (ex.RetryTransaction && !overallContext.IsExpired)
                    {
                        // If err.retry is true, and the transaction has not expired
                        //Apply OpRetryBackoff, with randomized jitter. E.g.each attempt will wait exponentially longer before retrying, up to a limit.
                        var jitter = randomJitter.Next(10);
                        var delayMs = opRetryBackoffMillisecond + jitter;
                        await Task.Delay(delayMs).CAF();
                        opRetryBackoffMillisecond = Math.Min(opRetryBackoffMillisecond * 10, 100);
                        //    Go back to the start of this loop, e.g.a new attempt.
                        continue;
                    }
                    else
                    {
                        // Otherwise, we are not going to retry. What happens next depends on err.raise
                        switch (ex.FinalErrorToRaise)
                        {
                            //  Failure post-commit may or may not be a failure to the application,
                            // as the cleanup process should complete the commit soon. It often depends on
                            // whether the application wants RYOW, e.g. AT_PLUS. So, success will be returned,
                            // but TransactionResult.unstagingComplete() will be false.
                            // The application can interpret this as it needs.
                            case TransactionOperationFailedException.FinalError.TransactionFailedPostCommit:
                                result.UnstagingComplete = false;
                                return result;

                            // Raise TransactionExpired to application, with a cause of err.cause.
                            case TransactionOperationFailedException.FinalError.TransactionExpired:
                                throw new TransactionExpiredException("Transaction Expired", ex.Cause, result);

                            // Raise TransactionCommitAmbiguous to application, with a cause of err.cause.
                            case TransactionOperationFailedException.FinalError.TransactionCommitAmbiguous:
                                throw new TransactionCommitAmbiguousException("Transaction may have failed to commit.", ex.Cause, result);

                            default:
                                throw new TransactionFailedException("Transaction failed.", ex.Cause, result);
                        }
                    }
                }
                catch (Exception notWrapped)
                {
                    // Assert err is an ErrorWrapper
                    throw new InvalidOperationException(
                        $"All exceptions should have been wrapped in an {nameof(TransactionOperationFailedException)}.",
                        notWrapped);
                }
            }

            throw new InvalidOperationException("Loop should not have exited without expiration.");
        }

        private async Task ExecuteApplicationLambda(Func<AttemptContext, Task> transactionLogic, TransactionContext overallContext, ILoggerFactory? loggerFactory, TransactionResult result)
        {
            var ctx = new AttemptContext(
                overallContext,
                Config,
                Guid.NewGuid().ToString(),
                TestHooks,
                _redactor,
                loggerFactory,
                DocumentRepository,
                AtrRepository
            );

            try
            {
                try
                {
                    await transactionLogic(ctx).CAF();
                    await ctx.AutoCommit().CAF();
                }
                catch (TransactionOperationFailedException)
                {
                    // already a classified error
                    throw;
                }
                catch (Exception innerEx)
                {
                    // If err is not an ErrorWrapper, follow
                    // Exceptions Raised by the Application Lambda logic to turn it into one.
                    // From now on, all errors must be an ErrorWrapper.
                    // https://hackmd.io/foGjnSSIQmqfks2lXwNp8w?view#Exceptions-Raised-by-the-Application-Lambda
                    var error = ErrorBuilder.CreateError(ctx, innerEx.Classify()).Cause(innerEx);
                    if (innerEx is IRetryable)
                    {
                        error.RetryTransaction();
                    }

                    throw error.Build();
                }
            }
            catch (TransactionOperationFailedException ex)
            {
                // If err.rollback is true (it generally will be), auto-rollback the attempt by calling rollbackInternal with appRollback=false.
                if (ex.AutoRollbackAttempt)
                {
                    try
                    {
                        _logger.LogWarning("Attempt failed, attempting automatic rollback...");
                        await ctx.RollbackInternal(isAppRollback: false).CAF();
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogWarning("Rollback failed due to {reason}", rollbackEx.Message);
                        // if rollback failed, raise the original error, but with retry disabled:
                        // Error(ec = err.ec, cause = err.cause, raise = err.raise
                        throw ErrorBuilder.CreateError(ctx, ex.CausingErrorClass)
                            .Cause(ex.Cause)
                            .DoNotRollbackAttempt()
                            .RaiseException(ex.FinalErrorToRaise)
                            .Build();
                    }
                }

                // If the transaction has expired, raised Error(ec = FAIL_EXPIRY, rollback=false, raise = TRANSACTION_EXPIRED)
                if (overallContext.IsExpired)
                {
                    if (ex.CausingErrorClass == ErrorClass.FailExpiry)
                    {
                        // already FailExpiry
                        throw;
                    }

                    _logger.LogWarning("Transaction is expired.  No more retries or rollbacks.");
                    throw ErrorBuilder.CreateError(ctx, ErrorClass.FailExpiry)
                        .DoNotRollbackAttempt()
                        .RaiseException(TransactionOperationFailedException.FinalError.TransactionExpired)
                        .Build();
                }

                // Else if it succeeded or no rollback was performed, propagate err up.
                _logger.LogDebug("Propagating error up. (ec = {ec}, retry = {retry}, finalError = {finalError})", ex.CausingErrorClass, ex.RetryTransaction, ex.FinalErrorToRaise);
                throw;
            }
            finally
            {
                result.UnstagingComplete = ctx.UnstagingComplete;
                if (Config.CleanupClientAttempts)
                {
                    AddCleanupRequest(ctx);
                }
            }
        }

        private void AddCleanupRequest(AttemptContext ctx)
        {
            var cleanupRequest = ctx.GetCleanupRequest();
            if (cleanupRequest != null)
            {
                if (!_cleanupWorkQueue.TryAddCleanupRequest(cleanupRequest))
                {
                    _logger.LogWarning("Failed to add background cleanup request: {req}", cleanupRequest);
                }
            }
        }

        internal async IAsyncEnumerable<TransactionCleanupAttempt> CleanupAttempts()
        {
            foreach (var cleanupRequest in _cleanupWorkQueue.RemainingCleanupRequests)
            {
                yield return await _cleaner.ProcessCleanupRequest(cleanupRequest).CAF();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                _disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Transactions()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            _cleanupWorkQueue.Dispose();

            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (Config.CleanupClientAttempts)
            {
                _ = await CleanupAttempts().ToListAsync();
            }

            if (_lostTransactionsCleanup != null)
            {
                await _lostTransactionsCleanup.DisposeAsync().CAF();
            }

            Dispose();
        }
    }
}


/* ************************************************************
 *
 *    @author Couchbase <info@couchbase.com>
 *    @copyright 2021 Couchbase, Inc.
 *
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *
 *        http://www.apache.org/licenses/LICENSE-2.0
 *
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *
 * ************************************************************/
