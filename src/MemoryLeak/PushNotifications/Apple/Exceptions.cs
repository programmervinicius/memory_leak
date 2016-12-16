namespace MemoryLeak.PushNotifications.Apple
{
    using System;
    using Common;

    public enum ApnsNotificationErrorStatusCode
    {
        NoErrors = 0,
        ProcessingError = 1,
        MissingDeviceToken = 2,
        MissingTopic = 3,
        MissingPayload = 4,
        InvalidTokenSize = 5,
        InvalidTopicSize = 6,
        InvalidPayloadSize = 7,
        InvalidToken = 8,
        Shutdown = 10,
        ConnectionError = 254,
        Unknown = 255
    }

    public class ApnsNotificationException : NotificationException
    {
        public ApnsNotificationException(byte errorStatusCode, ApnsNotification notification)
            : this(ToErrorStatusCode(errorStatusCode), notification)
        { }

        public ApnsNotificationException(ApnsNotificationErrorStatusCode errorStatusCode, ApnsNotification notification)
            : base("Apns notification error: '" + errorStatusCode + "'", notification)
        {
            this.Notification = notification;
            this.ErrorStatusCode = errorStatusCode;
        }

        public ApnsNotificationException(ApnsNotificationErrorStatusCode errorStatusCode, ApnsNotification notification, Exception innerException)
            : base("Apns notification error: '" + errorStatusCode + "'", notification, innerException)
        {
            this.Notification = notification;
            this.ErrorStatusCode = errorStatusCode;
        }

        public new ApnsNotification Notification { get; }
        public ApnsNotificationErrorStatusCode ErrorStatusCode { get; private set; }
        
        private static ApnsNotificationErrorStatusCode ToErrorStatusCode(byte errorStatusCode)
        {
            ApnsNotificationErrorStatusCode statusCode = ApnsNotificationErrorStatusCode.Unknown;

            Enum.TryParse(errorStatusCode.ToString(), out statusCode);

            return statusCode;
        }
    }

    public class ApnsConnectionException : Exception
    {
        public ApnsConnectionException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}