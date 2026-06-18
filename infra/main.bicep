targetScope = 'resourceGroup'

@description('Name used for resource naming.')
param name string = 'ai-callcenter'

@description('Azure region. HorizonDB preview is available only in selected regions.')
@allowed([
  'centralus'
  'westus2'
  'westus3'
  'swedencentral'
  'australiaeast'
])
@metadata({
  azd: {
    type: 'location'
  }
})
param location string

@description('Environment name supplied by azd.')
param environmentName string = name

@description('HorizonDB PostgreSQL major version.')
param postgresVersion string = '17'

@description('HorizonDB vCores.')
param postgresVCores int = 4

@description('HorizonDB replica count.')
param postgresReplicaCount int = 1

@secure()
@description('Auto-generated HorizonDB administrator password.')
param postgresPassword string = 'Hdb1!${replace(newGuid(), '-', '')}'

@description('Azure OpenAI chat/reranker model name.')
param chatModelName string = 'gpt-4o-mini'

@description('Azure OpenAI chat/reranker deployment name.')
param chatDeploymentName string = 'app-reranker'

@description('Azure OpenAI chat/reranker model version.')
param chatModelVersion string = '2024-07-18'

@description('Azure OpenAI chat/reranker deployment SKU.')
param chatDeploymentSku string = 'GlobalStandard'

@description('Azure OpenAI embedding model name.')
param embeddingModelName string = 'text-embedding-3-large'

@description('Azure OpenAI embedding deployment name.')
param embeddingDeploymentName string = 'app-embedding'

@description('Azure OpenAI embedding model version.')
param embeddingModelVersion string = '1'

@description('Azure OpenAI embedding deployment SKU.')
param embeddingDeploymentSku string = 'GlobalStandard'

@description('Azure OpenAI API version registered in HorizonDB model registry.')
param openAiApiVersion string = '2024-10-21'

@description('Deployment suffix used for HorizonDB parameter group names because preview create is not idempotent.')
param deploymentSuffix string = utcNow('yyyyMMddHHmmss')

@description('Use an existing HorizonDB cluster with the generated name. Set true after a successful first provision to avoid preview immutable-property update errors.')
param useExistingHorizonDb bool = false

var resourceToken = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName, location))
var shortToken = take(resourceToken, 8)
var resourcePrefix = take(toLower(replace('${name}-${shortToken}', '_', '-')), 36)
var compactPrefix = take(replace(resourcePrefix, '-', ''), 20)
var tags = {
  'azd-env-name': environmentName
}

var apiServiceName = 'api'
var postgresAdmin = 'horizonAdmin'
var postgresDatabase = 'callcenter'
var postgresPort = '5432'
var horizonDbClusterName = take('${resourcePrefix}-hdb', 55)
var horizonDbParamGroupName = take('${horizonDbClusterName}-p-${deploymentSuffix}', 63)
var logAnalyticsName = take('${resourcePrefix}-log', 63)
var registryName = take('${compactPrefix}acr', 50)
var containerEnvironmentName = take('${resourcePrefix}-cae', 60)
var containerAppName = take('${resourcePrefix}-api', 32)
var keyVaultName = take('${compactPrefix}kv${shortToken}', 24)
var openAiAccountName = take('${resourcePrefix}-openai', 64)
var speechAccountName = take('${resourcePrefix}-speech', 64)
var apiIdentityName = take('${resourcePrefix}-api-id', 128)

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: apiIdentityName
  location: location
  tags: tags
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiIdentity.id, 'acrpull')
  scope: registry
  properties: {
    principalType: 'ServicePrincipal'
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource apiOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, apiIdentity.id, 'openai-user')
  scope: openAi
  properties: {
    principalType: 'ServicePrincipal'
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

resource apiSpeechUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, apiIdentity.id, 'speech-user')
  scope: speech
  properties: {
    principalType: 'ServicePrincipal'
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f2dc8367-1007-4938-bd23-fe263f013447')
  }
}

resource apiCognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, apiIdentity.id, 'cognitive-services-user')
  scope: speech
  properties: {
    principalType: 'ServicePrincipal'
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiAccountName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: chatDeploymentName
  sku: {
    name: chatDeploymentSku
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: chatModelName
      version: chatModelVersion
    }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: embeddingDeploymentName
  sku: {
    name: embeddingDeploymentSku
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
  }
  dependsOn: [
    chatDeployment
  ]
}

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechAccountName
  location: location
  tags: tags
  kind: 'SpeechServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: speechAccountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    sku: {
      family: 'A'
      name: 'standard'
    }
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource apiKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiIdentity.id, 'kv-secrets-user')
  scope: keyVault
  properties: {
    principalType: 'ServicePrincipal'
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource deployerKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, deployer().objectId, 'deployer-kv-secrets-user')
  scope: keyVault
  properties: {
    principalType: 'User'
    principalId: deployer().objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource postgresPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!useExistingHorizonDb) {
  parent: keyVault
  name: 'postgres-password'
  properties: {
    value: postgresPassword
  }
}

