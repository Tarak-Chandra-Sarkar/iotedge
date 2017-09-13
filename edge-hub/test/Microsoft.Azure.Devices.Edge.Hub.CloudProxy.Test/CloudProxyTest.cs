﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.CloudProxy.Test
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.CloudProxy;
    using Microsoft.Azure.Devices.Edge.Hub.Core;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Hub.Mqtt;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Test.Common;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Azure.EventHubs;
    using Moq;
    using Newtonsoft.Json.Linq;
    using Xunit;

    [Bvt]
    public class CloudProxyTest
    {
        static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
        public static IEnumerable<object[]> GetTestMessages()
        {
            IList<IMessage> messages = MessageHelper.GenerateMessages(4);
            return new List<object[]> { new object[] { messages } };
        }

        public static IEnumerable<object[]> GetTestMessage()
        {
            IList<IMessage> messages = MessageHelper.GenerateMessages(1);
            return new List<object[]> { new object[] { messages[0] } };
        }

        [Theory]
        [MemberData(nameof(GetTestMessage))]
        public async Task SendMessageTest(IMessage message)
        {
            DateTime startTime = DateTime.UtcNow.Subtract(ClockSkew);
            Try<ICloudProxy> cloudProxy = await this.GetCloudProxyWithConnectionStringKey("device2ConnStrKey");
            Assert.True(cloudProxy.Success);
            await cloudProxy.Value.SendMessageAsync(message);
            bool disconnectResult = await cloudProxy.Value.CloseAsync();
            Assert.True(disconnectResult);
            await CheckMessageInEventHub(new List<IMessage> { message }, startTime);
        }

        [Theory]
        [MemberData(nameof(GetTestMessages))]
        public async Task SendMessageMultipleDevicesTest(IList<IMessage> messages)
        {
            DateTime startTime = DateTime.UtcNow.Subtract(ClockSkew);

            Try<ICloudProxy> cloudProxy1 = await this.GetCloudProxyWithConnectionStringKey("device2ConnStrKey");
            Assert.True(cloudProxy1.Success);

            Try<ICloudProxy> cloudProxy2 = await this.GetCloudProxyWithConnectionStringKey("device3ConnStrKey");
            Assert.True(cloudProxy2.Success);

            for (int i = 0; i < messages.Count; i = i + 2)
            {
                await cloudProxy1.Value.SendMessageAsync(messages[i]);
                await cloudProxy2.Value.SendMessageAsync(messages[i + 1]);             
            }

            bool disconnectResult = await cloudProxy1.Value.CloseAsync();
            Assert.True(disconnectResult);
            disconnectResult = await cloudProxy2.Value.CloseAsync();
            Assert.True(disconnectResult);

            await CheckMessageInEventHub(messages, startTime);
        }

        [Theory]
        [MemberData(nameof(GetTestMessages))]
        public async Task SendMessageBatchTest(IList<IMessage> messages)
        {
            DateTime startTime = DateTime.UtcNow.Subtract(ClockSkew);
            Try<ICloudProxy> cloudProxy = await this.GetCloudProxyWithConnectionStringKey("device2ConnStrKey");
            Assert.True(cloudProxy.Success);
            await cloudProxy.Value.SendMessageBatchAsync(messages);
            bool disconnectResult = await cloudProxy.Value.CloseAsync();
            Assert.True(disconnectResult);
            await CheckMessageInEventHub(messages, startTime);
        }

        [Fact]
        public async Task CanGetTwin()
        {
            Try<ICloudProxy> cloudProxy = await this.GetCloudProxyWithConnectionStringKey("device2ConnStrKey");
            Assert.True(cloudProxy.Success);
            IMessage result = await cloudProxy.Value.GetTwinAsync();
            string actualString = System.Text.Encoding.UTF8.GetString(result.Body);
            Assert.StartsWith("{", actualString);
            bool disconnectResult = await cloudProxy.Value.CloseAsync();
            Assert.True(disconnectResult);
        }

        [Fact]
        public async Task CanUpdateReportedProperties()
        {
            Try<ICloudProxy> cloudProxy = await this.GetCloudProxyWithConnectionStringKey("device2ConnStrKey");
            Assert.True(cloudProxy.Success);
            IMessage message = await cloudProxy.Value.GetTwinAsync();

            JObject twin = JObject.Parse(System.Text.Encoding.UTF8.GetString(message.Body));
            int version = (int)twin.SelectToken("reported.$version");
            int counter = (int?)twin.SelectToken("reported.bvtCounter") ?? 0;

            IMessage updateReportedPropertiesMessage = new Message(Encoding.UTF8.GetBytes($"{{\"bvtCounter\":{counter + 1}}}"));
            await cloudProxy.Value.UpdateReportedPropertiesAsync(updateReportedPropertiesMessage);

            message = await cloudProxy.Value.GetTwinAsync();
            twin = JObject.Parse(System.Text.Encoding.UTF8.GetString(message.Body));
            int nextVersion = (int)twin.SelectToken("reported.$version");
            var nextCounter = (int?)twin.SelectToken("reported.bvtCounter");
            Assert.NotNull(nextCounter);
            Assert.Equal(version + 1, nextVersion);
            Assert.Equal(counter + 1, nextCounter);

            bool disconnectResult = await cloudProxy.Value.CloseAsync();
            Assert.True(disconnectResult);
        }

        [Fact]
        public async Task CanListenForDesiredPropertyUpdates()
        {
            var update = new TaskCompletionSource<IMessage>();
            var cloudListener = new Mock<ICloudListener>();
            var deviceConnectionStringKey = "device2ConnStrKey";
            cloudListener.Setup(x => x.OnDesiredPropertyUpdates(It.IsAny<IMessage>()))
                .Callback((IMessage m) => update.TrySetResult(m))
                .Returns(TaskEx.Done);

            Try<ICloudProxy> cloudProxy = await this.GetCloudProxyWithConnectionStringKey(deviceConnectionStringKey);
            Assert.True(cloudProxy.Success);

            cloudProxy.Value.BindCloudListener(cloudListener.Object);
            await cloudProxy.Value.SetupDesiredPropertyUpdatesAsync();

            var desired = new TwinCollection()
            {
                ["desiredPropertyTest"] = Guid.NewGuid().ToString()
            };

            await UpdateDesiredProperty(ConnectionStringHelper.GetDeviceId(await SecretsHelper.GetSecretFromConfigKey(deviceConnectionStringKey)), desired);
            await update.Task;
            await cloudProxy.Value.RemoveDesiredPropertyUpdatesAsync();

            var expected = new Core.Test.Message(Encoding.UTF8.GetBytes(desired.ToJson()));
            expected.SystemProperties[SystemProperties.EnqueuedTime] = "";
            expected.SystemProperties[SystemProperties.Version] = desired.Version.ToString();
            IMessage actual = update.Task.Result;

            Assert.Equal(expected.Body, actual.Body);
            Assert.Equal(expected.Properties, actual.Properties);
            Assert.Equal(expected.SystemProperties.Keys, actual.SystemProperties.Keys);
        }

        async Task<Try<ICloudProxy>> GetCloudProxyWithConnectionStringKey(string connectionStringConfigKey)
        {
            const int ConnectionPoolSize = 10;
            string deviceConnectionString = await SecretsHelper.GetSecretFromConfigKey(connectionStringConfigKey);
            var converters = new MessageConverterProvider(new Dictionary<Type, IMessageConverter>()
            {
                { typeof(Client.Message), new MqttMessageConverter() },
                { typeof(Twin), new TwinMessageConverter() },
                { typeof(TwinCollection), new TwinCollectionMessageConverter() }
            });
            ICloudProxyProvider cloudProxyProvider = new CloudProxyProvider(converters, ConnectionPoolSize);
            var deviceIdentity = Mock.Of<IIdentity>(m => m.Id == ConnectionStringHelper.GetDeviceId(deviceConnectionString) && m.ConnectionString == deviceConnectionString);
            Try<ICloudProxy> cloudProxy = await cloudProxyProvider.Connect(deviceIdentity);
            return cloudProxy;
        }

        static async Task CheckMessageInEventHub(IList<IMessage> sentMessages, DateTime startTime)
        {
            string eventHubConnectionString = await SecretsHelper.GetSecretFromConfigKey("eventHubConnStrKey");
            var eventHubReceiver = new EventHubReceiver(eventHubConnectionString);
            IList<EventData> cloudMessages = await eventHubReceiver.GetMessagesFromAllPartitions(startTime);
            await eventHubReceiver.Close();
            Assert.NotNull(cloudMessages);
            Assert.NotEmpty(cloudMessages);
            Assert.True(MessageHelper.CompareMessagesAndEventData(sentMessages, cloudMessages));
        }

        static async Task UpdateDesiredProperty(string deviceId, TwinCollection desired)
        {
            string connectionString = await SecretsHelper.GetSecretFromConfigKey("iotHubConnStrKey");
            RegistryManager registryManager = RegistryManager.CreateFromConnectionString(connectionString);
            Twin twin = await registryManager.GetTwinAsync(deviceId);
            twin.Properties.Desired = desired;
            twin = await registryManager.UpdateTwinAsync(deviceId, twin, twin.ETag);
            desired["$version"] = twin.Properties.Desired.Version;
        }
    }
}
