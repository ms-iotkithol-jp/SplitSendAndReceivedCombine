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
        private static BlobContainerClient splittedClient;
        private static BlobContainerClient mergedClient;

        [FunctionName("StoreSplittedData")]
        public static async Task Run([IoTHubTrigger("messages/events", Connection = "iothubconnectionstring", ConsumerGroup = "split")] EventData message, ILogger log,
            ExecutionContext context)
        {
            if (splittedClient == null)
            {
                var config = new ConfigurationBuilder().SetBasePath(context.FunctionAppDirectory).AddJsonFile("local.settings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();
                var blobCS = config.GetConnectionString("outptblobstoag");
                splittedClient = new BlobContainerClient(blobCS, "splitreceive");
                mergedClient = new BlobContainerClient(blobCS, "mergedsplited");
            }
            log.LogInformation($"received message from ${message.SystemProperties["iothub-connection-device-id"]} at ${message.SystemProperties["iothub-enqueuedtime"]}");
            if (message.Properties.ContainsKey("msgtype"))
            {
                if (message.Properties["msgtype"].ToString() == "split")
                {
                    var deviceId = message.SystemProperties["iothub-connection-device-id"].ToString();
                    string dataid = message.Properties["dataid"].ToString();
                    string indexStr = message.Properties["index"].ToString();
                    string totalStr = message.Properties["total"].ToString();
                    string fn_common_part = $"{deviceId}/{dataid}";
                    int index = int.Parse(indexStr);
                    int total = int.Parse(totalStr);
                    if (index < total - 1)
                    {
                        using (var msgstream = new MemoryStream(message.Body.Array))
                        {
                            await splittedClient.UploadBlobAsync($"{fn_common_part}.{index}", msgstream);
                        }
                    }
                    else if (index == total - 1)
                    {
                        var blobs = new List<string>();
                        var results = splittedClient.GetBlobsAsync(prefix: fn_common_part);
                        int blobSize = 0;
                        byte[] contentBuffer = null;
                        await foreach (var blob in results)
                        {
                            blobs.Add(blob.Name);
                            blobSize = (int)blob.Properties.ContentLength.Value;
                        }
                        if (blobs.Count == total - 1)
                        {
                            for (int i = 0; i < total - 1; i++)
                            {
                                var buf = new byte[blobSize];
                                var blobName = $"{fn_common_part}.{i}";
                                var blob = splittedClient.GetBlobClient(blobName);
                                var blobd = await blob.DownloadAsync();
                                var bstream = blobd.Value.Content;
                                await bstream.ReadAsync(buf, 0, blobSize);
                                if (contentBuffer == null)
                                {
                                    contentBuffer = new byte[blobSize];
                                    buf.CopyTo(contentBuffer, 0);
                                }
                                else
                                {
                                    var tmpBuf = new byte[contentBuffer.Length + blobSize];
                                    contentBuffer.CopyTo(tmpBuf, 0);
                                    buf.CopyTo(tmpBuf, contentBuffer.Length);
                                    contentBuffer = tmpBuf;
                                }
                                await splittedClient.DeleteBlobAsync(blobName);
                            }
                            var destBuf = new byte[contentBuffer.Length + message.Body.Count];
                            contentBuffer.CopyTo(destBuf, 0);
                            message.Body.CopyTo(destBuf, contentBuffer.Length);
                            var mergedBlobName = $"{fn_common_part}.{message.Properties["ext"]}";
                            var memStream = new MemoryStream(destBuf);
                            await mergedClient.UploadBlobAsync(mergedBlobName, memStream);
                        }
                        else
                        {
                            log.LogInformation($"{dataid} from {deviceId} should be devided to {total} but {blobs.Count}");
                        }
                    }
                }
            }
        }
    }
}