resource existingPostgresPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = if (useExistingHorizonDb) {
  parent: keyVault
  name: 'postgres-password'
}

resource horizonDbParamGroup 'Microsoft.HorizonDb/parameterGroups@2026-01-20-preview' = if (!useExistingHorizonDb) {
  name: horizonDbParamGroupName
  location: location
  tags: tags
  properties: {
    applyImmediately: true
    description: 'AI Callcenter extensions'
    pgVersion: int(postgresVersion)
    parameters: [
      {
        name: 'azure.extensions'
        value: 'vector,azure_ai'
      }
    ]
  }
}

resource horizonDbCluster 'Microsoft.HorizonDb/clusters@2026-01-20-preview' = if (!useExistingHorizonDb) {
  name: horizonDbClusterName
  location: location
  tags: tags
  properties: {
    administratorLogin: postgresAdmin
    administratorLoginPassword: postgresPassword
    createMode: 'Create'
    version: postgresVersion
    vCores: postgresVCores
    replicaCount: postgresReplicaCount
    zonePlacementPolicy: 'BestEffort'
    parameterGroup: {
      id: horizonDbParamGroup.id
      applyImmediately: true
    }
  }
}

resource existingHorizonDbCluster 'Microsoft.HorizonDb/clusters@2026-01-20-preview' existing = if (useExistingHorizonDb) {
  name: horizonDbClusterName
}

var postgresHost = useExistingHorizonDb
  ? (existingHorizonDbCluster.properties.?fullyQualifiedDomainName ?? '${horizonDbClusterName}.${location}.horizondb.azure.com')
  : (horizonDbCluster.properties.?fullyQualifiedDomainName ?? '${horizonDbClusterName}.${location}.horizondb.azure.com')
var openAiEndpoint = openAi.properties.endpoint
var speechEndpoint = speech.properties.endpoint
var postgresPasswordSecretUri = useExistingHorizonDb
  ? existingPostgresPasswordSecret.properties.secretUri
  : postgresPasswordSecret.properties.secretUri

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: union(tags, {
    'azd-service-name': apiServiceName
  })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: apiIdentity.id
        }
      ]
      secrets: [
        {
          name: 'postgres-password'
          keyVaultUrl: postgresPasswordSecretUri
          identity: apiIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: apiServiceName
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'POSTGRES_HOST', value: postgresHost }
            { name: 'POSTGRES_PORT', value: postgresPort }
            { name: 'POSTGRES_DATABASE', value: postgresDatabase }
            { name: 'POSTGRES_USERNAME', value: postgresAdmin }
            { name: 'POSTGRES_PASSWORD', secretRef: 'postgres-password' }
            { name: 'AI_EMBEDDING_MODEL_ALIAS', value: 'app-embedding' }
            { name: 'AI_RERANKER_MODEL_ALIAS', value: 'app-reranker' }
            { name: 'AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
            { name: 'AZURE_OPENAI_API_VERSION', value: openAiApiVersion }
            { name: 'AZURE_OPENAI_EMBED_DEPLOYMENT', value: embeddingDeploymentName }
            { name: 'AZURE_OPENAI_CHAT_DEPLOYMENT', value: chatDeploymentName }
            { name: 'AZURE_CLIENT_ID', value: apiIdentity.properties.clientId }
            { name: 'SPEECH_REGION', value: location }
            { name: 'SPEECH_ENDPOINT', value: speechEndpoint }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 20
              periodSeconds: 30
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
  dependsOn: [
    apiKeyVaultSecretsUser
    apiSpeechUser
    apiCognitiveServicesUser
    acrPull
  ]
}

output AZURE_LOCATION string = location
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.name
output AZURE_CONTAINER_ENVIRONMENT_NAME string = containerEnvironment.name
output AZURE_KEY_VAULT_NAME string = keyVault.name
output POSTGRES_HOST string = postgresHost
output POSTGRES_DATABASE string = postgresDatabase
output POSTGRES_USERNAME string = postgresAdmin
output HORIZONDB_CLUSTER_NAME string = horizonDbClusterName
output HORIZONDB_POOL_NAME string = 'pool1'
output HORIZONDB_PARAMETER_GROUP_NAME string = useExistingHorizonDb ? '' : horizonDbParamGroup.name
output AZURE_OPENAI_ENDPOINT string = openAiEndpoint
output AZURE_OPENAI_API_VERSION string = openAiApiVersion
output AZURE_OPENAI_EMBED_DEPLOYMENT string = embeddingDeploymentName
output AZURE_OPENAI_EMBED_MODEL string = embeddingModelName
output AZURE_OPENAI_CHAT_DEPLOYMENT string = chatDeploymentName
output AZURE_OPENAI_CHAT_MODEL string = chatModelName
output SPEECH_REGION string = location
output SPEECH_ENDPOINT string = speechEndpoint
output SERVICE_API_NAME string = apiApp.name
output SERVICE_API_URI string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
