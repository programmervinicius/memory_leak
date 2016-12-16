namespace MemoryLeak
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using NLog;
    using NLog.Config;
    using NLog.Targets;
    using PushNotifications;
    using PushNotifications.Apple;

    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine(Process.GetCurrentProcess().Id);

            EnableConsoleLogging();

            var apnsHost = "gateway.push.apple.com";
            var apnsPort = 2195;
            var apnsFeedBackHost = "feedback.push.apple.com";
            var apnsFeedBackPort = 2196;
            var certificateFilePath = "";
            var certificatePassword = "";

            var certificate = new X509Certificate2(
                File.ReadAllBytes(certificateFilePath),
                certificatePassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            var applePushNotificationService = new ApplePushNotificationServiceProvider(
                new ApnsConfiguration(
                    certificate,
                    apnsHost,
                    apnsPort,
                    apnsFeedBackHost,
                    apnsFeedBackPort));
            applePushNotificationService.StartServiceProvider();

            while (true)
            {
            }
        }

        private static void EnableConsoleLogging()
        {
            var config = new LoggingConfiguration();
            var target = new ColoredConsoleTarget
            {
                Layout =
                    @"${date:format=HH\:mm\:ss.fff} ${level:uppercase=true:padding=6} ${threadid:padding=4} ${callsite:className=false:fileName=false:includeSourcePath=false:methodName=true} ${message} ${all-event-properties} ${exception:format=tostring}"
            };

            var rule = new LoggingRule("*", LogLevel.Trace, target);
            config.LoggingRules.Add(rule);
            LogManager.Configuration = config;
        }
    }
}