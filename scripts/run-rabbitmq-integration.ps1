param(
    [switch]$TestsOnly,
    [string]$Filter,
    [string]$Logger = 'console;verbosity=minimal'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'samples/docker-compose.yml'
$artifacts = Join-Path $repoRoot '.artifacts/rabbitmq-isolated'
$testVhost = 'sagaflow-tests-' + [Guid]::NewGuid().ToString('N')
$created = $false

if ([string]::IsNullOrWhiteSpace($env:SAGAFLOW_RABBITMQ_USERNAME) -or
    [string]::IsNullOrWhiteSpace($env:SAGAFLOW_RABBITMQ_PASSWORD)) {
    throw 'Set SAGAFLOW_RABBITMQ_USERNAME and SAGAFLOW_RABBITMQ_PASSWORD for the Compose broker.'
}

try {
    docker compose -f $composeFile up -d --wait rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'RabbitMQ Compose service did not become healthy.' }

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
    Remove-Item Env:SAGAFLOW_RABBITMQ_TEST_VHOST, Env:RabbitMq__VirtualHost, Env:SAGAFLOW_RABBITMQ_INTEGRATION, Env:SAGAFLOW_SAMPLE_RABBITMQ_INTEGRATION -ErrorAction SilentlyContinue
}
