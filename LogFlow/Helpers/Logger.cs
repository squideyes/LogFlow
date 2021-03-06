﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace LogFlow
{
    public sealed class Logger : IDisposable
    {
        private bool disposed = false;

        private CancellationTokenSource cts;

        private readonly List<BufferBlock<LogItem>> bufferBlocks;
        private readonly List<ActionBlock<LogItem[]>> writerBlocks;

        public event EventHandler<GenericArgs<AggregateException>> OnFailure;
        public event EventHandler OnCancelled;

        public Logger(params AbstractWriter[] writers)
        {
            Contract.Requires(writers != null);
            Contract.Requires(writers.Length >= 1);

            cts = new CancellationTokenSource();

            bufferBlocks = new List<BufferBlock<LogItem>>();
            writerBlocks = new List<ActionBlock<LogItem[]>>();

            foreach (var writer in writers)
            {
                writer.Setup();

                var bufferBlock = new BufferBlock<LogItem>(
                    new DataflowBlockOptions()
                    {
                        CancellationToken = cts.Token
                    });

                var batchBlock = new BatchBlock<LogItem>(
                    writer.BatchSize,
                    new GroupingDataflowBlockOptions()
                    {
                        Greedy = true,
                        CancellationToken = cts.Token
                    });

                var timer = new Timer(state => batchBlock.TriggerBatch());

                var timeoutBlock = new TransformBlock<LogItem, LogItem>(
                    value =>
                    {
                        timer.Change((int)writer.Timeout.TotalMilliseconds,
                            Timeout.Infinite);

                        return value;
                    },
                    new ExecutionDataflowBlockOptions()
                    {
                        CancellationToken = cts.Token,
                        SingleProducerConstrained = true                        
                    });

                var writerBlock = new ActionBlock<LogItem[]>(
                    async logItems =>
                    {
                        await writer.WriteAsync(logItems);
                    },
                    new ExecutionDataflowBlockOptions()
                    {
                        MaxDegreeOfParallelism = 1,
                        CancellationToken = cts.Token,
                        SingleProducerConstrained = true
                    });

                writerBlock.Completion.ContinueWith(task => writer.Teardown());

                timeoutBlock.LinkTo(batchBlock);
                bufferBlock.LinkTo(timeoutBlock);
                batchBlock.LinkTo(writerBlock);

                writerBlocks.Add(writerBlock);
                bufferBlocks.Add(bufferBlock);

                timeoutBlock.HandleCompletion(batchBlock);
                bufferBlock.HandleCompletion(timeoutBlock);
                batchBlock.HandleCompletion(writerBlock);
            }
        }

        ~Logger()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        public void Cancel()
        {
            cts.Cancel();
        }

        private void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                foreach (var bufferBlock in bufferBlocks)
                    bufferBlock.Complete();

                try
                {
                    Task.WaitAll(writerBlocks.Select(wb => wb.Completion).ToArray());
                }
                catch (AggregateException errors)
                {
                    if (OnFailure != null)
                        OnFailure(this, new GenericArgs<AggregateException>(errors));
                }
                catch (TaskCanceledException)
                {
                    if (OnCancelled != null)
                        OnCancelled(this, EventArgs.Empty);
                }
                catch (Exception error)
                {
                    if (OnFailure != null)
                    {
                        OnFailure(this, new GenericArgs<AggregateException>(
                            new AggregateException(error)));
                    }
                }
            }

            disposed = true;
        }

        public void Log(LogLevel status, string format, params object[] args)
        {
            var logItem = new LogItem(status, string.Format(format, args));

            foreach (var bufferBlock in bufferBlocks)
            {
                if (!bufferBlock.Post(logItem))
                    throw new ObjectDisposedException("Logger");
            }
        }
    }
}

