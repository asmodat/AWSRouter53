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

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWSRouter53
{
    public partial class Function
    {
        private static readonly int _maxRoutesPerInstance = 10;
        private EC2Helper _EC2;
        private Route53Helper _R53;
        private ILambdaContext _context;
        private ILambdaLogger _logger;
        private bool _verbose;

        private void Log(string msg)
        {
            if (msg.IsNullOrEmpty() || !_verbose)
                return;

            _logger.Log(msg);
        }

        public Function()
        {
            _EC2 = new EC2Helper();
            _R53 = new Route53Helper();
        }

        public async Task FunctionHandler(ILambdaContext context)
        {
            _logger.Log($"{context?.FunctionName} => {nameof(FunctionHandler)} => Started");
            _verbose = Environment.GetEnvironmentVariable("verbose").ToBoolOrDefault(true);
            var maxTime = 60 * 1000;
            var rateLimit = 20 * 1000;
            var sw = Stopwatch.StartNew();
            _context = context;
            _logger = context.Logger;

            long remaining = 0;
            do
            {
                _logger.Log($"New Execution Started, currently elpased: {sw.ElapsedMilliseconds}");
                var elapsed = await Execute();
                var rateAwait = rateLimit - elapsed;

                remaining = maxTime - (sw.ElapsedMilliseconds + Math.Max(rateLimit, 2 * elapsed) + 1000);

                if (rateAwait > 0 && remaining > 0)
                {
                    Log($"Rate Limit Awaiting {rateAwait} [ms] remaining out of {rateLimit} [ms]. Total elapsed: {sw.ElapsedMilliseconds} [ms]");
                    await Task.Delay((int)rateAwait);
                }

                await Task.Delay(1000);
            } while (remaining > 0);
        }

        public async Task<long> Execute()
        {
            var sw = Stopwatch.StartNew();
            Log($"{_context?.FunctionName} => {nameof(FunctionHandler)} => Execute");

            try
            {
                //Select Instances with Route Tag Key Only
                var instances = (await _EC2.ListInstances()).Where(instance => instance.Tags.Any(x => x.Key.Contains("Route53 Name"))).ToArray();

                var running = instances.Where(instance => instance.State.Code == 16);//running
                var not_running = instances.Where(instance => instance.State.Code != 16);//not running

                var blacklist = new List<string>();
                //only process running instances if there are stopped ones with the same 'Route53 Name'
                if (!running.IsNullOrEmpty() && !not_running.IsNullOrEmpty())
                {
                    foreach (var live in running)
                    {
                        var names_live = live.Tags.Where(x => x.Key.Contains("Route53 Name") && !x.Value.IsNullOrEmpty()).Select(x => x.Value);
                        var zones_live = live.Tags.Where(x => x.Key.Contains("Route53 Zone") && !x.Value.IsNullOrEmpty()).Select(x => x.Value);

                        foreach (var stopped in not_running)
                        {
                            if (blacklist.Contains(stopped.InstanceId) || live.InstanceId == stopped.InstanceId) 
                                continue; //dont process already blacklisted instances or the same instances

                            var names_stopped = stopped.Tags.Where(x => x.Key.Contains("Route53 Name") && !x.Value.IsNullOrEmpty()).Select(x => x.Value);
                            var zones_stopped = stopped.Tags.Where(x => x.Key.Contains("Route53 Zone") && !x.Value.IsNullOrEmpty()).Select(x => x.Value);

                            if (names_live.IntersectAny(names_stopped) && zones_live.IntersectAny(zones_stopped))
                            {
                                Console.WriteLine($"Blacklisting instance '{stopped.InstanceId}' from processing, found overlapping zones and names with already running instance '{live.InstanceId}'.");
                                blacklist.Add(stopped.InstanceId);
                            }
                        }
                    }
                }

                //Select Non blacklisted instances
                instances = instances.Where(instance => !blacklist.Contains(instance.InstanceId)).ToArray();

                var zones = await _R53.GetRecordSets();

                if (zones.Count <= 0)
                    Log($"AWSRouter53 can't process any tags, not a single Route53 Zone was found.");
                else
                    Log($"Processing validating routes for {zones.Count} zones and {instances.Length} instances...");

                await ParallelEx.ForEachAsync(instances, async instance => await Process(zones, instance));
                return sw.ElapsedMilliseconds;
            }
            finally
            {
                Log($"{_context?.FunctionName} => {nameof(FunctionHandler)} => Stopped, Eveluated within: {sw.ElapsedMilliseconds} [ms]");
            }
        }
    }
}
