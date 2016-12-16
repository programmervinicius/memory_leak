namespace MemoryLeak.PushNotifications.Apple
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;
    using NLog;
    using NLog.Fluent;

    public class ApnsConnection
    {
        private static int nextConnectionId;

        private readonly X509CertificateCollection certificates;

        private readonly X509Certificate2 certificate;

        private readonly SemaphoreSlim connectingSemaphore = new SemaphoreSlim(1);

        private readonly SemaphoreSlim batchSendSemaphore = new SemaphoreSlim(1);

        private readonly object notificationBatchQueueLock = new object();

        private readonly Queue<CompletableApnsNotification> notifications = new Queue<CompletableApnsNotification>();

        private readonly List<SentNotification> sent = new List<SentNotification>();

        private readonly Timer timerBatchWait;

        private readonly byte[] buffer = new byte[6];

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly int currentConnectionId;

        private TcpClient client;

        private SslStream stream;

        private Stream networkStream;

        private long batchId;

        public ApnsConfiguration Configuration { get; }

        public ApnsConnection(ApnsConfiguration configuration)
        {
            this.currentConnectionId = ++nextConnectionId;
            if (this.currentConnectionId >= int.MaxValue)
            {
                nextConnectionId = 0;
            }

            this.Configuration = configuration;

            this.certificate = this.Configuration.Certificate;

            this.certificates = new X509CertificateCollection();

            // Finally, add the main private cert for authenticating to our collection
            if (this.certificate != null)
            {
                this.certificates.Add(this.certificate);
            }

            this.timerBatchWait = new Timer(async state => 
            {
                await this.batchSendSemaphore.WaitAsync();
                try
                {
                    await this.SendNotificationsBatch().ConfigureAwait(false);
                }
                finally
                {
                    this.batchSendSemaphore.Release();
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Send(CompletableApnsNotification notification)
        {
            lock (this.notificationBatchQueueLock)
            {
                this.notifications.Enqueue(notification);

                if (this.notifications.Count >= this.Configuration.InternalBatchSize)
                {
                    // Make the timer fire immediately and send a batch off
                    this.timerBatchWait.Change(0, Timeout.Infinite);

                    return;
                }

                // Restart the timer to wait for more notifications to be batched
                //  This timer will keep getting 'restarted' before firing as long as notifications
                //  are queued before the timer's due time
                //  if the timer is actually called, it means no more notifications were queued, 
                //  so we should flush out the queue instead of waiting for more to be batched as they
                //  might not ever come and we don't want to leave anything stranded in the queue
                this.timerBatchWait.Change(this.Configuration.InternalBatchingWaitPeriod, Timeout.InfiniteTimeSpan);
            }
        }

        private async Task SendNotificationsBatch()
        {
            this.batchId++;
            if (this.batchId >= long.MaxValue)
            {
                this.batchId = 1;
            }  
            
            // Pause the timer
            this.timerBatchWait.Change(Timeout.Infinite, Timeout.Infinite);

            if (this.notifications.Count <= 0)
            {
                return;
            }

            // Let's store the batch items to send internally
            var toSend = new List<CompletableApnsNotification>();

            while (this.notifications.Count > 0 && toSend.Count < this.Configuration.InternalBatchSize)
            {
                var notification = this.notifications.Dequeue();
                toSend.Add(notification);
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Sending Batch ID={this.batchId}, Count={toSend.Count}")
                       .Write();
            try
            {
                var data = CreateBatch(toSend);

                if (data != null && data.Length > 0)
                {
                    for (var i = 0; i <= this.Configuration.InternalBatchFailureRetryCount; i++)
                    {
                        await this.connectingSemaphore.WaitAsync();

                        try
                        {
                            // See if we need to Connect
                            if (!this.SocketCanWrite() || i > 0)
                            {
                                await this.Connect();
                            } 
                        }
                        finally
                        {
                            this.connectingSemaphore.Release();
                        }
                
                        try
                        {
                            await this.networkStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);

                            break;
                        }
                        catch (Exception ex) when (i != this.Configuration.InternalBatchFailureRetryCount)
                        {
                            this.logger.Info()
                                       .Message($"APNS-CLIENT[{this.currentConnectionId}]: Retrying Batch: Batch ID={this.batchId}")
                                       .Exception(ex)
                                       .Write();
                        }
                    }

                    foreach (var notification in toSend)
                    {
                        this.sent.Add(new SentNotification(notification));
                    }
                }
            }
            catch (Exception exception)
            {
                this.logger.Error()
                           .Message($"APNS-CLIENT[{this.currentConnectionId}]: Send Batch Error: Batch ID={this.batchId}")
                           .Exception(exception)
                           .Write();

                foreach (var notification in toSend)
                {
                    notification.CompleteFailed(new ApnsNotificationException(
                        ApnsNotificationErrorStatusCode.ConnectionError,
                        notification.Notification, 
                        exception));
                }
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Sent Batch, waiting for possible response...")
                       .Write();
            try
            {
                await this.ReadApnsResponse();
            }
            catch (Exception exception)
            {
                this.logger.Info()
                           .Message($"APNS-Client[{this.currentConnectionId}]: ReadApnsResponse Exception")
                           .Exception(exception)
                           .Write();
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Done Reading for Batch ID={this.batchId}, reseting batch timer...")
                       .Write();

            // Restart the timer for the next batch
            this.timerBatchWait.Change(this.Configuration.InternalBatchingWaitPeriod, Timeout.InfiniteTimeSpan);
        }

        private static byte[] CreateBatch(List<CompletableApnsNotification> toSend)
        {
            if (toSend == null || toSend.Count <= 0)
            {
                return null;
            }
            
            var batchData = new List<byte>();

            // Add all the frame data
            foreach (var notification in toSend)
            {
                batchData.AddRange(notification.Notification.ToBytes());
            }
                
            return batchData.ToArray();
        }

        private async Task ReadApnsResponse()
        {
            var readCancelToken = new CancellationTokenSource();

            // We are going to read from the stream, but the stream *may* not ever have any data for us to
            // read (in the case that all the messages sent successfully, apple will send us nothing
            // So, let's make our read timeout after a reasonable amount of time to wait for apple to tell
            // us of any errors that happened.
            readCancelToken.CancelAfter(750);

            int len = -1;

            while (!readCancelToken.IsCancellationRequested)
            {
                // See if there's data to read
                if (this.client.Client.Available > 0)
                {
                    this.logger.Info()
                               .Message($"APNS-Client[{this.currentConnectionId}]: Data Available...")
                               .Write();

                    len = await this.networkStream.ReadAsync(this.buffer, 0, this.buffer.Length).ConfigureAwait(false);

                    this.logger.Info()
                               .Message($"APNS-Client[{this.currentConnectionId}]: Finished Read.")
                               .Write();
                    break;
                }

                // Let's not tie up too much CPU waiting...
                await Task.Delay(50).ConfigureAwait(false);
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Received {len} bytes response...")
                       .Write();

            // If we got no data back, and we didn't end up canceling, the connection must have closed
            if (len == 0)
            {
                this.logger.Info()
                           .Message($"APNS-Client[{this.currentConnectionId}]: Server Closed Connection...")
                           .Write();

                // Connection was closed
                this.Disconnect();

                return;
            }

            if (len < 0)
            { 
                //If we timed out waiting, but got no data to read, everything must be ok!
                this.logger.Info()
                           .Message($"APNS-Client[{this.currentConnectionId}]: Batch (ID={this.batchId}) completed with no error response...")
                           .Write();

                //Everything was ok, let's assume all 'sent' succeeded
                foreach (var notification in this.sent)
                {
                    notification.Notification.CompleteSuccessfully();
                }

                this.sent.Clear();

                return;
            }

            // If we make it here, we did get data back, so we have errors
            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Batch (ID={this.batchId}) completed with error response...")
                       .Write();

            // If we made it here, we did receive some data, so let's parse the error
            var status = this.buffer[1];
            var identifier = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(this.buffer, 2));

            // Let's handle the failure
            //Get the index of our failed notification (by identifier)
            var failedIndex = this.sent.FindIndex(n => n.Identifier == identifier);

            // If we didn't find an index for the failed notification, something is wrong
            // Let's just return
            if (failedIndex < 0)
            {
                return;
            }

            // Get all the notifications before the failed one and mark them as sent!
            if (failedIndex > 0)
            {
                // Get all the notifications sent before the one that failed
                // We can assume that these all succeeded
                var successful = this.sent.GetRange(0, failedIndex); //TODO: Should it be failedIndex - 1?

                // Complete all the successfully sent notifications
                foreach (var successNotification in successful)
                {
                    successNotification.Notification.CompleteSuccessfully();
                }

                // Remove all the successful notifications from the sent list
                // This should mean the failed notification is now at index 0
                this.sent.RemoveRange(0, failedIndex);
            }

            //Get the failed notification itself
            var failedNotification = this.sent[0];

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Failing Notification {failedNotification.Identifier}")
                       .Write();

            failedNotification.Notification.CompleteFailed(
                new ApnsNotificationException(status, failedNotification.Notification.Notification));

            // Now remove the failed notification from the sent list
            this.sent.RemoveAt(0);

            // The remaining items in the list were sent after the failed notification
            // we can assume these were ignored by apple so we need to send them again
            // Requeue the remaining notifications
            foreach (var sentNotification in this.sent)
            {
                this.notifications.Enqueue(sentNotification.Notification);
            }

            // Clear our sent list
            this.sent.Clear();

            // Apple will close our connection after this anyway
            this.Disconnect();
        }

        private bool SocketCanWrite()
        {
            if (this.client == null)
            {
                return false;
            }
           
            if (this.networkStream == null || !this.networkStream.CanWrite)
            {
                return false;
            }

            if (!this.client.Client.Connected)
            {
                return false;
            } 

            var result = this.client.Client.Poll(1000, SelectMode.SelectWrite);

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Can Write? {result}")
                       .Write();

            return result;
        }

        private async Task Connect()
        {
            if (this.client != null)
            {
                this.Disconnect();
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Connecting (Batch ID={this.batchId})")
                       .Write();

            this.client = new TcpClient();

            try
            {
                await this.client.ConnectAsync(this.Configuration.Host, this.Configuration.Port).ConfigureAwait(false);

                //Set keep alive on the socket may help maintain our APNS connection
                try
                {
                    this.client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
                catch {}

                //Really not sure if this will work on MONO....
                // This may help windows azure users
                try
                {
                    SetSocketKeepAliveValues(this.client.Client, (int)this.Configuration.KeepAlivePeriod.TotalMilliseconds, (int)this.Configuration.KeepAliveRetryPeriod.TotalMilliseconds);
                }
                catch {}
            }
            catch (Exception ex)
            {
                throw new ApnsConnectionException("Failed to Connect, check your firewall settings!", ex);
            }

            // We can configure skipping ssl all together, ie: if we want to hit a test server
            if (this.Configuration.SkipSsl)
            {
                this.networkStream = this.client.GetStream();
            }
            else
            {
                // Create our ssl stream
                this.stream = new SslStream(
                    this.client.GetStream(), 
                    false,
                    this.ValidateRemoteCertificate,
                    (sender, targetHost, localCerts, remoteCert, acceptableIssuers) => this.certificate);

                try
                {
                    await this.stream.AuthenticateAsClientAsync(this.Configuration.Host, this.certificates, System.Security.Authentication.SslProtocols.Tls, false);
                }
                catch (System.Security.Authentication.AuthenticationException ex)
                {
                    throw new ApnsConnectionException("SSL Stream Failed to Authenticate as Client", ex);
                }

                if (!this.stream.IsMutuallyAuthenticated)
                {
                    throw new ApnsConnectionException("SSL Stream Failed to Authenticate", null);
                }

                if (!this.stream.CanWrite)
                {
                    throw new ApnsConnectionException("SSL Stream is not Writable", null);
                }

                this.networkStream = this.stream;
            }

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Connected (Batch ID={this.batchId})")
                       .Write();
        }

        private void Disconnect()
        {
            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Disconnecting (Batch ID={this.batchId})")
                       .Write();
            //We now expect apple to close the connection on us anyway, so let's try and close things
            // up here as well to get a head start
            //Hopefully this way we have less messages written to the stream that we have to requeue
            try { this.stream.Dispose(); } catch { }
            try { this.networkStream.Dispose(); } catch { }
            try { this.client.Client.Shutdown(SocketShutdown.Both); } catch { }
            try { this.client.Client.Dispose(); } catch { }
            try { this.client.Dispose(); } catch { }

            this.client = null;
            this.networkStream = null;
            this.stream = null;

            this.logger.Info()
                       .Message($"APNS-Client[{this.currentConnectionId}]: Disconnected (Batch ID={this.batchId})")
                       .Write();
        }

        private bool ValidateRemoteCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors policyErrors)
        {
            if (this.Configuration.ValidateServerCertificate)
            {
                return policyErrors == SslPolicyErrors.None;
            }

            return true;
        }

        public class SentNotification
        {
            public SentNotification(CompletableApnsNotification notification)
            {
                this.Notification = notification;
                this.SentAt = DateTime.UtcNow;
                this.Identifier = notification.Notification.Identifier;
            }

            public CompletableApnsNotification Notification { get; }

            public DateTime SentAt { get; set; }

            public int Identifier { get; }
        }

        public class CompletableApnsNotification
        {
            public ApnsNotification Notification { get; }

            private readonly TaskCompletionSource<Exception> completionSource;

            public CompletableApnsNotification(ApnsNotification notification)
            {
                this.Notification = notification;
                this.completionSource = new TaskCompletionSource<Exception>();
            }

            public Task<Exception> WaitForComplete()
            {
                return this.completionSource.Task;
            }

            public void CompleteSuccessfully()
            {
                this.completionSource.SetResult(null);
            }

            public void CompleteFailed(Exception ex)
            {
                this.completionSource.SetResult(ex);
            }
        }

        /// <summary>
        /// Using IOControl code to configue socket KeepAliveValues for line disconnection detection(because default is toooo slow) 
        /// </summary>
        /// <param name="socket">Socket</param>
        /// <param name="keepAliveTime">The keep alive time. (ms)</param>
        /// <param name="keepAliveInterval">The keep alive interval. (ms)</param>
        public static void SetSocketKeepAliveValues(Socket socket, int keepAliveTime, int keepAliveInterval)
        {
            //KeepAliveTime: default value is 2hr
            //KeepAliveInterval: default value is 1s and Detect 5 times
            uint dummy = 0; //lenth = 4
            byte[] inOptionValues = new byte[System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 3]; //size = lenth * 3 = 12

            BitConverter.GetBytes((uint)1).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes((uint)keepAliveTime).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy));
            BitConverter.GetBytes((uint)keepAliveInterval).CopyTo(inOptionValues, System.Runtime.InteropServices.Marshal.SizeOf(dummy) * 2);
            // of course there are other ways to marshal up this byte array, this is just one way
            // call WSAIoctl via IOControl

            // .net 3.5 type
            socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
        }
    }
}
