# Host Windows sessions in ConPTY-managed child shells

The first release targets Windows 10 version 1809 or later and runs a nested PowerShell (matching the invoking PowerShell version) or Command Prompt shell inside a Windows pseudoconsole (ConPTY), rather than trying to proxy the shell that launched Hitch. A small P/Invoke adapter around the official ConPTY APIs keeps native terminal mechanics behind a narrow boundary. The initial distributable is a self-contained .NET 10 win-x64 executable, with ARM64 and other operating systems deferred.
