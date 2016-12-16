namespace MemoryLeak.PushNotifications
{
    using Apple;

    public interface IApplePushNotificationServiceProvider
    {
        void QueueApnsNotification(ApnsNotification notification);

        void StartServiceProvider();
    }
}