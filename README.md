# Play.Trading
Play Economy Play.Trading microservice.

## Build the docker image
```powershell
$version="1.0.1"
$env:GH_OWNER="jeevdotnetmicroservice"
$env:GH_PAT="[PAT HERE]"
docker build --secret id=GH_OWNER --secret id=GH_PAT -t play.trading:$version .
```

## Run the docker image
$cosmosDbConnString="[CONN STRING HERE]"
$serviceBusConnString="[CONN STRING HERE]"

```powershell
docker run -it --rm -p 5006:5006 --name trading -e MongoDbSettings__ConnectionString=$cosmosDbConnString -e ServiceBusSettings__ConnectionString=$serviceBusConnString -e ServiceSettings__MessageBroker="SERVICEBUS" play.trading:$version
```