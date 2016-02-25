﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public abstract class JobQueueTestsBase: TestWithLoggingBase {
        public JobQueueTestsBase(ITestOutputHelper output) : base(output) { }

        protected abstract IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay);
        
        public virtual async Task CanRunQueueJob() {
            const int workItemCount = 100;
            var queue = GetSampleWorkItemQueue(retries: 0, retryDelay: TimeSpan.Zero);
            await queue.DeleteQueueAsync();

            var enqueueTask = Run.InParallel(workItemCount, async index => {
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = "somepath" + index
                });
            });

            var job = new SampleQueueJob(queue, null, Log);
            await Task.Delay(10);
            await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask);

            var stats = await queue.GetQueueStatsAsync();
            Assert.Equal(0, stats.Queued);
            Assert.Equal(workItemCount, stats.Enqueued);
            Assert.Equal(workItemCount, stats.Dequeued);
        }
        
        public virtual async Task CanRunMultipleQueueJobs() {
            const int jobCount = 5;
            const int workItemCount = 100;

            Log.SetLogLevel<SampleQueueJob>(LogLevel.Error);
            Log.SetLogLevel<InMemoryMetricsClient>(LogLevel.Trace);

            var metrics = new InMemoryMetricsClient(true, loggerFactory: Log);

            var queues = new List<IQueue<SampleQueueWorkItem>>();
            for (int i = 0; i < jobCount; i++) {
                var q = GetSampleWorkItemQueue(retries: 3, retryDelay: TimeSpan.FromSeconds(1));
                await q.DeleteQueueAsync();
                q.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test", Log));
                queues.Add(q);
            }

            var enqueueTask = Run.InParallel(workItemCount, async index => {
                var queue = queues[RandomData.GetInt(0, jobCount - 1)];
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = RandomData.GetString()
                });
            });

            var cancellationTokenSource = new CancellationTokenSource();
            await Run.InParallel(jobCount, async index => {
                var queue = queues[index - 1];
                var job = new SampleQueueJob(queue, metrics, Log);
                await job.RunUntilEmptyAsync(cancellationTokenSource.Token);
                cancellationTokenSource.Cancel();
            });

            await enqueueTask;

            var queueStats = new List<QueueStats>();
            for (int i = 0; i < queues.Count; i++) {
                var stats = await queues[i].GetQueueStatsAsync();
                _logger.Info("Queue#{i}: Working: {working} Completed: {completed} Abandoned: {abandoned} Error: {errors} Deadletter: {deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                queueStats.Add(stats);
            }

            await metrics.FlushAsync();

            var counter = await metrics.GetCounterStatsAsync("completed");
            Assert.Equal(queueStats.Sum(s => s.Completed), counter.Count);
            Assert.InRange(queueStats.Sum(s => s.Completed), 0, workItemCount);
         }
    }
}