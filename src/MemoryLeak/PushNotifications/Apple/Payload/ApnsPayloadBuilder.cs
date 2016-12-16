namespace MemoryLeak.PushNotifications.Apple.Payload
{
    public class ApnsPayloadBuilder
    {
        public ApnsPayloadBuilder WithAlert(string title, string body)
        {
            return this;
        }

        public ApnsPayloadBuilder WithDefaultSound()
        {
            return this;
        }

        public ApnsPayloadBuilder WithBadge(int number)
        {
            return this;
        }

        public ApnsPayloadBuilder Silent()
        {
            return this;
        }

        public ApnsPayloadBuilder AddCustomData(string key, string value)
        {
            return this;
        }

        public string Build()
        {
            return string.Empty;
        }
    }
}