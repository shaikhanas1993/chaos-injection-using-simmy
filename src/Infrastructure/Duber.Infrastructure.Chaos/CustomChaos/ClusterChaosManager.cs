﻿using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Duber.Infrastructure.Chaos.CustomChaos
{
    internal static class ClusterChaosManager
    {
        private static HttpClient _httpClient = new HttpClient();
        private static GeneralChaosSetting _chaosSetting;

        internal static async Task RestartNodes(GeneralChaosSetting chaosSetting, int percentage)
        {
            _chaosSetting = chaosSetting;
            await GetAccessToken(_chaosSetting.TenantId, _chaosSetting.ClientId, _chaosSetting.ClientKey);
            var unhealthyNodes = await GetNodesToRestartStop(percentage);
            await RestartOrStopVM(Operation.Restart, unhealthyNodes);
        }

        internal static async Task StopNodes(GeneralChaosSetting chaosSetting, int percentage)
        {
            _chaosSetting = chaosSetting;
            await GetAccessToken(_chaosSetting.TenantId, _chaosSetting.ClientId, _chaosSetting.ClientKey);
            var unhealthyNodes = await GetNodesToRestartStop(percentage);
            await RestartOrStopVM(Operation.PowerOff, unhealthyNodes);
        }

        private static async Task RestartOrStopVM(Operation operation, IEnumerable<Value> nodes)
        {
            var taskList = new List<Task>();
            foreach (var node in nodes)
            {
                var URL = $"https://management.azure.com/subscriptions/{_chaosSetting.SubscriptionId}/resourceGroups/{_chaosSetting.ResourceGroupName}/providers/Microsoft.Compute/virtualMachineScaleSets/{_chaosSetting.VMScaleSetName}/virtualmachines/{node.instanceId}/{operation}?api-version=2018-06-01";
                taskList.Add(_httpClient.PostAsync(URL, null));
            }

            // TODO: handle aggregte and specific exception.
            await Task.WhenAll(taskList);            
        }

        private static async Task<VirtualMachineScaleSetVM> GetClusterNodes()
        {
            var URL = $"https://management.azure.com/subscriptions/{_chaosSetting.SubscriptionId}/resourceGroups/{_chaosSetting.ResourceGroupName}/providers/Microsoft.Compute/virtualMachineScaleSets/{_chaosSetting.VMScaleSetName}/virtualMachines?$select=instanceView&$expand=instanceView&api-version=2018-06-01";
            var response = await _httpClient.GetAsync(URL);
            return JsonConvert.DeserializeObject<VirtualMachineScaleSetVM>(await response.Content.ReadAsStringAsync());
        }

        private static async Task<IEnumerable<Value>> GetNodesToRestartStop(int percentage)
        {
            var nodes = await GetClusterNodes();
            percentage = Math.Min(percentage, 60); // ensures that at least the cluster will remain with the 40% of nodes. You can make it configurable.
            var numberOfNodesToRestartStop = Math.Ceiling(percentage * nodes.value.Count() / 100m);
            var unhealthyStatuses = new[] { "PowerState/deallocated", "PowerState/stopped", "ProvisioningState/updating" };

            // since this guy may be executed many times I  make sure I don't restart/stop more than the allowed nodes. We can also consider make this guy thread safe.
            var unhealthyNodes = nodes.value.Where(node => node.properties.instanceView.statuses.Any(status => unhealthyStatuses.Contains(status.code)));
            if (unhealthyNodes.Count() >= numberOfNodesToRestartStop)
                return new List<Value>();

            return nodes.value
                .Where(node => node.properties.instanceView.statuses.Any(status => !unhealthyStatuses.Contains(status.code)))
                .Take((int)numberOfNodesToRestartStop);
        }

        private static async Task<string> GetAccessToken(string tenantId, string clientId, string clientKey)
        {
            string authContextURL = $"https://login.windows.net/{tenantId}";
            var authenticationContext = new AuthenticationContext(authContextURL);
            var credential = new ClientCredential(clientId, clientKey);
            var result = await authenticationContext.AcquireTokenAsync("https://management.azure.com/", credential);

            if (result == null)
            {
                throw new InvalidOperationException("Failed to obtain the JWT token");
            }

            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.AccessToken}");
            return result.AccessToken;
        }

        private enum Operation
        {
            Restart = 0,
            PowerOff = 1
        }
    }
}
