namespace MemoryLeak.PushNotifications.Apple
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    using Common;

    public class ApnsNotification : INotification
    {
        public int Identifier { get; }

        public string DeviceToken { get; }

        public string Payload { get; }

        public string DistributionKey { get; set; }

        /// <summary>
        /// The expiration date after which Apple will no longer store and forward this push notification.
        /// If no value is provided, an assumed value of one year from now is used.  If you do not wish
        /// for Apple to store and forward, set this value to Notification.DoNotStore.
        /// </summary>
        public DateTime? Expiration { get; set; }

        public bool LowPriority { get; set; }

        public const int DeviceTokenBinaryMinSize = 32;

        public const int DeviceTokenStringMinSize = 64;

        public const int MaxPayloadSize = 2048; //will be 4096 soon

        public static readonly DateTime DoNotStore = DateTime.MinValue;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly object NextIdentifierLock = new object();

        private static int nextIdentifier = 1;

        public ApnsNotification(string deviceToken, string payload)
        {
            if (!string.IsNullOrEmpty(deviceToken) && deviceToken.Length < DeviceTokenStringMinSize)
            {
                throw new ArgumentException("Invalid DeviceToken Length", nameof(deviceToken));
            }

            this.DeviceToken = deviceToken;
            this.Payload = payload;
            this.Identifier = GetNextIdentifier();
        }

        public bool IsDeviceRegistrationIdValid()
        {
            var regex = new System.Text.RegularExpressions.Regex(@"^[0-9A-F]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return regex.Match(this.DeviceToken).Success;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(this.Payload))
            {
                return this.Payload;
            }

            return "{}";
        }

        public byte[] ToBytes()
        {
            var builder = new List<byte>();

            // 1 - Device Token
            if (string.IsNullOrEmpty(this.DeviceToken))
            {
                throw new NotificationException("Missing DeviceToken", this);
            }

            if (!this.IsDeviceRegistrationIdValid())
            {
                throw new NotificationException("Invalid DeviceToken", this);
            }  

            // Turn the device token into bytes
            byte[] deviceToken = new byte[this.DeviceToken.Length / 2];
            for (int i = 0; i < deviceToken.Length; i++)
            {
                try
                {
                    deviceToken[i] = byte.Parse(this.DeviceToken.Substring (i * 2, 2), System.Globalization.NumberStyles.HexNumber);
                }
                catch (Exception)
                {
                    throw new NotificationException("Invalid DeviceToken", this);
                }
            }

            if (deviceToken.Length < DeviceTokenBinaryMinSize)
            {
                throw new NotificationException("Invalid DeviceToken Length", this);
            }

            builder.Add(0x01); // Device Token ID
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Convert.ToInt16(deviceToken.Length))));
            builder.AddRange(deviceToken);

            // 2 - Payload
            var payload = Encoding.UTF8.GetBytes(this.ToString());
            if (payload.Length > MaxPayloadSize)
            {
                throw new NotificationException("Payload too large (must be " + MaxPayloadSize + " bytes or smaller", this);
            }

            builder.Add(0x02); // Payload ID
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Convert.ToInt16(payload.Length))));
            builder.AddRange(payload);

            // 3 - Identifier
            builder.Add(0x03);
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int16)4)));
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(this.Identifier)));

            // 4 - Expiration
            // APNS will not store-and-forward a notification with no expiry, so set it one year in the future
            // if the client does not provide it.
            int expiryTimeStamp = -1;
            if (this.Expiration != DoNotStore)
            {
                DateTime concreteExpireDateUtc = (this.Expiration ?? DateTime.UtcNow.AddMonths(1)).ToUniversalTime();
                TimeSpan epochTimeSpan = concreteExpireDateUtc - UnixEpoch;
                expiryTimeStamp = (int)epochTimeSpan.TotalSeconds;
            }

            builder.Add(0x04); // 4 - Expiry ID
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int16)4)));
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(expiryTimeStamp)));

            // 5 - Priority
            var priority = this.LowPriority ? (byte)5 : (byte)10;
            builder.Add(0x05); // 5 - Priority
            builder.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int16)1)));
            builder.Add(priority);

            var frameLength = builder.Count;

            builder.Insert(0, 0x02); // COMMAND 2 for new format

            // Insert the frame length
            builder.InsertRange(1, BitConverter.GetBytes(IPAddress.HostToNetworkOrder((Int32)frameLength)));

            return builder.ToArray();
        }

        private static int GetNextIdentifier()
        {
            lock (NextIdentifierLock)
            {
                if (nextIdentifier >= int.MaxValue - 10)
                {
                   nextIdentifier = 1; 
                }

                return nextIdentifier++;
            }
        }
    }
}