﻿namespace MemoryLeak.PushNotifications.Apple
{
    using System;
    using System.Collections.Generic;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading.Tasks;

    public class ApnsFeedbackService
    {
        public ApnsConfiguration Configuration { get; }

        public delegate void FeedbackReceivedDelegate(string deviceToken, DateTime timestamp);

        public event FeedbackReceivedDelegate FeedbackReceived;

        public ApnsFeedbackService(ApnsConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public void StarFetchFeedback()
        {
            Task.Factory.StartNew(async () =>
            {
                var certificate = this.Configuration.Certificate;

                var certificates = new X509CertificateCollection { certificate };

                var client = new TcpClient();

                await client.ConnectAsync(this.Configuration.FeedbackHost, this.Configuration.FeedbackPort);

                var stream = new SslStream(client.GetStream(), true,
                    (sender, cert, chain, sslErrs) => true,
                    (sender, targetHost, localCerts, remoteCert, acceptableIssuers) => certificate);

                await stream.AuthenticateAsClientAsync(this.Configuration.FeedbackHost, certificates, System.Security.Authentication.SslProtocols.Tls, false);

                //Set up
                byte[] buffer = new byte[4096];
                int recd = 0;
                var data = new List<byte>();

                //Get the first feedback
                recd = stream.Read(buffer, 0, buffer.Length);

                //Continue while we have results and are not disposing
                while (recd > 0)
                {
                    // Add the received data to a list buffer to work with (easier to manipulate)
                    for (int i = 0; i < recd; i++)
                    {
                        data.Add(buffer[i]);
                    }

                    //Process each complete notification "packet" available in the buffer
                    while (data.Count >= (4 + 2 + 32)) // Minimum size for a valid packet
                    {
                        var secondsBuffer = data.GetRange(0, 4).ToArray();
                        var tokenLengthBuffer = data.GetRange(4, 2).ToArray();

                        // Get our seconds since epoch
                        // StarFetchFeedback endianness and reverse if needed
                        if (BitConverter.IsLittleEndian)
                        {
                            Array.Reverse(secondsBuffer);
                        }
                            
                        var seconds = BitConverter.ToInt32(secondsBuffer, 0);

                        //Add seconds since 1970 to that date, in UTC
                        var timestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

                        //flag to allow feedback times in UTC or local, but default is local
                        if (!this.Configuration.FeedbackTimeIsUtc)
                        {
                            timestamp = timestamp.ToLocalTime();
                        }

                        if (BitConverter.IsLittleEndian)
                        {
                            Array.Reverse(tokenLengthBuffer);
                        }
                            
                        var tokenLength = BitConverter.ToInt16(tokenLengthBuffer, 0);

                        if (data.Count >= 4 + 2 + tokenLength)
                        {
                            var tokenBuffer = data.GetRange(6, tokenLength).ToArray();
                            var token = BitConverter.ToString(tokenBuffer).Replace("-", string.Empty).ToLower().Trim();

                            // Remove what we parsed from the buffer
                            data.RemoveRange(0, 4 + 2 + tokenLength);

                            // Raise the event to the consumer
                            this.FeedbackReceived?.Invoke(token, timestamp);
                        }
                    }

                    //Read the next feedback
                    recd = stream.Read(buffer, 0, buffer.Length);
                }

                try
                {
                    stream.Dispose();
                }
                catch{}

                try
                {
                    client.Client.Shutdown(SocketShutdown.Both);
                    client.Client.Dispose();
                }
                catch { }

                try { client.Dispose(); } catch { }
            }, 
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach).Unwrap();
        }
    }
}
