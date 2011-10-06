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
    class WindowsAzureBlob2FileSystemSync
    {
        // Main thread handle
        static Thread _mainThread = null;

        static void Main(string[] args)
        {
            string accountName = null;
            string accountKey = null;
            string containerName = null;
            string localPathName = null;
            string excludePaths = null;
            string fileNameIncludesToSync = null;
            string excludeSubDirectories = null;
            string syncFrequencyInSeconds = null;            
            CloudStorageAccount storageAccount = null;
            FileSyncScopeFilter filter = null;

            try
            {
                try
                {
                    if (RoleEnvironment.IsAvailable)
                    {
                        // Store main thread handle
                        WindowsAzureBlob2FileSystemSync._mainThread = Thread.CurrentThread;
                        
                        // Read configuration settings
                        accountName = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.StorageAccountName");
                        accountKey = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.StorageAccountPrimaryKey");
                        containerName = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncContainerName");

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
                        localPathName = Path.Combine(appRootDir, RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.LocalFolderToSync"));

                        fileNameIncludesToSync = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.FileNameIncludesToSync");
                        excludePaths = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.ExcludePathsFromSync");
                        excludeSubDirectories = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.ExcludeSubDirectories");
                        syncFrequencyInSeconds = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncFrequencyInSeconds");
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

                if (!Directory.Exists(localPathName))
                {
                    Trace.TraceError("Please ensure that the local target directory exists.");
                    Environment.Exit(-1);
                }

                //
                // Setup Store
                //
                if (accountName.Equals("devstoreaccount1"))
                {
                    storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
                }
                else
                {
                    storageAccount = new CloudStorageAccount(new StorageCredentialsAccountAndKey(accountName, accountKey), true);
                }

                //
                // Create container if needed
                //
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
                blobClient.GetContainerReference(containerName).CreateIfNotExist();

                // Whether to include specific files only 
                if (!string.IsNullOrEmpty(fileNameIncludesToSync))
                {
                    if (filter == null)
                    {
                        filter = new FileSyncScopeFilter();
                    }
                    string[] fileNameIncludesToSyncInfo = fileNameIncludesToSync.Split(',');
                    foreach (string fileIncludes in fileNameIncludesToSyncInfo)
                    {
                        filter.FileNameIncludes.Add(fileIncludes);
                    }
                }

                // Set exclude path filter                
                if (!string.IsNullOrEmpty(excludePaths))
                {
                    if (filter == null)
                    {
                        filter = new FileSyncScopeFilter();
                    }
                    string[] excludePathInfo = excludePaths.Split(',');
                    foreach (string excludePath in excludePathInfo)
                    {
                        filter.SubdirectoryExcludes.Add(excludePath);
                    }
                }

                // Whether to exclude directoraries
                if (excludeSubDirectories.Equals("true"))
                {
                    if (filter == null)
                    {
                        filter = new FileSyncScopeFilter();
                    }
                    filter.AttributeExcludeMask = FileAttributes.Directory;
                }
                
                if (syncFrequencyInSeconds.Equals("-1"))
                {
                    SynchronizeOnce(filter, localPathName, containerName, storageAccount);
                }                
                else
                {
                    // Need to synchronize periodically

                    // Register event handler for roleinstance stopping  and changed events
                    RoleEnvironment.Stopping += WindowsAzureBlob2FileSystemSync.RoleEnvironmentStopping;
                    RoleEnvironment.Changed += WindowsAzureBlob2FileSystemSync.RoleEnvironmentChanged;

                    int frequencyInSecond = int.Parse(syncFrequencyInSeconds, System.Globalization.NumberStyles.Integer);
                    if (frequencyInSecond > 0)
                    {
                        // Start Synchronization periodically
                        while (true)
                        {
                            try
                            {
                                SynchronizeOnce(filter, localPathName, containerName, storageAccount);
                            }
                            catch (Exception ex)
                            {
                                Trace.TraceError("Failed to Synchronize. Error: {0}", ex.Message);
                            }

                            // Check new value for SyncFrequencyInSeconds, it can be modified
                            syncFrequencyInSeconds = RoleEnvironment.GetConfigurationSettingValue("FileSystemDurabilityPlugin.SyncFrequencyInSeconds");
                            int currentFrequencyInSecond = int.Parse(syncFrequencyInSeconds, System.Globalization.NumberStyles.Integer);
                            if (frequencyInSecond != currentFrequencyInSecond)
                            {
                                Trace.TraceInformation("Changing sync frequency to {0} seconds.", currentFrequencyInSecond);
                                frequencyInSecond = currentFrequencyInSecond;
                            }

                            try
                            {
                                if (frequencyInSecond > 0)
                                {
                                    Thread.Sleep(TimeSpan.FromSeconds(frequencyInSecond));
                                }
                                else
                                {
                                    // Pause the thread
                                    Thread.Sleep(Timeout.Infinite);
                                }
                            }
                            catch (ThreadInterruptedException)
                            {
                                Trace.TraceInformation("File Synchronization thread interrupted. Configuration settings might have changed.");                                
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.Message);
            }
        }

        // Event handler for roleinstance stopping event
        private static void RoleEnvironmentStopping(object sender, RoleEnvironmentStoppingEventArgs e)
        {
            Trace.TraceError("Roleinstance stopping, hence terminating file synchronization.");
            Environment.Exit(-1);
        }

        // Event handler for roleenvironment changed event
        private static void RoleEnvironmentChanged(object sender, RoleEnvironmentChangedEventArgs e)
        {
            if (WindowsAzureBlob2FileSystemSync._mainThread != null)
            {
                Trace.TraceInformation("Rolenvironment changed. Interrupting Synchronization thread that might be sleeping");
                WindowsAzureBlob2FileSystemSync._mainThread.Interrupt();
            }
        }

        // Main sync happens here
        private static void SynchronizeOnce(
            FileSyncScopeFilter filter, 
            string localPathName, 
            string containerName, 
            CloudStorageAccount storageAccount)
        {
            // Setup Provider
            AzureBlobStore blobStore = new AzureBlobStore(containerName, storageAccount);

            AzureBlobSyncProvider azureProvider = new AzureBlobSyncProvider(containerName, blobStore);
            azureProvider.ApplyingChange += new EventHandler<ApplyingBlobEventArgs>(UploadingFile);

            FileSyncProvider fileSyncProvider = null;
            if (filter == null)
            {
                try
                {
                    fileSyncProvider = new FileSyncProvider(localPathName);
                }
                catch (ArgumentException)
                {
                    fileSyncProvider = new FileSyncProvider(Guid.NewGuid(), localPathName);
                }
            }
            else
            {
                try
                {
                    fileSyncProvider = new FileSyncProvider(localPathName, filter, FileSyncOptions.None);
                }
                catch (ArgumentException)
                {
                    fileSyncProvider = new FileSyncProvider(Guid.NewGuid(), localPathName, filter, FileSyncOptions.None);
                }
            }

            fileSyncProvider.ApplyingChange += new EventHandler<ApplyingChangeEventArgs>(WindowsAzureBlob2FileSystemSync.DownloadingFile);

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