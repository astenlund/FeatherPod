// FeatherPod Infrastructure - Multi-Feed Architecture
// This template creates Storage Account, App Service, Function App (for async normalization), managed identities, and RBAC
// All resource names are parameterized - use parameters.json for Prod, parameters-test.json for Test

@description('Environment: Prod or Test (for documentation and tagging)')
@allowed([
  'Prod'
  'Test'
])
param environment string = 'Prod'

@description('Name of the storage account (must be globally unique, 3-24 chars, lowercase alphanumeric)')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Azure region for resources')
param location string = 'swedencentral'

@description('Storage account SKU')
@allowed([
  'Standard_LRS'
  'Standard_GRS'
  'Standard_ZRS'
  'Premium_LRS'
])
param storageSku string = 'Standard_LRS'

@description('Name of the blob container (single container for all data)')
param containerName string = 'featherpod'

@description('Name of the App Service Plan')
param appServicePlanName string

@description('Name of the App Service (web app)')
param appServiceName string

@description('App Service Plan SKU')
@allowed([
  'F1'
  'B1'
  'B2'
  'B3'
  'S1'
  'S2'
  'S3'
  'P1v2'
  'P2v2'
  'P3v2'
])
param appServicePlanSku string = 'F1'

@description('.NET runtime version')
param dotnetVersion string = 'DOTNETCORE|10.0'

@description('Name of the Function App for async normalization')
param functionAppName string

@description('Shared secret for internal API calls between Function App and App Service')
@secure()
param internalApiKey string

@description('Name of the Azure SignalR Service')
param signalRServiceName string

@description('Name of the Azure OpenAI account')
param openAiAccountName string

@description('Name of the Azure Speech Services account')
param speechAccountName string

@description('LLM model name for AI title suggestions')
param llmModelName string

@description('LLM model version')
param llmModelVersion string

// Resource tags
var tags = {
  Environment: environment
  Application: 'FeatherPod'
}

// Storage Account
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: storageSku
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// Blob Service
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// Single Container (hierarchical structure: feeds.json, {feedId}/episodes.json, {feedId}/{filename})
resource featherpodContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

// Queue Service
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// Normalization Jobs Queue
// Name must match FeatherPod.Shared/JobStorageNames.cs (QueueName).
resource normalizationQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = {
  parent: queueService
  name: 'normalization-jobs'
}

// Table Service
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// Normalization Jobs Status Table
// Name must match FeatherPod.Shared/JobStorageNames.cs (TableName).
resource normalizationTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  parent: tableService
  name: 'normalizationjobs'
}

// Log Analytics Workspace (required for App Insights)
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appServiceName}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appServiceName}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
    RetentionInDays: 30
  }
}

// App Service Plan
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
    tier: appServicePlanSku == 'F1' ? 'Free' : (appServicePlanSku == 'B1' || appServicePlanSku == 'B2' || appServicePlanSku == 'B3' ? 'Basic' : (appServicePlanSku == 'S1' || appServicePlanSku == 'S2' || appServicePlanSku == 'S3' ? 'Standard' : 'PremiumV2'))
  }
  kind: 'linux'
  properties: {
    reserved: true // Required for Linux
  }
}

// App Service (Web App)
resource appService 'Microsoft.Web/sites@2023-01-01' = {
  name: appServiceName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: dotnetVersion
      appCommandLine: 'dotnet FeatherPod.Server.dll'
      alwaysOn: appServicePlanSku != 'F1' // AlwaysOn not supported on F1
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
    }
  }
}

// App Settings
resource appServiceSettings 'Microsoft.Web/sites/config@2023-01-01' = {
  parent: appService
  name: 'appsettings'
  properties: {
    AllowedHosts: '${appServiceName}.azurewebsites.net'
    Azure__AccountName: storageAccountName
    Azure__ContainerName: containerName
    Podcast__BaseUrl: 'https://${appServiceName}.azurewebsites.net'
    Internal__Key: internalApiKey
    Azure__SignalR__ConnectionString: signalRService.listKeys().primaryConnectionString
    AzureOpenAI__Endpoint: 'https://${openAiAccountName}.openai.azure.com/'
    AzureOpenAI__Deployment: gpt4oMiniDeployment.name
    AzureSpeech__Endpoint: 'https://${speechAccountName}.cognitiveservices.azure.com/'
    AzureSpeech__MaxConcurrent: '3'
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  }
}

