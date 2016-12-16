namespace MemoryLeak.PushNotifications
{
    using System;
    using Apple;

    public class ApplePushNotificationServiceProvider : IApplePushNotificationServiceProvider
    {
        private readonly ApnsServiceBroker apnsServiceBroker;

        private readonly ApnsFeedbackService apnsFeedbackService;

        private bool started;

        public ApplePushNotificationServiceProvider(ApnsConfiguration configuration)
        {;
            this.apnsServiceBroker = new ApnsServiceBroker(configuration);
            this.apnsServiceBroker.OnNotificationSucceeded += this.OnNotificationSucceeded;
            this.apnsServiceBroker.OnNotificationFailed += this.OnNotificationFailed;

            this.apnsFeedbackService = new ApnsFeedbackService(configuration);
            this.apnsFeedbackService.FeedbackReceived += this.OnApnsFeedbackServiceFeedbackReceived;
        }

        public void QueueApnsNotification(ApnsNotification notification)
        {
            if (!this.started)
            {
                throw new InvalidOperationException("You must start service before");
            }

            this.apnsServiceBroker.QueueNotification(notification);
        }

        public void StartServiceProvider()
        {
            this.apnsServiceBroker.Start();
            this.apnsFeedbackService.StarFetchFeedback();

            this.started = true;
        }

        private void OnApnsFeedbackServiceFeedbackReceived(string deviceToken, DateTime timestamp)
        {
            this.RemoveTokenAsync(deviceToken);
        }

        private async void OnNotificationSucceeded(ApnsNotification notification)
        {
        }

        private void OnNotificationFailed(ApnsNotification notification, AggregateException exception)
        {
            exception.Handle(ex => 
            {
                // See what kind of exception it was to further diagnose
                if (ex is ApnsNotificationException)
                {
                    var notificationException = (ApnsNotificationException)ex;

                    // Deal with the failed notification
                    var apnsNotification = notificationException.Notification;
                    var statusCode = notificationException.ErrorStatusCode;

                    if (statusCode == ApnsNotificationErrorStatusCode.InvalidToken)
                    {
                        this.RemoveTokenAsync(apnsNotification.DeviceToken);
                    }
                    
                }
                else
                {
                    // Inner exception might hold more useful information like an ApnsConnectionException           
       
                }

                // Mark it as handled
                return true;
            });
        }

        private async void RemoveTokenAsync(string deviceToken)
        {
        }
    }
}