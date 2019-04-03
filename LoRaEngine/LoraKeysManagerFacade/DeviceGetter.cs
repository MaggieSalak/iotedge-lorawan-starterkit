﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoraKeysManagerFacade
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using LoRaWan.Shared;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using StackExchange.Redis;

    public class DeviceGetter
    {
        private readonly RegistryManager registryManager;
        private readonly ILoRaDeviceCacheStore cacheStore;

        public DeviceGetter(RegistryManager registryManager, ILoRaDeviceCacheStore cacheStore)
        {
            this.registryManager = registryManager;
            this.cacheStore = cacheStore;
        }

        /// <summary>
        /// Entry point function for getting devices
        /// </summary>
        [FunctionName(nameof(GetDevice))]
        public async Task<IActionResult> GetDevice(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req,
            ILogger log)
        {
            try
            {
                VersionValidator.Validate(req);
            }
            catch (IncompatibleVersionException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }

            // ABP parameters
            string devAddr = req.Query["DevAddr"];
            // OTAA parameters
            string devEUI = req.Query["DevEUI"];
            string devNonce = req.Query["DevNonce"];
            string gatewayId = req.Query["GatewayId"];

            if (devEUI != null)
            {
                EUIValidator.ValidateDevEUI(devEUI);
            }

            try
            {
                var results = await this.GetDeviceList(devEUI, gatewayId, devNonce, devAddr, log);
                string json = JsonConvert.SerializeObject(results);
                return new OkObjectResult(json);
            }
            catch (DeviceNonceUsedException)
            {
                return new BadRequestObjectResult("UsedDevNonce");
            }
            catch (JoinRefusedException ex)
            {
                log.LogDebug("Join refused: {msg}", ex.Message);
                return new BadRequestObjectResult("JoinRefused: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            finally
            {
                _ = this.StartDeltaReload(gatewayId);
                _ = this.StartFullReloadIfNeeded(gatewayId);
            }
        }

        public async Task<List<IoTHubDeviceInfo>> GetDeviceList(string devEUI, string gatewayId, string devNonce, string devAddr, ILogger log = null)
        {
            var results = new List<IoTHubDeviceInfo>();

            if (devEUI != null)
            {
                // OTAA join
                using (var deviceCache = new LoRaDeviceCache(this.cacheStore, devEUI, gatewayId))
                {
                    var joinInfo = await this.GetJoinInfoAsync(devEUI, gatewayId, log);

                    var cacheKeyDevNonce = string.Concat(devEUI, ":", devNonce);
                    var lockKeyDevNonce = string.Concat(cacheKeyDevNonce, ":joinlockdevnonce");

                    if (await this.cacheStore.LockTakeAsync(lockKeyDevNonce, gatewayId, TimeSpan.FromSeconds(10)))
                    {
                        try
                        {
                            var freshValue = this.cacheStore.StringSet(cacheKeyDevNonce, devNonce, TimeSpan.FromMinutes(5), onlyIfNotExists: true);
                            if (!freshValue)
                            {
                                log?.LogDebug("dev nonce already used. Ignore request '{key}':{gwid}", devEUI, gatewayId);
                                throw new DeviceNonceUsedException();
                            }

                            var iotHubDeviceInfo = new IoTHubDeviceInfo
                            {
                                DevEUI = devEUI,
                                PrimaryKey = joinInfo.PrimaryKey
                            };

                            results.Add(iotHubDeviceInfo);

                            if (await deviceCache.TryToLockAsync())
                            {
                                this.cacheStore.KeyDelete(devEUI);
                                log?.LogDebug("Removed key '{key}':{gwid}", devEUI, gatewayId);
                            }
                            else
                            {
                                log?.LogWarning("Failed to acquire lock for '{key}'", devEUI);
                            }
                        }
                        finally
                        {
                            this.cacheStore.LockRelease(lockKeyDevNonce, gatewayId);
                        }
                    }
                    else
                    {
                        throw new DeviceNonceUsedException();
                    }
                }
            }
            else if (devAddr != null)
            {
                // ABP or normal message

                // TODO check for sql injection
                devAddr = devAddr.Replace('\'', ' ');
                using (var devAddrCache = new LoRaDevAddrCache(this.cacheStore, devAddr, gatewayId))
                {
                    if (devAddrCache.TryGetInfo(out List<DevAddrCacheInfo> devAddressesInfo))
                    {
                        for (int i = 0; i < devAddressesInfo.Count; i++)
                        {
                            // device was not yet populated
                            if (!string.IsNullOrEmpty(devAddressesInfo[i].PrimaryKey))
                            {
                                results.Add(devAddressesInfo[i]);
                            }
                            else
                            {
                                // we need to load the primaryKey from IoTHub
                                // Add a lock loadPrimaryKey get lock get
                                    devAddressesInfo[i].PrimaryKey = await this.LoadPrimaryKeyAsync(devAddressesInfo[i].DevEUI);
                                    results.Add(devAddressesInfo[i]);
                                    devAddrCache.StoreInfo(devAddressesInfo[i]);
                            }
                        }

                        // if the cache results are null, we query the IoT Hub
                        if (results.Count == 0)
                        {
                            var query = this.registryManager.CreateQuery($"SELECT * FROM devices WHERE properties.desired.DevAddr = '{devAddr}' OR properties.reported.DevAddr ='{devAddr}'", 100);
                            while (query.HasMoreResults)
                            {
                                var page = await query.GetNextAsTwinAsync();

                                foreach (var twin in page)
                                {
                                    if (twin.DeviceId != null)
                                    {
                                        var device = await this.registryManager.GetDeviceAsync(twin.DeviceId);
                                        var iotHubDeviceInfo = new IoTHubDeviceInfo
                                        {
                                            DevAddr = devAddr,
                                            DevEUI = twin.DeviceId,
                                            PrimaryKey = device.Authentication.SymmetricKey.PrimaryKey
                                        };
                                        results.Add(iotHubDeviceInfo);
                                        devAddrCache.StoreInfo((DevAddrCacheInfo)iotHubDeviceInfo);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                throw new Exception("Missing devEUI or devAddr");
            }

            return results;
        }

        private async Task StartFullReloadIfNeeded(string gatewayId)
        {
            if ((DateTime.UtcNow - LoRaDevAddrCache.LastFullReload) > TimeSpan.FromHours(LoRaDevAddrCache.CacheFullReloadAfterHours))
            {
                    List<DevAddrCacheInfo> devAddrCacheInfos = new List<DevAddrCacheInfo>();
                    // load all the LoRa devices
                    var queryDeviceList = this.registryManager.CreateQuery($"SELECT * FROM devices WHERE is_defined(properties.desired.AppKey) OR is_defined(properties.desired.AppSKey) OR is_defined(properties.desired.NwkSKey)");
                    while (queryDeviceList.HasMoreResults)
                    {
                        var page = await queryDeviceList.GetNextAsTwinAsync();
                        foreach (var twin in page)
                        {
                            // can device id be null?
                            if (twin.DeviceId != null)
                            {
                                devAddrCacheInfos.Add(new DevAddrCacheInfo()
                                {
                                    DevAddr = twin.Properties.Desired["DevAddr"] ?? twin.Properties.Reported["DevAddr"],
                                    DevEUI = twin.DeviceId,
                                    GatewayId = twin.Properties.Desired["GatewayId"]
                                });
                            }
                        }
                    }

                    LoRaDevAddrCache.RebuildCache(devAddrCacheInfos, this.cacheStore, gatewayId);
            }
        }

        /// <summary>
        /// Delta reload update the cache with recently updated elements from IoT Hub
        /// </summary>
        private async Task StartDeltaReload(string gatewayId)
        {
            if ((DateTime.UtcNow - LoRaDevAddrCache.LastDeltaReload) > TimeSpan.FromMinutes(LoRaDevAddrCache.CacheUpdateAfterMinutes))
            {
                var query = this.registryManager.CreateQuery($"SELECT * FROM c where properties.desired.$metadata.$lastUpdated >= '{LoRaDevAddrCache.LastDeltaReload.ToString()}'");
                while (query.HasMoreResults)
                {
                    var page = await query.GetNextAsTwinAsync();

                    foreach (var twin in page)
                    {
                        if (twin.DeviceId != null)
                        {
                            var device = await this.registryManager.GetDeviceAsync(twin.DeviceId);
                            var loRaDevAddr = new DevAddrCacheInfo()
                            {
                                DevAddr = twin.Properties.Desired["DevAddr"] ?? twin.Properties.Reported["DevAddr"],
                                DevEUI = twin.DeviceId,
                                GatewayId = twin.Properties.Desired["GatewayId"]
                            };

                            // Not sure about performance of this, might want to switch
                            using (var devAddrCache = new LoRaDevAddrCache(this.cacheStore, loRaDevAddr.DevAddr, loRaDevAddr.GatewayId))
                            {
                                if (devAddrCache.TryGetInfo(out List<DevAddrCacheInfo> listInfo))
                                {
                                    bool updated = false;
                                    for (int i = 0; i < listInfo.Count; i++)
                                    {
                                        // Need to review logic here.
                                        if (loRaDevAddr.DevAddr == listInfo[i].DevAddr)
                                        {
                                            if (loRaDevAddr.DevEUI == listInfo[i].DevEUI)
                                            {
                                                if (loRaDevAddr.GatewayId != listInfo[i].GatewayId)
                                                {
                                                    loRaDevAddr.GatewayId = listInfo[i].GatewayId;
                                                    devAddrCache.StoreInfo(loRaDevAddr);
                                                    updated = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    if (!updated)
                                    {
                                        devAddrCache.StoreInfo(loRaDevAddr);
                                    }
                                }
                                else
                                {
                                    // list does not contain we add the element
                                    devAddrCache.StoreInfo(loRaDevAddr);
                                }
                            }
                        }
                    }
                }

                this.cacheStore.StringSet(string.Concat("lastdeltareload:", gatewayId), DateTime.UtcNow.ToString(), null);
            }
        }

        private async Task<string> LoadPrimaryKeyAsync(string devEUI)
        {
            var device = await this.registryManager.GetDeviceAsync(devEUI);
            if (device == null)
            {
                return null;
            }

            return device.Authentication.SymmetricKey?.PrimaryKey;
        }

        private async Task<JoinInfo> GetJoinInfoAsync(string devEUI, string gatewayId, ILogger log)
        {
            var cacheKeyJoinInfo = string.Concat(devEUI, ":joininfo");
            var lockKeyJoinInfo = string.Concat(devEUI, ":joinlockjoininfo");
            JoinInfo joinInfo = null;

            if (await this.cacheStore.LockTakeAsync(lockKeyJoinInfo, gatewayId, TimeSpan.FromMinutes(5)))
            {
                try
                {
                    joinInfo = this.cacheStore.GetObject<JoinInfo>(cacheKeyJoinInfo);
                    if (joinInfo == null)
                    {
                        joinInfo = new JoinInfo();

                        var device = await this.registryManager.GetDeviceAsync(devEUI);
                        if (device != null)
                        {
                            joinInfo.PrimaryKey = device.Authentication.SymmetricKey.PrimaryKey;
                            var twin = await this.registryManager.GetTwinAsync(devEUI);
                            const string GatewayIdProperty = "GatewayID";
                            if (twin.Properties.Desired.Contains(GatewayIdProperty))
                            {
                                joinInfo.DesiredGateway = twin.Properties.Desired[GatewayIdProperty].Value as string;
                            }
                        }

                        this.cacheStore.ObjectSet(cacheKeyJoinInfo, joinInfo, TimeSpan.FromMinutes(60));
                        log?.LogDebug("updated cache with join info '{key}':{gwid}", devEUI, gatewayId);
                    }

                    if (string.IsNullOrEmpty(joinInfo.PrimaryKey))
                    {
                        throw new JoinRefusedException("Not in our network.");
                    }

                    if (!string.IsNullOrEmpty(joinInfo.DesiredGateway) && gatewayId != joinInfo.DesiredGateway)
                    {
                        throw new JoinRefusedException("Not the owning gateway");
                    }

                    log?.LogDebug("got LogInfo '{key}':{gwid} attached gw: {desiredgw}", devEUI, gatewayId, joinInfo.DesiredGateway);
                }
                finally
                {
                    var released = this.cacheStore.LockRelease(lockKeyJoinInfo, gatewayId);
                }
            }
            else
            {
                throw new JoinRefusedException("Failed to acquire lock for joininfo");
            }

            return joinInfo;
        }
    }
}
