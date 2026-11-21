param(
    [switch]$TestsOnly,
    [string]$Filter,
    [string]$Logger = 'console;verbosity=minimal'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'Sample/compose.yaml'
$artifacts = Join-Path $repoRoot '.artifacts/rabbitmq-isolated'
$testVhost = 'sagaflow-tests-' + [Guid]::NewGuid().ToString('N')
$created = $false
$environmentKeys = @('SAGAFLOW_RABBITMQ_USERNAME', 'SAGAFLOW_RABBITMQ_PASSWORD', 'SAGAFLOW_RABBITMQ_HOST',
    'SAGAFLOW_RABBITMQ_PORT', 'SAGAFLOW_RABBITMQ_TEST_VHOST', 'SAGAFLOW_RABBITMQ_INTEGRATION',
    'SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION', 'SAGAFLOW_POSTGRESQL_TEST_CONNECTION', 'RabbitMq__VirtualHost',
    'RabbitMq__UserName', 'RabbitMq__Password', 'RabbitMq__HostName', 'ConnectionStrings__RabbitMq')
$previousEnvironment = @{}
foreach ($key in $environmentKeys) { $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key) }

try {
    $config = docker compose -f $composeFile config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Sample Compose configuration.' }
    $compose = $config | Out-String | ConvertFrom-Json
    if (-not $env:SAGAFLOW_RABBITMQ_USERNAME) { $env:SAGAFLOW_RABBITMQ_USERNAME = $compose.services.rabbitmq.environment.RABBITMQ_DEFAULT_USER }
    if (-not $env:SAGAFLOW_RABBITMQ_PASSWORD) { $env:SAGAFLOW_RABBITMQ_PASSWORD = $compose.services.rabbitmq.environment.RABBITMQ_DEFAULT_PASS }
    if (-not $env:SAGAFLOW_RABBITMQ_HOST) { $env:SAGAFLOW_RABBITMQ_HOST = '127.0.0.1' }
    if (-not $env:SAGAFLOW_RABBITMQ_PORT) {
        $env:SAGAFLOW_RABBITMQ_PORT = ($compose.services.rabbitmq.ports | Where-Object target -eq 5672).published
    }
    if (-not $env:SAGAFLOW_POSTGRESQL_TEST_CONNECTION) {
        $postgres = [System.Data.Common.DbConnectionStringBuilder]::new()
        $postgres['Host'] = '127.0.0.1'
        $postgres['Port'] = ($compose.services.postgres.ports | Where-Object target -eq 5432).published
        $postgres['Database'] = 'postgres'
        $postgres['Username'] = 'postgres'
        $postgres['Password'] = $compose.services.postgres.environment.POSTGRES_PASSWORD
        $env:SAGAFLOW_POSTGRESQL_TEST_CONNECTION = $postgres.ConnectionString
    }

    docker compose -f $composeFile up -d --wait rabbitmq postgres
    if ($LASTEXITCODE -ne 0) { throw 'Sample RabbitMQ or PostgreSQL did not become healthy.' }

    docker compose -f $composeFile exec -T rabbitmq rabbitmqctl add_vhost $testVhost
    if ($LASTEXITCODE -ne 0) { throw "Could not create test vhost $testVhost." }
    $created = $true
    docker compose -f $composeFile exec -T rabbitmq rabbitmqctl set_permissions -p $testVhost $env:SAGAFLOW_RABBITMQ_USERNAME '.*' '.*' '.*'
    if ($LASTEXITCODE -ne 0) { throw "Could not grant test vhost permissions to $($env:SAGAFLOW_RABBITMQ_USERNAME)." }

    $env:SAGAFLOW_RABBITMQ_TEST_VHOST = $testVhost
    $env:RabbitMq__VirtualHost = $testVhost
    $env:RabbitMq__UserName = $env:SAGAFLOW_RABBITMQ_USERNAME
    $env:RabbitMq__Password = $env:SAGAFLOW_RABBITMQ_PASSWORD
    $env:RabbitMq__HostName = if ($env:SAGAFLOW_RABBITMQ_HOST) { $env:SAGAFLOW_RABBITMQ_HOST } else { 'localhost' }
    $rabbitUser = [Uri]::EscapeDataString($env:RabbitMq__UserName)
    $rabbitPassword = [Uri]::EscapeDataString($env:RabbitMq__Password)
    $rabbitPort = [int]$env:SAGAFLOW_RABBITMQ_PORT
    $env:ConnectionStrings__RabbitMq = [UriBuilder]::new('amqp', $env:RabbitMq__HostName, $rabbitPort, $testVhost).Uri.AbsoluteUri.Replace('amqp://', "amqp://${rabbitUser}:${rabbitPassword}@")
    $env:SAGAFLOW_RABBITMQ_INTEGRATION = 'true'
    $env:SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION = 'true'

    Push-Location $repoRoot
    try {
        if (-not $TestsOnly) {
            dotnet build SagaFlow.slnx --artifacts-path $artifacts --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw 'Full solution build failed.' }
        }
        $testArgs = @('test', 'SagaFlow.slnx', '--artifacts-path', $artifacts, '--no-build', '--no-restore', '--logger', $Logger, '-m:1')
        if ($Filter) { $testArgs += @('--filter', $Filter) }
        dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Full solution tests failed.' }
    }
    finally { Pop-Location }
}
finally {
    if ($created) {
        docker compose -f $composeFile exec -T rabbitmq rabbitmqctl delete_vhost $testVhost
        if ($LASTEXITCODE -ne 0) { Write-Warning "Test vhost $testVhost requires manual removal." }
    }
    foreach ($key in $environmentKeys) { [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key]) }
}
