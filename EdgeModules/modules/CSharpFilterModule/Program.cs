namespace CSharpFilterModule
{
    using System;
    using System.Collections.Generic;   // For KeyValuePair<>
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;   // For TwinCollection
    using Newtonsoft.Json;                  // For JsonConvert

    class Program
    {
        static int counter;
        static int temperatureThreshold { get; set; } = 103;

        class MessageBody
        {
            public Machine machine { get; set; }
            public Ambient ambient { get; set; }
            public string timeCreated { get; set; }
        }

        class Machine
        {
            public double temperature { get; set; }
            public double pressure { get; set; }
        }

        class Ambient 
        {
            public double temperature { get; set; }
            public int humidity { get; set; }
        }

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Read the TemperatureThreshold value from the module twin's desired properties
            var moduleTwin = await ioTHubModuleClient.GetTwinAsync();
            var moduleTwinCollection = moduleTwin.Properties.Desired;
            try
            {
                temperatureThreshold = moduleTwinCollection["TemperatureThreshold"];
            }
            catch (ArgumentOutOfRangeException e)
            {
                Console.WriteLine($"Property TemperatureThreshold not exist: {e.Message}");
            }

            // Attach a callback for updates to the module twin's desired properties
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdate, null);

            // Register a callback for messages that are received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", FilterMessages, ioTHubModuleClient);
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub to inform 
        /// us that the desired properties have been changed. If a property 'TemperatureThreshold'
        ///  is found, we take its value and assign it to our temperatureThreshold variable.
        /// </summary>
        static Task OnDesiredPropertiesUpdate(TwinCollection desiredProperties, object userContext)
        {
            try
            {
                Console.WriteLine("Desired property change: ");
                Console.WriteLine(JsonConvert.SerializeObject(desiredProperties));

                if (desiredProperties["TemperatureThreshold"] != null)
                {
                    temperatureThreshold = desiredProperties["TemperatureThreshold"];
                }
            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Error when receiving desired property: {exception}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error when receiving desired property: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It filters temperatures below the temperature threshold to limit the amount of messages
        /// that are send to the IoT Hub. It also adds an Alert MessageType property.
        /// </summary>
        static async Task<MessageResponse> FilterMessages(Message message, object userContext)
        {
            var counterValue = Interlocked.Increment(ref counter);
            var result = MessageResponse.Completed;

            try
            {
                var moduleClient = userContext as ModuleClient;
                if (moduleClient == null)
                {
                    throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
                }
                var messageBytes = message.GetBytes();
                var messageString = Encoding.UTF8.GetString(messageBytes);
                Console.WriteLine($"Received message {counterValue}: [{messageString}]");

                // Get the message body
                var messageBody = JsonConvert.DeserializeObject<MessageBody>(messageString);

                if (messageBody != null && messageBody.machine.temperature > temperatureThreshold)
                {
                    Console.WriteLine($"Machine temperature {messageBody.machine.temperature} exceeds threshold {temperatureThreshold}");
                    var filteredMessage = new Message(messageBytes);
                    foreach (KeyValuePair<string, string> prop in message.Properties )
                    {
                        filteredMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    filteredMessage.Properties.Add("MessageType", "Alert");
                    await moduleClient.SendEventAsync("output1", filteredMessage);
                }
                else
                {
                    Console.WriteLine($"Sending message {counterValue} on to the next Module!");
                    var filteredMessage = new Message(messageBytes);
                    foreach (KeyValuePair<string, string> prop in message.Properties )
                    {
                        filteredMessage.Properties.Add(prop.Key, prop.Value);
                    }
                    await moduleClient.SendEventAsync("output2", filteredMessage);
                }

                // Indicate that the message treatment is completed
                return MessageResponse.Completed;
            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Error in sample: {exception}");
                }

                result = MessageResponse.Abandoned;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error in sample: {ex.Message}");

                result = MessageResponse.Abandoned;
            }
            return result;
        }
    }
}
