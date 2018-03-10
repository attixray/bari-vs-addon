using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KOTEM.BariVSPackage.BariExtension.Utils
{
    public class NotSuspendedException : Exception
    {
        public NotSuspendedException()
            : base("Not Suspended!")
        {
        }
    }

    internal class SuspendableBase
    {
        private long suspendCount = 0;

        public virtual bool IsSuspended => GetSuspendCount() > 0;

        /// <summary>
        /// <see cref="BeginSuspend"/> notification.
        /// </summary>
        protected virtual void OnBeginSuspend()
        {
            // Do nothing at this level...
        }

        /// <summary>
        /// <see cref="EndSuspend"/> notification. It is called the same number of times as the <see cref="OnBeginSuspend"/>.
        /// </summary>
        protected virtual void OnEndSuspend()
        {
            // Do nothing at this level...
        }

        public IDisposable BeginSuspend()
        {
            Interlocked.Increment(ref suspendCount);
            OnBeginSuspend();
            return new SimpleStateHolder(true, EndSuspend);
        }

        public virtual void EndSuspend()
        {
            var currentCount = Interlocked.Decrement(ref suspendCount);
            if (currentCount < 0)
            {
                throw new NotSuspendedException();
            }

            OnEndSuspend();
        }

        private long GetSuspendCount()
        {
            return Interlocked.Read(ref suspendCount);
        }
    }
}
