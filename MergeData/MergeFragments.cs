using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace MergeData
{
    public static class MergeFragments
    {
        [FunctionName("MergeFragments")]
        public static async Task<string> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            ILogger log)
        {
            int counter = 0;
            var dataFragInfo = context.GetInput<DataFragInfo>();
            var entityId = new EntityId(nameof(MergeState), context.InstanceId);
            var mergeStateProxy = context.CreateEntityProxy<IMergeState>(entityId);
            mergeStateProxy.Allocate(dataFragInfo.UnitSize * dataFragInfo.TotalFragments);
            // In the case of SignalEntity, update actions are done by asynchronously so this logic can't gurantedd to complete all merge actions before blob upload.
            // context.SignalEntity(entityId,nameof(MergeState.Initialize));
            // context.SignalEntity(entityId, nameof(MergeState.Allocate), dataFragInfo.UnitSize * dataFragInfo.TotalFragments);

            for (int i = 0; i < dataFragInfo.TotalFragments; i++)
            {
                // Wait for fragment uploading via MergeFragments_NotifyFragment
                var result = await context.WaitForExternalEvent<(byte[] data, int index)>($"{i}");
                using (await context.LockAsync(entityId))
                {
                    (byte[] data, int offset) mergeArg = (result.data, dataFragInfo.UnitSize * result.index);
                    mergeStateProxy.WriteData(mergeArg);
                }

                // In the case of SignalEntity, update actions are done by asynchronously so this logic can't gurantedd to complete all merge actions before blob upload.
                // context.SignalEntity(entityId, nameof(MergeState.WriteData),mergeArg);
                // In the case of CallActivityAsync target MergeState instance can't be accessed in MergeFragments_Merge logic.
                // var mergeResult = await context.CallActivityAsync<bool>("MergeFragments_Merge", mergeArg);
                log.LogInformation($"Merged[{i}] - {counter++}");
                // This logic process result of CallActivityAsync case.
                // if (!mergeResult)
                // {
                //     log.LogInformation($"Merge Failed for Index:{i}");
                //     break;
                // }
            }

            var blobName = $"{dataFragInfo.DataName}{dataFragInfo.ExtName}";
            using (await context.LockAsync(entityId))
            {
                (EntityId entityId, string blobName) callArg = (entityId, blobName);
                var uploadResult = await context.CallActivityAsync<bool>("MergeFragments_Upload", callArg);
                if (!uploadResult)
                {
                    log.LogInformation($"Upload of {blobName} is failed");
                }
            }
            return blobName;
        }

        // This function isn't use because this logic can't access to MergeState instance which was created in MergeFragment orchestration function.
        [FunctionName("MergeFragments_Merge")]
        public static async Task<bool> Merge(
            [ActivityTrigger](EntityId entityId, byte[] data,int offset) arg,
            [DurableClient] IDurableEntityClient client,
            ILogger log)
        {
            try
            {
                var mergeState = await client.ReadEntityStateAsync<MergeState>(arg.entityId);
                (byte[] data, int offset) writeArg = (arg.data, arg.offset);
                mergeState.EntityState.WriteData(writeArg);
                log.LogInformation($"Write data {arg.data.Length} bytes at {arg.offset}");

                return true;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                return false;
            }
        }

        [FunctionName("MergeFragments_Upload")]
        public static async Task<bool> Upload(
            [ActivityTrigger](EntityId entityId,string blobName) arg,
            Binder binder,
            [DurableClient] IDurableEntityClient client,
            ILogger log)
        {
            try
            {
                var blobAttrbutes = new Attribute[]
                {
                    new BlobAttribute($"merged/{arg.blobName}",FileAccess.Write),
                    new StorageAccountAttribute("outputblobstorage")
                };
                using (var mergedClient = await binder.BindAsync<CloudBlobStream>(blobAttrbutes))
                {
                    var dfi = await client.ReadEntityStateAsync<MergeState>(arg.entityId);
                    await mergedClient.WriteAsync(dfi.EntityState.MergedData, 0, dfi.EntityState.TotalSize);
                    await mergedClient.FlushAsync();
                    log.LogInformation($"{arg.blobName} is uploaded");
                }
                return true;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                return false;
            }

        }

        [FunctionName("MergeFragments_NotifyFragment")]
        public static async Task<HttpResponseMessage> NotifyFragment(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient client)
        {
            try
            {
                var instanceId = req.RequestUri.ParseQueryString()["instanceid"];
                var Index = int.Parse(req.RequestUri.ParseQueryString()["index"]);
                var TotalFragments = int.Parse(req.RequestUri.ParseQueryString()["total"]);
                var UnitSize = int.Parse(req.RequestUri.ParseQueryString()["size"]);

                using (var stream = await req.Content.ReadAsStreamAsync())
                {
                    var data = new byte[UnitSize];
                    await stream.ReadAsync(data, 0, UnitSize);
                    (byte[] data, int index) arg = (data, Index);
                    await client.RaiseEventAsync(instanceId, $"{Index}", arg);
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
                var httpContent = new StringContent(ex.Message);
                response.Content = httpContent;
                return response;
            }
        }

        [FunctionName("MergeFragments_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log,
            ExecutionContext context)
        {
            var dataFragInfo = new DataFragInfo()
            {
                DataName = req.RequestUri.ParseQueryString()["dataname"],
                ExtName = req.RequestUri.ParseQueryString()["extname"],
                UnitSize = int.Parse(req.RequestUri.ParseQueryString()["unitsize"]),
                TotalFragments = int.Parse(req.RequestUri.ParseQueryString()["total"])
            };
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("MergeFragments", null, dataFragInfo);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName(nameof(MergeState))]
        public static Task Run([EntityTrigger] IDurableEntityContext ctx) => ctx.DispatchAsync<MergeState>();

        public class DataFragInfo
        {
            public int TotalFragments { get; set; }
            public string DataName { get; set; }
            public string ExtName { get; set; }
            public int UnitSize { get; set; }
        }

        public interface IMergeState
        {
            public void Allocate(int size);
            public void WriteData((byte[] data, int offset) arg);
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class MergeState : IMergeState
        {
            [JsonProperty("MergedData")]
            public byte[] MergedData { get; set; }
            [JsonProperty("TotalSize")]
            public int TotalSize { get; set; }

            public void Allocate(int size)
            {
                TotalSize = 0;
                MergedData = new byte[size];
            }
            
            public void WriteData((byte[] data, int offset) arg)
            {
                arg.data.CopyTo(MergedData, arg.offset);
                TotalSize += arg.data.Length;
            }
        }
    }
}