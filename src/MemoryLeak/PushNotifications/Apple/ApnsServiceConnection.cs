namespace MemoryLeak.PushNotifications.Apple
{
    using System.Threading.Tasks;
    using Common;

    public class ApnsServiceConnectionFactory : IServiceConnectionFactory<ApnsNotification>
    {
        public ApnsServiceConnectionFactory(ApnsConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public ApnsConfiguration Configuration { get; }

        public IServiceConnection<ApnsNotification> Create()
        {
            return new ApnsServiceConnection(this.Configuration);
        }
    }

    public class ApnsServiceBroker : ServiceBroker<ApnsNotification>
    {
        public ApnsServiceBroker(ApnsConfiguration configuration) : base(new ApnsServiceConnectionFactory (configuration))
        {
        }
    }

    public class ApnsServiceConnection : IServiceConnection<ApnsNotification>
    {
        private readonly ApnsConnection connection;

        public ApnsServiceConnection(ApnsConfiguration configuration)
        {
            this.connection = new ApnsConnection(configuration);
        }
        
        public async Task Send(ApnsNotification notification)
        {
            var completableNotification = new ApnsConnection.CompletableApnsNotification(notification);

            this.connection.Send(completableNotification);

            var ex = await completableNotification.WaitForComplete().ConfigureAwait(false);

            if (ex != null)
            {
                throw ex;
            }
        }
    }
}