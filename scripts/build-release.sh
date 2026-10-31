#!/usr/bin/env bash
set -euo pipefail

dotnet test KubicEmu.sln --configuration Release
dotnet publish src/KubicEmu.App/KubicEmu.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/release/app
zip -j -q -FS artifacts/KubicEmu-win-x64.zip artifacts/release/app/*.dll artifacts/release/app/KubicEmu.exe README.md
dotnet publish src/KubicEmu.Setup/KubicEmu.Setup.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/release/setup
cp artifacts/KubicEmu-win-x64.zip artifacts/release/setup/KubicEmu-win-x64.zip
zip -j -q -FS artifacts/KubicEmu-Setup-win-x64.zip artifacts/release/setup/KubicEmu.Setup.exe artifacts/release/setup/KubicEmu-win-x64.zip