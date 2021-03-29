using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventHubs;
using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.IO;
using Azure.Storage.Blobs.Models;
using System.Collections.Generic;

namespace StoreSplittedData
{
    public static class StoreSplittedData
    {
        private static Dictionary<string, string> mergeOrchectrators = new Dictionary<string, string>();
        static HttpClient httpClient;
        static string webhookStarterUrl;
        static string webhookNotifyUrl;

        [FunctionName("StoreSplittedData")]
        public static async Task Run([IoTHubTrigger("messages/events", Connection = "iothubconnectionstring", ConsumerGroup = "split")] EventData message, ILogger log,
            ExecutionContext context)
        {
            log.LogInformation($"received message from ${message.SystemProperties["iothub-connection-device-id"]} at ${message.SystemProperties["iothub-enqueuedtime"]}");
            if (message.Properties.ContainsKey("msgtype"))
            {
                if (message.Properties["msgtype"].ToString() == "split")
                {
                    if (httpClient == null)
                    {
                        httpClient = new HttpClient();
                        var config = new ConfigurationBuilder().SetBasePath(context.FunctionAppDirectory).AddJsonFile("local.settings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();
                        webhookStarterUrl  = config.GetConnectionString("webhook_starter");
                        webhookNotifyUrl = config.GetConnectionString("webhook_notify");
                    }
                    var deviceId = message.SystemProperties["iothub-connection-device-id"].ToString();
                    string dataid = message.Properties["dataid"].ToString();
                    string indexStr = message.Properties["index"].ToString();
                    string totalStr = message.Properties["total"].ToString();
                    string fn_common_part = $"{deviceId}/{dataid}";

                    string orchId = null;
                    if (mergeOrchectrators.ContainsKey(fn_common_part))
                    {
                        orchId = mergeOrchectrators[fn_common_part];
                    }
                    if (string.IsNullOrEmpty(orchId))
                    {
                        var starterParameter = new Dictionary<string, string>()
                        {
                            {"dataname",fn_common_part },
                            {"index", indexStr },
                            {"total", totalStr }
                        };
                        if (message.Properties.ContainsKey("ext"))
                        {
                            starterParameter.Add("extname", message.Properties["ext"].ToString());
                        }
                        var starterUrl = $"{webhookStarterUrl}?{await new FormUrlEncodedContent(starterParameter).ReadAsStringAsync()}";
                        if (message.Properties.ContainsKey("ext"))
                        {
                            starterUrl += $"&extname={message.Properties["ext"]}";
                        }
                        var request = new HttpRequestMessage(HttpMethod.Get, starterUrl);
                        var response = await httpClient.SendAsync(request);
                        if (response.StatusCode== System.Net.HttpStatusCode.OK)
                        {
                            var starterResponse = await response.Content.ReadAsStringAsync();
                            dynamic starterResponseJson = Newtonsoft.Json.JsonConvert.DeserializeObject(starterResponse);
                            orchId = starterResponseJson["id"];
                            mergeOrchectrators.Add(fn_common_part, orchId);
                            log.LogInformation($"Starting merge job ${fn_common_part}<=>${orchId}");
                        }
                    }
                    if (!string.IsNullOrEmpty(orchId))
                    {
                        var notifyParemeter = new Dictionary<string, string>()
                        {
                            { "instanceid", orchId },
                            { "index", indexStr },
                            { "total", totalStr },
                            { "size", $"{message.Body.Count}" }
                        };
                        var notifyUrl = $"{webhookNotifyUrl}?{await new FormUrlEncodedContent(notifyParemeter).ReadAsStringAsync()}";
                        var content = new ByteArrayContent(message.Body.Array);
                        var response = await httpClient.PostAsync(notifyUrl, content);
                        if (response.StatusCode== System.Net.HttpStatusCode.OK)
                        {
                            log.LogInformation($"Notifyed instaneid={orchId}");
                        }
                        else
                        {
                            log.LogInformation($"Notification response = ${response.StatusCode}");
                        }
                    }
                    else
                    {
                        // error
                    }

                    if (message.Properties.ContainsKey("ext"))
                    {
                        fn_common_part += $".{message.Properties["ext"]}";
                    }
                    int index = int.Parse(indexStr);
                    int total = int.Parse(totalStr);
                    // should i invoke function when index == 0? or send data by not blob but octed stream?
                    // using (var msgstream = new MemoryStream(message.Body.Array))
                    // {
                    //    var blobName = $"{fn_common_part}.{totalStr}.{index}.{index}";
                    //    await splittedClient.UploadBlobAsync(blobName, msgstream);
                    //    log.LogInformation($"{blobName} is uploaded");
                    //}
                }
            }
        }
    }
}