using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AsmodatStandard.Extensions;
using AsmodatStandard.Extensions.Collections;
using AsmodatStandard.Extensions.Threading;
using Amazon.Lambda.Core;
using AWSWrapper.EC2;
using AWSWrapper.Route53;
using Amazon.Route53.Model;
using Amazon.Route53;
using Amazon.EC2;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWSRouter53
{
    public class Function
    {
        private static readonly int _maxRoutesPerInstance = 10;
        private EC2Helper _EC2;
        private Route53Helper _R53;
        public Function()
        {
            _EC2 = new EC2Helper();
            _R53 = new Route53Helper();
        }

        public async Task FunctionHandler(ILambdaContext context)
        {
            var sw = Stopwatch.StartNew();
            context.Logger.Log($"{context?.FunctionName} => {nameof(FunctionHandler)} => Started");

            try
            {
                var t1 = _EC2.ListInstances();
                var t2 = _R53.GetRecordSets();

                await Task.WhenAll(t1, t2);

                var instances = await t1;
                var zones = await t2;

                if (zones.Count <= 0)
                    context.Logger.Log($"AWSRouter53 can't process any tags, not a single Route53 Zone was found.");
                else
                    context.Logger.Log($"Processing validating routes for {zones.Count} zones and {instances.Length} instances...");

                await ParallelEx.ForEachAsync(instances, async instance => await Process(zones, instance, context.Logger));
            }
            finally
            {
                context.Logger.Log($"{context?.FunctionName} => {nameof(FunctionHandler)} => Stopped, Eveluated within: {sw.ElapsedMilliseconds} [ms]");
            }
        }

        public async Task Process(Dictionary<HostedZone, ResourceRecordSet[]> zones, Amazon.EC2.Model.Instance instance, ILambdaLogger logger)
        {
            if (!instance.Tags.Any(x => x.Key.Contains("Route53")))
                return;

            logger.Log($"Processing and Veryfying Route53 Recors for EC2 Instance {instance.InstanceId}, Name: {instance.GetTagValueOrDefault("Name")}");

            var disableAll = instance.GetTagValueOrDefault("Route53 Disable All").ToBoolOrDefault(false);

            await ParallelEx.ForAsync(0, _maxRoutesPerInstance, async i =>
            {
                var suffix = i == 0 ? "" : $" {i + 1}";
                var name = instance.GetTagValueOrDefault($"Route53 Name{suffix}");
                var type = instance.GetTagValueOrDefault($"Route53 Address{suffix}").CoalesceNullOrEmpty("public").ToLower();
                string address = null;

                if (type == "public")
                    address = instance.PublicIpAddress;
                else if (type == "private")
                    address = instance.PrivateIpAddress;
                else
                {
                    logger.Log($"Invalid Tag: 'Route53 Address{suffix}' expected 'public' or 'private' but was '{type}', Tags Origin: EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
                    return;
                }

                var enabled =
                    !disableAll && //not globally disabled
                    !address.IsNullOrEmpty() && //has valid public or private IP 
                    instance.State.Name == InstanceStateName.Running && //instance is running
                    instance.GetTagValueOrDefault($"Route53 Enable{suffix}").ToBoolOrDefault(false); //is locally enabled

                var zoneId = instance.GetTagValueOrDefault($"Route53 Zone{suffix}");
                var ttl = instance.GetTagValueOrDefault($"Route53 TTL{suffix}").ToIntOrDefault(60);

                if (!name.IsNullOrEmpty() && zones.Any(z => z.Key.Id.Contains(zoneId)))
                {
                    var zone = zones.First(z => z.Key.Id.Contains(zoneId));
                    var recordName = $"{name.Trim(' ', '.')}.{zone.Key.Name.Trim(' ', '.')}";

                    //for whatever reason those names end with dot once they are saved...
                    var record = zone.Value.FirstOrDefault(r => r.Name.Equals($"{recordName}.", StringComparison.InvariantCultureIgnoreCase));

                    await ProcessRecord(zoneId, record, instance, enabled, recordName, ttl, address, logger);
                }
            });

            logger.Log($"Finished Processing and Veryfying Route53 Recors for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
        }

        public async Task ProcessRecord(string zoneId, ResourceRecordSet record, Amazon.EC2.Model.Instance instance,
            bool enabled,
            string name,
            long ttl,
            string address,
            ILambdaLogger logger)
        {
            try
            {
                if (!enabled)
                {
                    if (record == null)
                    {
                        logger.Log($"No need to destroy, record '{name}' does not exists. Tags Origin: EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
                        return; //no need to update, record does not exist
                    }
                    else
                    {
                        logger.Log($"Destroying record {record.Name} linked to EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})...");
                        await _R53.DestroyRecord(zoneId, record.Name, record.Type);
                    }
                }
                else
                {
                    if (
                        record?.Type == RRType.A &&
                        record?.ResourceRecords.Any(r => r.Value == address) == true &&
                        record?.TTL == ttl)
                    {
                        logger.Log($"Correct record already exists for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
                        return; //no need to update, correct record already exists
                    }

                    logger.Log($"Updating record of EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")}), Old Record Name: '{record?.Name}'...");
                    await _R53.UpsertRecord(zoneId: zoneId, Name: name, Value: address,
                        Type: RRType.A, TTL: ttl);
                }
            }
            catch (Exception ex)
            {
                logger.Log($"Failed during Update or Validation of Route53 record for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")}), ZoneId: {zoneId}, Name: {name}, Old Record Name: {record?.Name}, Enable: {enabled}, TTL: {ttl}, Address: {address}, Error: {ex.JsonSerializeAsPrettyException()}");
            }
        }
    }
}
