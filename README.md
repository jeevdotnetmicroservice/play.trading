# Play.Trading
Play Economy Play.Trading microservice.

## Build the docker image
```powershell
$version="1.0.2"
$rpname="jeevplayeconomy"
$env:GH_OWNER="jeevdotnetmicroservice"
$env:GH_PAT="[PAT HERE]"
docker build --secret id=GH_OWNER --secret id=GH_PAT -t "$rpname.azurecr.io/play.trading:$version" .
```

## Run the docker image
$cosmosDbConnString="[CONN STRING HERE]"
$serviceBusConnString="[CONN STRING HERE]"

```powershell
docker run -it --rm -p 5006:5006 --name trading -e MongoDbSettings__ConnectionString=$cosmosDbConnString -e ServiceBusSettings__ConnectionString=$serviceBusConnString -e ServiceSettings__MessageBroker="SERVICEBUS" play.trading:$version
```

## Publishing the Docker image
```powershell
az acr login --name $rpname
docker push "$rpname.azurecr.io/play.trading:$version"
```

## Create the kubernetes namespace
```powershell
$namespace="trading"
kubectl create namespace $namespace
```

### Creating the pod managed identity
```powershell
$rgname="playeconomy"
az identity create --resource-group $rgname --name $namespace
$IDENTITY_RESOURCE_ID=az identity show -g $rgname -n $namespace --query id -otsv

az aks pod-identity add --resource-group $rgname --cluster-name $rpname --namespace $namespace --name $namespace --identity-resource-id $IDENTITY_RESOURCE_ID
```

## Granting access to Key Vault secrets
```powershell
$IDENTITY_CLIENT_ID=az identity show -g $rgname -n $namespace --query clientId -otsv
az keyvault set-policy -n $rpname --secret-permissions get list --spn $IDENTITY_CLIENT_ID
```

## Create the Kubernetes pod
```poswershell
kubectl apply -f .\kubernetes\trading.yaml -n $namespace
```

## Install the Helm Chart
```powershell
$helmUser=[guid]::Empty.Guid
$helmPassword = az acr login --name $rpname --expose-token --output tsv --query accessToken

$env:HELM_EXPERIMENTAL_OCI=1
helm registry login "$rpname.azurecr.io" --username $helmUser --password $helmPassword

$chartVersion="0.1.0"
helm upgrade trading-service oci://$rpname.azurecr.io/helm/microservice --version $chartVersion -f .\helm\values.yaml -n $namespace --install
```

## Required repository secrets for GitHub workflow
GH_PAT: Created in GitHub user profile --> Settings --> Developer settings --> Personal access token
AZURE_CLIENT_ID: From AAD App Registration
AZURE_SUBSCRIPTION_ID: From Azure Portal subscription
AZURE_TENAN_ID: From Azure AAD properties page