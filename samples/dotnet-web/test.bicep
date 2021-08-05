@app('WebApp')
@uses(storageacc, 'Storage Blob Data Contributor')
@uses(kv, 'Key Vault Secrets User')
resource mySite 'Microsoft.Web/sites@2021-01-15' = {
  properties: {
    serverFarmId: appService.id
  }
}

resource appService 'Microsoft.Web/serverFarms@2020-06-01' = {
  sku: {
    name: 'F1'
  }
}

resource storageacc 'Microsoft.Storage/storageAccounts@2021-04-01' = {
  kind:'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
}

resource kv 'Microsoft.KeyVault/vaults@2021-04-01-preview' = {
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    tenantId: subscription().tenantId
  }
}
