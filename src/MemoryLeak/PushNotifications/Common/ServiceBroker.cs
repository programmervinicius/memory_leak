namespace MemoryLeak.PushNotifications.Common
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using NLog;
    using NLog.Fluent;

    public class ServiceBroker<TNotification> : IServiceBroker<TNotification> where TNotification : INotification
    {
        public event NotificationSuccessDelegate<TNotification> OnNotificationSucceeded;

        public event NotificationFailureDelegate<TNotification> OnNotificationFailed;

        public int ScaleSize { get; private set; }

        public IServiceConnectionFactory<TNotification> ServiceConnectionFactory { get; }

        private readonly BlockingCollection<TNotification> notifications;

        private readonly List<ServiceWorker<TNotification>> workers;

        private readonly object lockWorkers;

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private bool running;

        public ServiceBroker(IServiceConnectionFactory<TNotification> connectionFactory)
        {
            this.ServiceConnectionFactory = connectionFactory;

            this.lockWorkers = new object();
            this.workers = new List<ServiceWorker<TNotification>>();
            this.running = false;

            this.notifications = new BlockingCollection<TNotification>();
            this.ScaleSize = 1;
        }

        public void QueueNotification(TNotification notification)
        {
            this.notifications.Add(notification);
        }

        public IEnumerable<TNotification> TakeMany()
        {
            return this.notifications.GetConsumingEnumerable();
        }

        public bool IsCompleted => this.notifications.IsCompleted;

        public void Start()
        {
            if (this.running)
            {
                return;
            }

            this.running = true;
            this.ChangeScale(this.ScaleSize);
        }

        public void Stop(bool immediately = false)
        {
            if (!this.running)
            {
                throw new OperationCanceledException("ServiceBroker has already been signaled to Stop");
            }

            this.running = false;

            this.notifications.CompleteAdding();

            lock (this.lockWorkers)
            {
                // Kill all workers right away
                if (immediately)
                {
                    this.workers.ForEach(sw => sw.Cancel());
                }

                var all = (from sw in this.workers
                                       select sw.WorkerTask).ToArray();

                this.logger.Info()
                           .Message("Stopping: Waiting on Tasks")
                           .Write();

                Task.WaitAll(all);

                this.logger.Info()
                           .Message("Stopping: Done Waiting on Tasks")
                           .Write();

                this.workers.Clear();
            }
        }

        public void ChangeScale(int newScaleSize)
        {
            if (newScaleSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newScaleSize), "Must be Greater than Zero");
            }

            this.ScaleSize = newScaleSize;

            if (!this.running)
            {
                return;
            }
            
            lock (this.lockWorkers)
            {
                // Scale down
                while (this.workers.Count > this.ScaleSize)
                {
                    this.workers[0].Cancel();
                    this.workers.RemoveAt(0);
                }

                // Scale up
                while (this.workers.Count < this.ScaleSize)
                {
                    var worker = new ServiceWorker<TNotification>(this, this.ServiceConnectionFactory.Create());
                    this.workers.Add(worker);
                    worker.Start();
                }

                this.logger.Debug()
                           .Message($"Scaled Changed to: {this.workers.Count}")
                           .Write();
            }
        }

        public void RaiseNotificationSucceeded(TNotification notification)
        {
            this.OnNotificationSucceeded?.Invoke(notification);
        }

        public void RaiseNotificationFailed(TNotification notification, AggregateException exception)
        {
            this.OnNotificationFailed?.Invoke(notification, exception);
        }
    }

    internal class ServiceWorker<TNotification> where TNotification : INotification
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        public ServiceWorker(IServiceBroker<TNotification> broker, IServiceConnection<TNotification> connection)
        {
            this.Broker = broker;
            this.Connection = connection;

            this.CancelTokenSource = new CancellationTokenSource();
        }

        public IServiceBroker<TNotification> Broker { get; }

        public IServiceConnection<TNotification> Connection { get; }

        public CancellationTokenSource CancelTokenSource { get; }

        public Task WorkerTask { get; private set; }

        public void Start()
        {
            this.WorkerTask = Task.Factory.StartNew(async () =>
            {
                while (!this.CancelTokenSource.IsCancellationRequested && !this.Broker.IsCompleted)
                {
                    try
                    {
                        var toSend = new List<Task>();
                        foreach (var notification in this.Broker.TakeMany())
                        {
                            var t = this.Connection.Send(notification);
                            // Keep the continuation
                            var cont = t.ContinueWith(ct =>
                            {
                                var cn = notification;
                                var ex = t.Exception;

                                if (ex == null)
                                {
                                    this.Broker.RaiseNotificationSucceeded(cn);
                                }
                                else
                                {
                                    this.Broker.RaiseNotificationFailed(cn, ex);
                                }
                            });

                            // Let's wait for the continuation not the task itself
                            toSend.Add(cont);
                        }

                        if (toSend.Count <= 0)
                        {
                            continue;
                        }

                        try
                        {
                            this.logger.Info()
                                       .Message($"Waiting on all tasks {toSend.Count}")
                                       .Write();

                            await Task.WhenAll(toSend).ConfigureAwait(false);

                            this.logger.Info()
                                       .Message("All Tasks Finished")
                                       .Write();
                        }
                        catch (Exception exception)
                        {
                            this.logger.Error()
                                       .Message("Waiting on all tasks Failed")
                                       .Exception(exception)
                                       .Write();
                        }

                        this.logger.Info()
                                   .Message("Passed WhenAll")
                                   .Write();
                    }
                    catch (Exception exception)
                    {
                        this.logger.Error()
                                   .Message("Broker.Take")
                                   .Exception(exception)
                                   .Write();
                    }
                }

                if (this.CancelTokenSource.IsCancellationRequested)
                {
                    this.logger.Info()
                               .Message("Cancellation was requested")
                               .Write();
                }
                if (this.Broker.IsCompleted)
                {
                    this.logger.Info()
                               .Message("Broker IsCompleted")
                               .Write();
                }

                this.logger.Debug()
                           .Message("Broker Task Ended")
                           .Write();
            }, 
            this.CancelTokenSource.Token, 
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

            this.WorkerTask.ContinueWith(t => 
            {
                var exception = t.Exception;
                if (exception != null)
                {
                    this.logger.Error()
                               .Exception(exception)
                               .Message("ServiceWorker.WorkerTask Error")
                               .Write();                              
                }
            }, 
            TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Cancel()
        {
            this.CancelTokenSource.Cancel();
        }
    }
}
