Set objShell = CreateObject("WScript.Shell")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
psScript = scriptDir & "\Uninstall.ps1"
objShell.Run "powershell -NoProfile -STA -ExecutionPolicy Bypass -File """ & psScript & """", 0, False
