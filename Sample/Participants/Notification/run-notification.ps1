param(
    [string]$Python
)

$ErrorActionPreference = 'Stop'
$venv = Join-Path $PSScriptRoot '.venv'
$venvPython = Join-Path $venv 'Scripts/python.exe'

if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    $pythonArgs = @()
    if (-not $Python) {
        $launcher = Get-Command py -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($launcher) {
            $Python = $launcher.Source
            $pythonArgs = @('-3')
        }
        else {
            $interpreter = Get-Command python -CommandType Application -ErrorAction SilentlyContinue |
                Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\python.exe' } |
                Select-Object -First 1
            if (-not $interpreter) {
                throw 'Install Python 3 or pass its executable path with -Python.'
            }
            $Python = $interpreter.Source
        }
    }

    & $Python @pythonArgs -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Could not create the Python environment (exit $LASTEXITCODE)." }
}

& $venvPython -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw "Could not install Notification dependencies (exit $LASTEXITCODE)." }

& $venvPython -u (Join-Path $PSScriptRoot 'main.py')
if ($LASTEXITCODE -ne 0) { throw "Notification exited with code $LASTEXITCODE." }
