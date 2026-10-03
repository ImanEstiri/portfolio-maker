@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@description('Principal ID of the CvMaker service principal to grant Azure AI Developer role.')
param principalId string

@description('The endpoint URL of the existing Azure OpenAI resource (e.g. https://<name>.openai.azure.com/).')
param openAiEndpoint string

@description('The resource ID of the existing Azure OpenAI resource.')
param openAiResourceId string

// ============================================================================
// AZURE AI FOUNDRY HUB
// The hub is the top-level workspace that holds connections, compute, and
// shared configuration for all child projects.
// ============================================================================
resource aiHub 'Microsoft.MachineLearningServices/workspaces@2024-07-01-preview' = {
  name: take('hub-${uniqueString(resourceGroup().id)}', 33)
  location: location
  kind: 'Hub'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    friendlyName: 'CvMaker AI Hub'
    publicNetworkAccess: 'Enabled'
  }
}

// Grant the Hub's system-assigned identity "Cognitive Services OpenAI Contributor" on the OpenAI resource.
// Some Agents operations (for example assistant creation routed through the project/hub connection)
// require assistants/write data actions, which are not covered by the OpenAI User role alone.
// Role ID: a001fd3d-188f-4b5d-821b-7da978bf7442
resource hubOpenAiContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiResourceId, aiHub.id, 'a001fd3d-188f-4b5d-821b-7da978bf7442')
  scope: openAiExisting
  properties: {
    principalId: aiHub.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a001fd3d-188f-4b5d-821b-7da978bf7442' // Cognitive Services OpenAI Contributor
    )
  }
}

// Reference the existing OpenAI resource so we can assign roles to it from this module.
resource openAiExisting 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: last(split(openAiResourceId, '/'))
}

// Grant the Hub's system-assigned identity "Cognitive Services OpenAI User" on the OpenAI resource.
// Required for authType: 'AAD' connections — the Hub authenticates as itself when proxying requests.
// Role ID: 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd
resource hubOpenAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiResourceId, aiHub.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openAiExisting
  properties: {
    principalId: aiHub.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
    )
  }
}

// Connect the existing Azure OpenAI resource to the hub so projects can reach it
// via Managed Identity without storing API keys.
resource openAiConnection 'Microsoft.MachineLearningServices/workspaces/connections@2024-07-01-preview' = {
  parent: aiHub
  name: 'azure-openai'
  properties: {
    category: 'AzureOpenAI'
    target: openAiEndpoint
    authType: 'AAD'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
      ResourceId: openAiResourceId
    }
  }
  dependsOn: [hubOpenAiRoleAssignment]
}

// ============================================================================
// AZURE AI FOUNDRY PROJECT
// A project is a child workspace scoped to a product area. CvMaker.Agentic's
// FoundryAgentRuntime connects to this project and creates its own Agent
// resources lazily on first invocation (Ai/FoundryAgentRuntime.cs) — nothing
// further needs provisioning here.
// ============================================================================
resource aiProject 'Microsoft.MachineLearningServices/workspaces@2024-07-01-preview' = {
  name: take('project-${uniqueString(resourceGroup().id)}', 33)
  location: location
  kind: 'Project'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    friendlyName: 'CvMaker Project'
    hubResourceId: aiHub.id
    publicNetworkAccess: 'Enabled'
  }
  dependsOn: [openAiConnection]
}

// Grant the Project's system-assigned identity OpenAI access as well.
// In practice, Foundry project-backed agent operations can surface the project identity in authorization
// errors, so both the project and hub identities must be able to perform data-plane OpenAI calls.
resource projectOpenAiUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiResourceId, aiProject.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openAiExisting
  properties: {
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
    )
  }
}

resource projectOpenAiContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiResourceId, aiProject.id, 'a001fd3d-188f-4b5d-821b-7da978bf7442')
  scope: openAiExisting
  properties: {
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a001fd3d-188f-4b5d-821b-7da978bf7442' // Cognitive Services OpenAI Contributor
    )
  }
}

// Grant "Cognitive Services OpenAI Contributor" to the CvMaker service principal
// on the OpenAI resource. Required for Assistants API data-plane actions
// (create/delete agents, threads, runs) — "Azure AI Developer" alone is insufficient.
// Role ID: a001fd3d-188f-4b5d-821b-7da978bf7442
resource agenticOpenAiContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiResourceId, principalId, 'a001fd3d-188f-4b5d-821b-7da978bf7442')
  scope: openAiExisting
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a001fd3d-188f-4b5d-821b-7da978bf7442' // Cognitive Services OpenAI Contributor
    )
  }
}

// Grant "Azure AI Developer" role to the CvMaker service principal on the project.
// This role allows creating/running/deleting agents and threads.
// Role ID: 64702f94-c441-49e6-a78b-ef80e0188fee
resource aiDeveloperRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, principalId, '64702f94-c441-49e6-a78b-ef80e0188fee')
  scope: aiProject
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '64702f94-c441-49e6-a78b-ef80e0188fee' // Azure AI Developer
    )
  }
}

// The discoveryUrl is the shared regional endpoint (e.g. "https://eastus2.api.azureml.ms/discovery").
// Strip "/discovery" to get the base host URL used to construct the agents endpoint.
var discoveryBase = replace(aiProject.properties.discoveryUrl, '/discovery', '')

// PersistentAgentsClient (Azure.AI.Agents.Persistent, used by FoundryAgentRuntime) takes the
// full agents endpoint URI and appends /assistants, /threads, etc. to it:
//   {discoveryBase}/agents/v1.0/subscriptions/{sub}/resourceGroups/{rg}/providers/
//       Microsoft.MachineLearningServices/workspaces/{workspace}
// This matches the agentsEndpointUri property exposed by the workspace resource, and is what
// AzureAIFoundry:Endpoint (flyio/agentic.fly.toml) must be set to.
var agentsEndpointUri = '${discoveryBase}/agents/v1.0/subscriptions/${subscription().subscriptionId}/resourceGroups/${resourceGroup().name}/providers/Microsoft.MachineLearningServices/workspaces/${aiProject.name}'

@description('Full agents endpoint URI for PersistentAgentsClient. Set as AzureAIFoundry:Endpoint.')
output projectEndpoint string = agentsEndpointUri

@description('The name of the AI Foundry project workspace.')
output projectName string = aiProject.name
