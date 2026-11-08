' 3MF Library Opener: registered for threemflib:// links. Starts open-model.ps1 without showing a console window.
Option Explicit

Dim shell, folder, url, command
If WScript.Arguments.Count < 1 Then WScript.Quit 2

Set shell = CreateObject("WScript.Shell")
folder = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)

' The link travels in an environment variable, not on the command line: Run expands %NAME% sequences, and a
' percent-encoded link is full of % signs.
url = WScript.Arguments(0)
shell.Environment("Process")("THREEMFLIB_URL") = url

command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & _
          folder & "\open-model.ps1"""
shell.Run command, 0, False
