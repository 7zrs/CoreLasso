@echo off
rem Relaunches CoreLasso elevated. You only need this if a game runs elevated
rem (most do not) or if "Access denied" shows up in the log.
powershell -NoProfile -Command "Start-Process -FilePath '%~dp0bin\CoreLasso.exe' -Verb RunAs"
