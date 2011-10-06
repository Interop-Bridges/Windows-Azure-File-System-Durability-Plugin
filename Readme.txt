// Copyright © Microsoft Corporation.  All Rights Reserved.
// This code released under the terms of the 
// Apache License, Version 2.0 (http://opensource.org/licenses/Apache-2.0)

FileSystemDurabilityPlugin:
===========================
Windows Azure platform provides infinite scalability if applications are designed using best practices recommended for scalability. 
Most important requirement is to design stateless application. If application does not store any state on the host it is running on, 
then it becomes easy to replicate this application on multiple instances and serving more requests. 

Instance count in the Windows Azure application’s serviceconfigration.cscfg file can be used to provision multiple instances.

It is easy to write stateless application from scratch, but if we have an existing application that stores state on 
local resources, then this may not be trivial and may need design changes. Most (or almost all) of the PHP 
applications that are available uses local file system to store data and state. Applications like Joomla, WordPress 
provides functionality to upload media files and they are stored on local file system. This will not work in server 
farm when there is a load balancer in front on these servers.

We developed this FileSystemDurabilityPlugin to solve the above issue with typical stateful PHP applications.

Architecture
============
Architecture is similar to http://waacceleratorumbraco.codeplex.com/ 

Master copy of all writable, modifiable files is kept in Windows Azure blob Storage. Each VM instance (1..N) runs 
this plugin exe in the background and it synchronizes files with master blob storage periodically. User can configure 
this duration using a setting in serviceconfiguration.cscfg file. The same configuration file also contain settings 
for blob storage account credential and blob container to be used.

Instead of writing our own synchronization logic, we rely on Microsoft Sync Framework’s file synchronization provider.

Sync Framework’s file synchronization provider is designed to correctly handle local concurrent operations by 
other applications on files that may be part of an ongoing synchronization operation. If a local concurrent change 
has happened on a file after the last change detection pass on the replica, either on the source or the destination, 
to prevent loss of the concurrent change, any changes to that file will not be synchronized until the next 
synchronization session (or the next change detection pass, if the application is using explicit change detection).

If the user provisions a new instance, the FileSystemDurabilityPlugin exe makes sure to copy files from master 
blob storage to local file system. So before the role starts, applications gets all required files on local host.

Original Sample
===============
This plugin is entirely based on http://code.msdn.microsoft.com/Synchronizing-Files-to-a14ecf57 sample written by 
Microsoft Sync Framework team. This is just a Windows Azure plugin wrapper on top of it.

Requirement
===========
You need to install Windows Azure SDK 1.5.

How to Install this plugin?
===========================
Option-1) Build from source
---------------------------
- Install following Microsoft Sync Framework 2.1 runtimes needed for the plugin. These msi
  1) ProviderServices-v2.1-x64-ENU.msi
  2) Synchronization-v2.1-x64-ENU.msi

- Build the Visual Studio Solution. It will produce binaries in FileSystemDurabilityPlugin\bin\debug folder as shown below,

<SolutionDir>\FileSystemDurabilityPlugin\bin\debug
│   FileSystemDurabilityPlugin.csplugin
│   FileSystemDurabilityPlugin.exe
│   FileSystemDurabilityPlugin.exe.config
│   FileSystemDurabilityPlugin.pdb
│   FileSystemDurabilityPlugin.vshost.exe
│   FileSystemDurabilityPlugin.vshost.exe.config
│   FileSystemDurabilityPlugin.vshost.exe.manifest
│   InstallSyncFx.cmd
│   Microsoft.WindowsAzure.Diagnostics.dll
│   Microsoft.WindowsAzure.Diagnostics.xml
│   Microsoft.WindowsAzure.StorageClient.dll
│   Microsoft.WindowsAzure.StorageClient.xml
│   msshrtmi.dll
│
└───syncfx
        ProviderServices-v2.1-x64-ENU.msi
        Synchronization-v2.1-x64-ENU.msi

- Create a folder FileSystemDurabilityPlugin in Windows Azure SDK plugin 
  folder "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins"

- Copy content of <SolutionDir>\FileSystemDurabilityPlugin\bin\debug folder to the folder
  "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins\FileSystemDurabilityPlugin". You may 
  need administrative privileges to update "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins" folder.

Option-2) Download prebuilt plugin for Windows Azure SDK 1.5
------------------------------------------------------------
- Download the prebuilt binary for the plugin from 
  https://github.com/downloads/Interop-Bridges/Windows-Azure-File-System-Durability-Plugin/FileSystemDurabilityPlugin-v1.1.zip

- Extract FileSystemDurabilityPlugin.zip into "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins" folder. This 
  will create a folder "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins\FileSystemDurabilityPlugin"
  You may need administrative privileges to update "C:\Program Files\Windows Azure SDK\v1.5\bin\plugins" folder.

How to use this plugin?
=======================
This plugin can be imported into Windows Azure Service project similar to other default Windows Azure plugins 
like Diagnostics or RemoteAcess. In the ServiceDefinition.csdef file of the Windows Azure Service project, you need 
to import plugin as follows:

<Imports>
    <Import moduleName="FileSystemDurabilityPlugin" />
</Imports>

Users need to set following settings in ServiceConfiguration.cscfg file.
<!-- Settings needed for FileSystemDurabilityPlugin -->
<Setting name="FileSystemDurabilityPlugin.StorageAccountName" value="*****" /> <!-- Windows Azure Storage account name -->
<Setting name="FileSystemDurabilityPlugin.StorageAccountPrimaryKey" value="*****" /> <!-- Windows Azure Storage account key -->
<Setting name="FileSystemDurabilityPlugin.SyncContainerName" value="*****" /> <!-- Valid container name -->
<Setting name="FileSystemDurabilityPlugin.LocalFolderToSync" value="*****" /> <!-- Relative path to approot -->
<Setting name="FileSystemDurabilityPlugin.FileNameIncludesToSync" value="" /> <!-- Optional: To sync specific files only -->
<Setting name="FileSystemDurabilityPlugin.ExcludePathsFromSync" value="" />   <!-- Optional. To exclude some folders from sync -->
<Setting name="FileSystemDurabilityPlugin.ExcludeSubDirectories" value="false" /> <!-- To exclude sub directories, set this value as true -->
<Setting name="FileSystemDurabilityPlugin.SyncFrequencyInSeconds" value="7200" /> <!-- Keep this value large to minimize Windows Azure Storage Transaction cost. Use 0 value to pause the sync. -->

Once settings are defined in ServiceConfiguration.cscfg, you can package and deploy the application to Windows Azure. Please make sure to use 
correct settings otherwise your role instances may crash and keep on recycling.

How to modify sync frequency after the deployment?
==================================================
To modify the sync frequency after the deployment, one needs to modify the FileSystemDurabilityPlugin.SyncFrequencyInSeconds for the deployment 
on the Windows Azure portal.

How to modify the pause the file synchronization?
=================================================
To pause the file synchronization process, one needs to set the FileSystemDurabilityPlugin.SyncFrequencyInSeconds value to 0.

============
*** NOTE ***
============
As this plugin periodically check for changes in master blob storage container for several blobs, it makes lots of 
Windows Azure Storage Transactions. Therefore you should NOT set low value for SyncFrequencyInSeconds duration. 
Typically this value should be in hours. Please monitor daily usage for your Windows Azure Storage Transactions 
and increase this duration if you think the cost is not justified for your business.