@echo off
echo Building PrintStation...
dotnet build --no-restore -q
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)
echo Build OK

echo Launching BH instance...
start "PrintStation BH" dotnet run --project src\HitePhoto.PrintStation --no-build -- --profile BH

echo Launching WB instance...
start "PrintStation WB" dotnet run --project src\HitePhoto.PrintStation --no-build -- --profile WB

echo.
echo Both instances launching. Close this window when done.
pause
