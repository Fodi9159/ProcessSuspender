@echo off
setlocal
rem Prefer modern SDK build when available (works with .NET Framework reference assemblies).
where dotnet >nul 2>nul
if %errorlevel%==0 (
    dotnet build ProcessSuspender.csproj -c Release --nologo -v q
    if %errorlevel%==0 (
        copy /y "bin\Release\net48\ProcessSuspender.exe" "ProcessSuspender.exe" >nul
        echo Built ProcessSuspender.exe via dotnet.
        exit /b 0
    )
    echo dotnet build failed, falling back to csc.exe...
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found. Please install .NET Framework 4.x or .NET SDK.
    exit /b 1
)
set "UA=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll"
set "UAT=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll"
set "WB=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
if not exist "%UA%" (
    echo UIA assemblies not found at "%UA%".
    echo This happens on machines without .NET Framework 4.x GAC - install it or use: dotnet build
    exit /b 1
)
rem Build to temp first so a running ProcessSuspender.exe does not lock the build.
set "TMPOUT=%TEMP%\ProcessSuspender_build.exe"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%TMPOUT%" /win32icon:appicon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:"%UA%" /r:"%UAT%" /r:"%WB%" ProcessSuspender.cs Settings.cs TrayApp.cs SettingsForm.cs Hotkeys.cs InputHook.cs Toast.cs Theme.cs
if %errorlevel%==0 (
    copy /y "%TMPOUT%" "ProcessSuspender.exe" >nul
    if %errorlevel%==0 (
        echo Built ProcessSuspender.exe
    ) else (
        echo Built "%TMPOUT%" but could not overwrite ProcessSuspender.exe - close the running app and copy it manually.
    )
) else (
    echo Build FAILED
    exit /b 1
)
