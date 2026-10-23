using System;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarDock
{
    /// <summary>
    /// Polls the taskbar on a background thread so UI Automation never blocks the
    /// animation. Snapshots are delivered on the UI thread of the dispatcher control.
    /// </summary>
    internal sealed class TaskbarMonitor : IDisposable
    {
        private const int PollInterval = 500;
        private readonly Control dispatcher;
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private readonly AutoResetEvent wakeSignal = new AutoResetEvent(false);
        private readonly Thread thread;
        private bool disposed;

        public event Action<TaskbarSnapshot> SnapshotAvailable;

        public TaskbarMonitor(Control dispatcher)
        {
            this.dispatcher = dispatcher;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TaskbarDock monitor"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void RequestRefresh()
        {
            if (!disposed)
                wakeSignal.Set();
        }

        private void Run()
        {
            var handles = new WaitHandle[] { stopSignal, wakeSignal };
            do
            {
                TaskbarSnapshot snapshot;
                try
                {
                    snapshot = TaskbarButtonSource.ReadSnapshot();
                }
                catch (Exception exception)
                {
                    AppTrace.Log("Taskbar read failed: " + exception.Message);
                    snapshot = null;
                }
                if (snapshot != null)
                    Deliver(snapshot);
            }
            while (WaitHandle.WaitAny(handles, PollInterval) != 0);
        }

        private void Deliver(TaskbarSnapshot snapshot)
        {
            if (disposed || !dispatcher.IsHandleCreated)
                return;
            try
            {
                dispatcher.BeginInvoke((MethodInvoker)delegate
                {
                    Action<TaskbarSnapshot> handler = SnapshotAvailable;
                    if (!disposed && handler != null)
                        handler(snapshot);
                });
            }
            catch (InvalidOperationException)
            {
                // The dispatcher is being destroyed during shutdown.
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            stopSignal.Set();
            thread.Join(1500);
        }
    }
}
