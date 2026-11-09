param(
    [Parameter(Mandatory)]
    [ValidateSet('Order', 'Return', 'Procurement')]
    [string]$Domain
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'samples/docker-compose.yml'
$domainName = (Get-Culture).TextInfo.ToTitleCase($Domain.ToLowerInvariant())
$database = 'sagaflow_' + $domainName.ToLowerInvariant()
$projectName = "SagaFlow.$domainName"
$projectDirectory = Join-Path $repoRoot "samples/${domainName}Process/$projectName"
$profile = if ($domainName -eq 'Order') { 'SagaFlow.Order.Domain' } else { $projectName }

$composeJson = docker compose -f $composeFile config --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the sample Compose configuration.' }
$compose = $composeJson | Out-String | ConvertFrom-Json
$passwordKey = 'SAGAFLOW_POSTGRES_' + $domainName.ToUpperInvariant() + '_PASSWORD'
$password = $compose.services.postgres.environment.$passwordKey

docker compose -f $composeFile up -d --wait rabbitmq postgres
if ($LASTEXITCODE -ne 0) { throw 'Sample RabbitMQ or PostgreSQL did not become healthy.' }
$endpoint = docker compose -f $composeFile port postgres 5432
if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^127\.0\.0\.1:(\d+)$') {
    throw 'Could not resolve the sample PostgreSQL loopback port.'
}

$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host'] = '127.0.0.1'
$connection['Port'] = $Matches[1]
$connection['Database'] = $database
$connection['Username'] = $database
$connection['Password'] = $password

$previousProvider = $env:SagaFlow__Storage__Provider
$previousConnection = $env:SagaFlow__Storage__ConnectionString
Push-Location $projectDirectory
try {
    $env:SagaFlow__Storage__Provider = 'PostgreSQL'
    $env:SagaFlow__Storage__ConnectionString = $connection.ConnectionString
    dotnet run --project "$projectName.csproj" --launch-profile $profile
    if ($LASTEXITCODE -ne 0) { throw "$projectName exited with code $LASTEXITCODE." }
}
finally {
    $env:SagaFlow__Storage__Provider = $previousProvider
    $env:SagaFlow__Storage__ConnectionString = $previousConnection
    Pop-Location
}
