using System.Linq;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using AWSWrapper.EC2;
using AWSWrapper.Route53;
using Amazon.Route53.Model;
using Amazon.Route53;

namespace AWSRouter53
{
    public partial class Function
    {
        public async Task ProcessRecord(string zoneId, ResourceRecordSet record, Amazon.EC2.Model.Instance instance,
            bool enabled,
            string name,
            long ttl,
            string address)
        {
            if (!enabled)
            {
                if (record == null)
                {
                    Log($"No need to destroy, record '{name}' does not exists for zone '{zoneId}'. Tags Origin: EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
                    return; //no need to update, record does not exist
                }
                else
                {
                    Log($"Destroying record {record.Name} linked to EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})...");
                    await _R53.DestroyRecord(zoneId, record.Name, record.Type);
                }

                return;
            }

            if (record?.Type == RRType.A &&
                record?.ResourceRecords.Any(r => r.Value == address) == true &&
                record?.TTL == ttl)
            {
                Log($"Correct record already exists for EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")})");
                return; //no need to update, correct record already exists
            }

            Log($"Updating record of EC2 Instance {instance.InstanceId} ({instance.GetTagValueOrDefault("Name")}), Old Record Name: '{record?.Name}'...");
            await _R53.UpsertRecordAsync(
                zoneId: zoneId,
                Name: name,
                Value: address,
                Type: RRType.A,
                TTL: ttl);
        }
    }
}
