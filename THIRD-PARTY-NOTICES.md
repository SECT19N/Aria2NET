# Third-party notices

Aria2NET is free software under the GNU General Public License v3 (see `LICENSE.txt`). It ships with, or uses, the following third-party software and data.

## aria2

The download engine. Aria2NET starts the unmodified `aria2c.exe` as a separate process and talks to it over a local RPC connection.

- Project: https://aria2.github.io/ (source: https://github.com/aria2/aria2)
- License: GNU General Public License v2 or later, with an exception that allows linking with OpenSSL. The license text is in `tools\aria2\COPYING`, and the OpenSSL license in `tools\aria2\LICENSE.OpenSSL`, next to `aria2c.exe`.

## DB-IP IP to Country Lite database

Used offline to show a country code for the peers of a torrent. Lookups happen on your computer only.

- Source: https://db-ip.com/db/download/ip-to-country-lite
- License: Creative Commons Attribution 4.0 International (CC BY 4.0). "IP Geolocation by DB-IP".

## Public tracker list

The optional list of public BitTorrent trackers is downloaded when you use it (by default from https://github.com/ngosang/trackerslist). It is not shipped with the app. Its license is that of the list you choose.

## .NET libraries

| Package | License |
| --- | --- |
| Microsoft.WindowsAppSDK and Microsoft.Windows.SDK.BuildTools (WinUI 3) | MIT / Microsoft software license terms |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.DependencyInjection | MIT |
| Microsoft.Extensions.Logging and Microsoft.Extensions.Logging.Abstractions | MIT |
| Microsoft.Data.Sqlite (with SQLitePCLRaw and SQLite) | MIT / Apache-2.0 / public domain |
| System.Security.Cryptography.ProtectedData | MIT |
| .NET runtime (self-contained build) | MIT |

The full license text of each package is available on https://www.nuget.org/ under its name.
