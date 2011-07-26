// Copyright © Microsoft Corporation.  All Rights Reserved.
// This code released under the terms of the 
// Apache License, Version 2.0 (http://opensource.org/licenses/Apache-2.0)

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.IO;
using System.Text;
using Microsoft.Synchronization;
using Microsoft.Synchronization.Files;

using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.WindowsAzure.ServiceRuntime;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FileSystemDurabilityPlugin
{
    class AzureBlobSync
    {
        static string _accountName;
        static string _accountKey;
        static string _containerName;
        static string _localPathName;
        static string _excludePaths;
        static string _syncFrequencyInSeconds;
        static CloudStorageAccount _storageAccount;

        static void Main(string[] args)
        {
            try
            {                
                try
                {
                    if (RoleEnvironment.IsAvailable)
                    {
                        // Read configuration settings
                        _accountName = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.StorageAccountName");
                        _accountKey = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.StorageAccountPrimaryKey");
                        _containerName = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncContainerName");

                        // FileSystemDurabilityPlugin.LocalFolderToSync must be relative to web site root or approot

                        // Check if sitesroot\0 exists. We only synchronize the firt web site
                        string appRootDir = Environment.GetEnvironmentVariable("RoleRoot") + @"\sitesroot\0";
                        if (!Directory.Exists(appRootDir))
                        {
                            // May be WorkerRole
                            appRootDir = Environment.GetEnvironmentVariable("RoleRoot") + @"\approot";
                        }

                        try
                        {
                            // Make appRootDir writable. 
                            DirectorySecurity sec = Directory.GetAccessControl(appRootDir);                           
                            SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                            sec.AddAccessRule(new FileSystemAccessRule(everyone,
                                FileSystemRights.Modify | FileSystemRights.Synchronize,
                                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, 
                                PropagationFlags.None, 
                                AccessControlType.Allow));
                            Directory.SetAccessControl(appRootDir, sec);                            
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceError("Failed to make directory {0} writable. Error: {1}", appRootDir, ex.Message);
                            Environment.Exit(-1);
                        }

                        // Set sync folder on local VM
                        _localPathName = Path.Combine(appRootDir, RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.LocalFolderToSync"));
                        
                        _excludePaths = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.ExcludePathsFromSync");
                        _syncFrequencyInSeconds = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncFrequencyInSeconds");
                    }
                    else
                    {
                        // Outside role envionment, read command line argument
                        Trace.TraceError("Outside role envionment. Synchronization not possible.");
                        Environment.Exit(-1);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Failed to read configuration settings. Error: {0}", ex.Message);
                    Environment.Exit(-1);
                }

                if (!Directory.Exists(_localPathName))
                {
                    Trace.TraceError("Please ensure that the local target directory exists.");
                    Environment.Exit(-1);
                }

                //
                // Setup Store
                //
                if (_accountName.Equals("devstoreaccount1"))
                {
                    _storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
                }
                else
                {
                    _storageAccount = new CloudStorageAccount(new StorageCredentialsAccountAndKey(_accountName, _accountKey), true);
                }

                //
                // Create container if needed
                //
                CloudBlobClient blobClient = _storageAccount.CreateCloudBlobClient();
                blobClient.GetContainerReference(_containerName).CreateIfNotExist();

                // Set exclude filter
                FileSyncScopeFilter filter = null;
                if (!string.IsNullOrEmpty(_excludePaths))
                {
                    filter = new FileSyncScopeFilter();
                    string[] excludePathInfo = _excludePaths.Split(',');
                    foreach (string excludePath in excludePathInfo)
                    {
                        filter.SubdirectoryExcludes.Add(excludePath);
                    }
                }

                if (_syncFrequencyInSeconds.Equals("-1"))
                {
                    SynchronizeOnce(filter);
                }                
                else
                {
                    int frequencyInSecond = int.Parse(_syncFrequencyInSeconds, System.Globalization.NumberStyles.Integer);
                    if (frequencyInSecond > 0)
                    {
                        // Start Synchronization periodically
                        while (true)
                        {
                            try
                            {
                                SynchronizeOnce(filter);
                            }
                            catch (Exception ex)
                            {
                                Trace.TraceError("Failed to Synchronize. Error: {0}", ex.Message);
                            }

                            // Check new value for SyncFrequencyInSeconds, it can be modified
                            _syncFrequencyInSeconds = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncFrequencyInSeconds");
                            frequencyInSecond = int.Parse(_syncFrequencyInSeconds, System.Globalization.NumberStyles.Integer);

                            Thread.Sleep(TimeSpan.FromSeconds(frequencyInSecond));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.Message);
            }
        }

        // Main sync happens here
        private static void SynchronizeOnce(FileSyncScopeFilter filter)
        {
            // Setup Provider
            AzureBlobStore blobStore = new AzureBlobStore(_containerName, _storageAccount);

            AzureBlobSyncProvider azureProvider = new AzureBlobSyncProvider(_containerName, blobStore);
            azureProvider.ApplyingChange += new EventHandler<ApplyingBlobEventArgs>(UploadingFile);

            FileSyncProvider fileSyncProvider = null;
            if (filter == null)
            {
                try
                {
                    fileSyncProvider = new FileSyncProvider(_localPathName);
                }
                catch (ArgumentException)
                {
                    fileSyncProvider = new FileSyncProvider(Guid.NewGuid(), _localPathName);
                }
            }
            else
            {
                try
                {
                    fileSyncProvider = new FileSyncProvider(_localPathName, filter, FileSyncOptions.None);
                }
                catch (ArgumentException)
                {
                    fileSyncProvider = new FileSyncProvider(Guid.NewGuid(), _localPathName, filter, FileSyncOptions.None);
                }
            }

            fileSyncProvider.ApplyingChange += new EventHandler<ApplyingChangeEventArgs>(AzureBlobSync.DownloadingFile);

            try
            {
                SyncOrchestrator orchestrator = new SyncOrchestrator();
                orchestrator.LocalProvider = fileSyncProvider;
                orchestrator.RemoteProvider = azureProvider;
                orchestrator.Direction = SyncDirectionOrder.DownloadAndUpload;

                orchestrator.Synchronize();
            }
            catch (Exception ex)
            {
                Trace.TraceError("Failed to Synchronize. Error: {0}", ex.Message);
            }
            finally
            {
                fileSyncProvider.Dispose();
            }
        }
        
        public static void DownloadingFile(object sender, ApplyingChangeEventArgs args)
        {
        }

        public static void UploadingFile(object sender, ApplyingBlobEventArgs args)
        {
        }
    }
}