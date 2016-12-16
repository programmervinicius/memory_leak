namespace MemoryLeak.PushNotifications.Apple
{
    using System;
    using System.Security.Cryptography.X509Certificates;

    public class ApnsConfiguration
    {
        public ApnsConfiguration(
            X509Certificate2 certificate,
            string apnsHost,
            int apnsPort,
            string apnsFeedBackHost,
            int apnsFeedBackPort)
        {
            this.Host = apnsHost;
            this.FeedbackHost = apnsFeedBackHost;
            this.Port = apnsPort;
            this.FeedbackPort = apnsFeedBackPort;

            this.Certificate = certificate;

            this.MillisecondsToWaitBeforeMessageDeclaredSuccess = 3000;
            this.ConnectionTimeout = 10000;
            this.MaxConnectionAttempts = 3;

            this.FeedbackIntervalMinutes = 10;
            this.FeedbackTimeIsUtc = false;
            this.ValidateServerCertificate = false;

            this.KeepAlivePeriod = TimeSpan.FromMinutes(20);
            this.KeepAliveRetryPeriod = TimeSpan.FromSeconds(30);

            this.InternalBatchSize = 1000;
            this.InternalBatchingWaitPeriod = TimeSpan.FromMilliseconds(750);

            this.InternalBatchFailureRetryCount = 1;
        }

        public string Host { get; private set; }

        public int Port { get; private set; }

        public string FeedbackHost { get; private set; }

        public int FeedbackPort { get; private set; }

        public X509Certificate2 Certificate { get; private set; }

        public bool SkipSsl { get; set; }

        public int MillisecondsToWaitBeforeMessageDeclaredSuccess { get; set; }

        public int FeedbackIntervalMinutes { get; set; }

        public bool FeedbackTimeIsUtc { get; private set; }

        public bool ValidateServerCertificate { get; private set; }

        public int ConnectionTimeout { get; set; }

        public int MaxConnectionAttempts { get; set; }

        /// <summary>
        /// The internal connection to APNS servers batches notifications to send before waiting for errors for a short time.
        /// This value will set a maximum size per batch.  The default value is 1000.  You probably do not want this higher than 7500.
        /// </summary>
        /// <value>The size of the internal batch.</value>
        public int InternalBatchSize { get; private set; }

        /// <summary>
        /// How long the internal connection to APNS servers should idle while collecting notifications in a batch to send.
        /// Setting this value too low might result in many smaller batches being used.
        /// </summary>
        /// <value>The internal batching wait period.</value>
        public TimeSpan InternalBatchingWaitPeriod { get; private set; }

        /// <summary>
        /// How many times the internal batch will retry to send in case of network failure. The default value is 1.
        /// </summary>
        /// <value>The internal batch failure retry count.</value>
        public int InternalBatchFailureRetryCount { get; private set; }

        /// <summary>
        /// Gets or sets the keep alive period to set on the APNS socket
        /// </summary>
        public TimeSpan KeepAlivePeriod { get; private set; }

        /// <summary>
        /// Gets or sets the keep alive retry period to set on the APNS socket
        /// </summary>
        public TimeSpan KeepAliveRetryPeriod { get; }
    }
}