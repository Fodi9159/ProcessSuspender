@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found. Please install .NET Framework 4.x.
    exit /b 1
)
set "UA=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll"
set "UAT=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll"
set "WB=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
"%CSC%" /nologo /target:winexe /optimize+ /out:ProcessSuspender.exe /win32icon:appicon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:"%UA%" /r:"%UAT%" /r:"%WB%" ProcessSuspender.cs Settings.cs TrayApp.cs SettingsForm.cs Hotkeys.cs InputHook.cs Toast.cs
if %errorlevel%==0 (
    echo Built ProcessSuspender.exe
) else (
    echo Build FAILED
    exit /b 1
)
