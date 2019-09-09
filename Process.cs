using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AsmodatStandard.Extensions;
using AsmodatStandard.Extensions.Collections;
using AsmodatStandard.Extensions.Threading;
using Amazon.Lambda.Core;
using AWSWrapper.EC2;
using Amazon.Route53.Model;
using Amazon.EC2;

namespace AWSRouter53
{
    public partial class Function
    {
        public async Task Process(Dictionary<HostedZone, ResourceRecordSet[]> zones, Amazon.EC2.Model.Instance instance)
        {
            Log($"Processing and Veryfying Route53 Recors for EC2 Instance {instance.InstanceId}, Name: {instance.GetTagValueOrDefault("Name")}");

            var disableAll = instance.GetTagValueOrDefault("Route53 Disable All").ToBoolOrDefault(false);

            await ParallelEx.ForAsync(0, _maxRoutesPerInstance, async i =>
            {
                var suffix = i == 0 ? "" : $" {i - 1}";
                var name = instance.GetTagValueOrDefault($"Route53 Name{suffix}");
                var type = instance.GetTagValueOrDefault($"Route53 Address{suffix}").CoalesceNullOrEmpty("public").ToLower();
                string address = null;

                if (type == "public")
                    address = instance.PublicIpAddress;
                else if (type == "private")
                    address = instance.PrivateIpAddress;
                else
                {
                    Log($"Invalid Tag: 'Route53 Address{suffix}' expected 'public' or 'private' but was '{type}', Tags Origin: EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
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

                    var ex = await ProcessRecord(zoneId, record, instance, enabled, recordName, ttl, address).CatchExceptionAsync();
                    if (ex != null)
                        Log($"Failed during Update or Validation of Route53 record for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")}), ZoneId: {zoneId}, Name: {name}, Old Record Name: {record?.Name}, Enable: {enabled}, TTL: {ttl}, Address: {address}, Error: {ex.JsonSerializeAsPrettyException()}");
                }
            });

            Log($"Finished Processing and Veryfying Route53 Recors for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
        }
    }
}
