@echo off
echo Building VPM...

REM Clean previous build
if exist .build rmdir /s /q .build
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

REM Publish
dotnet publish VPM.csproj --configuration Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=false -p:DebugType=none -o .build --nologo --verbosity normal

REM Copy configuration files
if exist urls.csv copy /Y urls.csv .build\
if exist urls.json copy /Y urls.json .build\
if exist VPM.json copy /Y VPM.json .build\
if exist _links\VPM.bin copy /Y _links\VPM.bin .build\

REM Clean up extracted native DLLs from output folder
del /q .build\*.dll 2>nul
del /q .build\Microsoft.Web.WebView2.*.xml 2>nul

echo.
echo Build complete in .build folder.
pause
