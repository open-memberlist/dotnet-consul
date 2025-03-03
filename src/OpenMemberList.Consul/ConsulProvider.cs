﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenMemberList;

namespace OpenMemberlist.Consul
{
    //TLDR;
    //this class has a very simple responsibility, poll consul for status updates.
    //then transform these statuses to MemberStatus messages and pass on to the 
    //cluster MemberList instance
    //
    //Helper functionality: register, deregister and refresh TTL

    [PublicAPI]
    public class ConsulProvider : IMemberListProvider
    {
        private readonly TimeSpan _blockingWaitTime;
        private readonly ConsulClient _client;

        private readonly TimeSpan
            _deregisterCritical; //this is how long the service exists in consul before disappearing when unhealthy, min 1 min

        private readonly TimeSpan _refreshTtl; //this is the refresh rate of TTL, should be smaller than the above

        private readonly TimeSpan _serviceTtl; //this is how long the service is healthy without a ttl refresh
        
        private string _consulServiceInstanceId; //the specific instance id of this node in consul

        private string _consulServiceName; //name of the custer, in consul this means the name of the service

        //   private string _consulSessionId;
        private volatile bool _deregistered;
        private string _host;

        private string[] _labels;
        private ILogger _logger;

        private int _port;
        private bool _shutdown;
        private IMemberListClient _cluster;

        public ConsulProvider(ConsulProviderConfig config) : this(config, clientConfiguration => { })
        {
        }

        public ConsulProvider(ConsulProviderConfig config, Action<ConsulClientConfiguration> clientConfiguration)
        {
            _serviceTtl = config!.ServiceTtl;
            _refreshTtl = config!.RefreshTtl;
            _deregisterCritical = config!.DeregisterCritical;
            _blockingWaitTime = config!.BlockingWaitTime;
            _client = new ConsulClient(clientConfiguration);
        }

        public ConsulProvider(IOptions<ConsulProviderConfig> options) : this(options.Value, clientConfiguration => { })
        {
        }

        public ConsulProvider(
            IOptions<ConsulProviderConfig> options,
            Action<ConsulClientConfiguration> clientConfiguration
        ) :
            this(options.Value, clientConfiguration)
        {
        }

        public async Task StartMemberAsync(IMemberListClient cluster)
        {
            _cluster = cluster;
            var (host, port) = cluster.GetAddress();
            _labels = cluster.GetLabels();

            await RegisterMemberAsync();
            StartUpdateTtlLoop();
            StartMonitorMemberStatusChangesLoop();
        }

        public Task StartClientAsync(IMemberListClient cluster)
        {
            _cluster = cluster;
            StartMonitorMemberStatusChangesLoop();

            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(bool graceful)
        {
            _logger.LogInformation("Shutting down consul provider");
            //flag for shutdown. used in thread loops
            _shutdown = true;

            if (graceful)
            {
                await DeregisterServiceAsync();
                _deregistered = true;
            }

            _logger.LogInformation("Shut down consul provider");
        }
        
        private void StartMonitorMemberStatusChangesLoop()
        {
            _ = Task.Run(async () => {
                    var waitIndex = 0ul;

                    while (!_shutdown && !_cluster.Shutdown.IsCancellationRequested)
                    {
                        try
                        {
                            var statuses = await _client.Health.Service(_consulServiceName, null, false, new QueryOptions
                                {
                                    WaitIndex = waitIndex,
                                    WaitTime = _blockingWaitTime
                                }
                                , _cluster.Shutdown
                            );
                            if (_deregistered) break;

                            _logger.LogDebug("Got status updates from Consul");

                            waitIndex = statuses.LastIndex;

                            var currentMembers =
                                statuses
                                    .Response
                                    .Where(v => IsAlive(v.Checks)) //only include members that are alive
                                    .Select(ToMember)
                                    .ToArray();
                            
                            _cluster.UpdateMembers(currentMembers);
                        }
                        catch (Exception x)
                        {
                            if (!_cluster.Shutdown.IsCancellationRequested)
                            {
                                _logger.LogError(x, "Consul Monitor failed");

                                //just backoff and try again
                                await Task.Delay(2000);
                            }
                        }
                    }
                }
            );

            static Member ToMember(ServiceEntry v)
            {
                var member = new Member
                {
                    Id = v.Service.Meta["id"],
                    Host = v.Service.Address,
                    Port = v.Service.Port
                };

                member.Labels.AddRange(v.Service.Tags);

                return member;
            }
        }

        private void StartUpdateTtlLoop() => _ = Task.Run(async () => {
                while (!_shutdown)
                {
                    try
                    {
                        await _client.Agent.PassTTL("service:" + _consulServiceInstanceId, "");
                        await Task.Delay(_refreshTtl, _cluster.Shutdown);
                    }
                    catch (Exception x)
                    {
                        if (!_cluster.Shutdown.IsCancellationRequested) _logger.LogError(x, "Consul TTL Loop failed");
                    }
                }

                _logger.LogInformation("Consul Exiting TTL loop");
            }
        );

        //Register this cluster in consul.
        private async Task RegisterMemberAsync()
        {
            var s = new AgentServiceRegistration
            {
                ID = _consulServiceInstanceId,
                Name = _consulServiceName,
                Tags = _labels.ToArray(),
                Address = _host,
                Port = _port,
                Check = new AgentServiceCheck
                {
                    DeregisterCriticalServiceAfter = _deregisterCritical,
                    TTL = _serviceTtl
                },
                Meta = new Dictionary<string, string>
                {
                    //register a unique ID for the current process
                    //if a node with host X and port Y, joins, then leaves, then joins again.
                    //we need a way to distinguish the new node from the old node.
                    //this is what this ID is for
                    {"id", _cluster.Id}
                }
            };
            await _client.Agent.ServiceRegister(s);
        }

        //unregister this cluster from consul
        private async Task DeregisterServiceAsync()
        {
            await _client.Agent.ServiceDeregister(_consulServiceInstanceId);
            _logger.LogInformation("Deregistered service");
        }

        private static bool IsAlive(HealthCheck[] serviceChecks) =>
            serviceChecks.All(c => c.Status.Equals(HealthStatus.Passing));
    }
}