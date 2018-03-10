using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KOTEM.BariVSPackage.BariExtension.Utils
{
    public class STATaskScheduler : TaskScheduler, IDisposable
    {
        private readonly List<Thread> threads;
        private BlockingCollection<Task> tasks;
        private static int id;
        public override int MaximumConcurrencyLevel => threads.Count;

        public STATaskScheduler(int numberOfThreads)
        {
            var currentId = id++;
            if (numberOfThreads < 1)
            {
                throw new ArgumentOutOfRangeException("concurrencyLevel");
            }

            tasks = new BlockingCollection<Task>();

            threads = Enumerable.Range(0, numberOfThreads).Select(i =>
            {
                var thread = new Thread(() =>
                {
                    foreach (var t in tasks.GetConsumingEnumerable())
                    {
                        TryExecuteTask(t);
                    }
                });
                thread.IsBackground = true;
                thread.Name = string.Format("STAThread({0}-{1})", currentId, i);
                thread.SetApartmentState(ApartmentState.STA);
                return thread;
            }).ToList();

            threads.ForEach(t => t.Start());
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return tasks.ToArray();
        }

        protected override void QueueTask(Task task)
        {
            tasks.Add(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
                   TryExecuteTask(task);
        }

        public void Dispose()
        {
            if (tasks != null)
            {
                tasks.CompleteAdding();

                foreach (var thread in threads)
                    thread.Join();

                tasks.Dispose();
                tasks = null;
            }
        }
    }
}