// Role definition GUIDs (well-known)
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var cognitiveServicesOpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var cognitiveServicesSpeechUserRoleId = 'f2dc8367-1007-4938-bd23-fe263f013447'

// Role Assignment: Grant App Service managed identity access to Storage Account (Blob)
resource appServiceBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Grant App Service managed identity access to Queue Storage
resource appServiceQueueRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Grant App Service managed identity access to Table Storage
resource appServiceTableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appService.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Function App (Flex Consumption Plan - Linux)
// Flex Consumption provides faster cold starts, configurable instance memory, and better scaling
resource functionAppPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${functionAppName}-plan'
  location: location
  tags: tags
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true // Required for Linux (Flex Consumption is Linux-only)
  }
}

@description('Maximum number of Function App instances for scale-out')
@minValue(1)
@maxValue(1000)
param functionAppScaleLimit int = 3

@description('Instance memory size in MB (512, 2048, or 4096)')
@allowed([
  512
  2048
  4096
])
param functionAppInstanceMemoryMB int = 4096

// Deployment container for Flex Consumption (replaces WEBSITE_CONTENTSHARE/WEBSITE_CONTENTAZUREFILECONNECTIONSTRING)
resource functionDeploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'function-deployments'
  properties: {
    publicAccess: 'None'
  }
}

// Function App
resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionAppPlan.id
    httpsOnly: true
    siteConfig: {
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
    }
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: functionAppScaleLimit
        instanceMemoryMB: functionAppInstanceMemoryMB
      }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}function-deployments'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
    }
  }
}

// Function App Settings
resource functionAppSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: {
    AzureWebJobsStorage__accountName: storageAccountName
    FUNCTIONS_EXTENSION_VERSION: '~4'
    StorageAccountName: storageAccountName
    ContainerName: containerName
    AppServiceUrl: 'https://${appServiceName}.azurewebsites.net'
    InternalKey: internalApiKey
    JobRetentionDays: '7'
    OrphanedBlobRetentionDays: '1'
    CleanupSchedule: '0 0 3 * * *'
    AzureOpenAIEndpoint: 'https://${openAiAccountName}.openai.azure.com/'
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  }
}

// Role Assignment: Grant Function App managed identity access to Blob Storage
resource functionAppBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Grant Function App managed identity access to Queue Storage
resource functionAppQueueRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Grant Function App managed identity access to Table Storage
resource functionAppTableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Azure SignalR Service (Free tier for progress push)
resource signalRService 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: signalRServiceName
  location: location
  tags: tags
  sku: {
    name: 'Free_F1'
    tier: 'Free'
    capacity: 1
  }
  kind: 'SignalR'
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
  }
}

// Azure OpenAI (for AI title suggestions)
resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiAccountName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    publicNetworkAccess: 'Enabled'
  }
}

resource gpt4oMiniDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAiAccount
  name: 'gpt-4o-mini'
  sku: {
    name: 'Standard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: llmModelName
      version: llmModelVersion
    }
  }
}

// Azure Speech Services (for conversation transcription with diarization)
resource speechAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechAccountName
  location: location
  tags: tags
  kind: 'SpeechServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: speechAccountName
    publicNetworkAccess: 'Enabled'
  }
}

// Role Assignment: Grant App Service managed identity access to Azure OpenAI
resource appServiceOpenAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, appService.id, cognitiveServicesOpenAiUserRoleId)
  scope: openAiAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Grant App Service managed identity access to Azure Speech Services
resource appServiceSpeechRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speechAccount.id, appService.id, cognitiveServicesSpeechUserRoleId)
  scope: speechAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesSpeechUserRoleId)
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output containerName string = featherpodContainer.name
output appServicePlanId string = appServicePlan.id
output appServicePlanName string = appServicePlan.name
output appServiceId string = appService.id
output appServiceName string = appService.name
output appServiceDefaultHostname string = appService.properties.defaultHostName
output appServicePrincipalId string = appService.identity.principalId
output functionAppId string = functionApp.id
output functionAppName string = functionApp.name
output functionAppDefaultHostname string = functionApp.properties.defaultHostName
output speechAccountName string = speechAccount.name
output functionAppPrincipalId string = functionApp.identity.principalId
output appInsightsName string = appInsights.name
output signalRServiceName string = signalRService.name
output openAiAccountName string = openAiAccount.name
